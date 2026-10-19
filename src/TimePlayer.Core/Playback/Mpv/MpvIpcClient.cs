using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TimePlayer.Core.Playback.Mpv;

/// <summary>
/// Asynchronous Named Pipe client communicating with mpv's JSON IPC protocol over local pipes.
/// </summary>
public sealed class MpvIpcClient : IMpvIpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<MpvIpcClient> _logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<MpvResponse>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private NamedPipeClientStream? _pipeClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _disposeCts;
    private Task? _readLoopTask;
    private int _requestIdCounter;

    public bool IsConnected => _pipeClient?.IsConnected ?? false;

    public event EventHandler<MpvEvent>? EventReceived;
    public event EventHandler<MpvPropertyChangeEventArgs>? PropertyChanged;
    public event EventHandler? Disconnected;

    public MpvIpcClient(ILogger<MpvIpcClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// After mpv restarts, the old pipe, its reader and writer and its read loop would otherwise stay
    /// alive next to the new ones.
    /// </summary>
    private async Task CloseConnectionAsync()
    {
        var oldCts = _disposeCts;
        var oldLoop = _readLoopTask;
        if (oldCts is null && _pipeClient is null)
            return;
        try { oldCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _writer?.Dispose(); } catch (Exception) { }
        try { _reader?.Dispose(); } catch (Exception) { }
        try { _pipeClient?.Dispose(); } catch (Exception) { }
        if (oldLoop is not null)
        {
            try { await oldLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        }
        oldCts?.Dispose();
        _writer = null;
        _reader = null;
        _pipeClient = null;
        _disposeCts = null;
        _readLoopTask = null;
    }

    public async Task ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _logger.LogInformation("Connecting to local mpv named pipe: {Pipe}", pipeName);
        await CloseConnectionAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(100);

        while (stopwatch.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            // Enforce strictly local computer connection using "."
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                var remainingMs = (int)Math.Clamp((timeout - stopwatch.Elapsed).TotalMilliseconds, 1, 1000);
                await pipe.ConnectAsync(remainingMs, cancellationToken);

                _pipeClient = pipe;
                _reader = new StreamReader(pipe, Encoding.UTF8);
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                _disposeCts = new CancellationTokenSource();
                _readLoopTask = Task.Run(() => ReadLoopAsync(_disposeCts.Token), _disposeCts.Token);

                _logger.LogInformation("Successfully connected to mpv named pipe in {Elapsed}ms.", stopwatch.ElapsedMilliseconds);
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // Pipe may not have been created yet by mpv; wait and retry
                await pipe.DisposeAsync();
                await Task.Delay(delay, cancellationToken);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        throw new TimeoutException($"Failed to connect to mpv named pipe '{pipeName}' within {timeout.TotalSeconds} seconds.");
    }

    public async Task<MpvResponse> SendCommandAsync(IReadOnlyList<object> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!IsConnected || _writer == null)
        {
            throw new InvalidOperationException("Not connected to mpv named pipe.");
        }

        var requestId = Interlocked.Increment(ref _requestIdCounter);
        var tcs = new TaskCompletionSource<MpvResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        var request = new MpvCommandRequest
        {
            Command = command,
            RequestId = requestId
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(15)); // request timeout

        await _writeLock.WaitAsync(linkedCts.Token);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), linkedCts.Token);
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }

        using (linkedCts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(requestId, out var pending))
            {
                pending.TrySetCanceled();
            }
        }))
        {
            return await tcs.Task;
        }
    }

    public async Task<bool> SetPropertyAsync(string propertyName, object value, CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(new object[] { "set_property", propertyName, value }, cancellationToken);
        return response.IsSuccess;
    }

    public async Task<T?> GetPropertyAsync<T>(string propertyName, CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(new object[] { "get_property", propertyName }, cancellationToken);
        if (!response.IsSuccess || response.Data == null)
            return default;

        try
        {
            return response.Data.Value.Deserialize<T>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize property {Name} into {Type}.", propertyName, typeof(T).Name);
            return default;
        }
    }

    public async Task<bool> ObservePropertyAsync(int propertyId, string propertyName, CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(new object[] { "observe_property", propertyId, propertyName }, cancellationToken);
        return response.IsSuccess;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    _logger.LogInformation("EOF reached on mpv named pipe.");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                ProcessIncomingLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception in mpv IPC read loop.");
        }
        finally
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
            CancelPendingRequests();
        }
    }

    private void ProcessIncomingLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Check if it is a command response (contains request_id)
            if (root.TryGetProperty("request_id", out var reqProp) && reqProp.ValueKind == JsonValueKind.Number)
            {
                var requestId = reqProp.GetInt32();
                if (_pendingRequests.TryRemove(requestId, out var tcs))
                {
                    var response = JsonSerializer.Deserialize<MpvResponse>(line, JsonOptions);
                    if (response != null)
                    {
                        tcs.TrySetResult(response);
                    }
                    else
                    {
                        tcs.TrySetException(new InvalidOperationException("Failed to deserialize mpv response."));
                    }
                }
                return;
            }

            // Check if it is an event (contains "event")
            if (root.TryGetProperty("event", out var eventProp) && eventProp.ValueKind == JsonValueKind.String)
            {
                var eventName = eventProp.GetString();

                if (string.Equals(eventName, "property-change", StringComparison.OrdinalIgnoreCase))
                {
                    var id = root.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                    var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    var data = root.TryGetProperty("data", out var dataProp) ? dataProp.Clone() : (JsonElement?)null;

                    PropertyChanged?.Invoke(this, new MpvPropertyChangeEventArgs(id, name, data));
                }
                else
                {
                    var mpvEvent = JsonSerializer.Deserialize<MpvEvent>(line, JsonOptions);
                    if (mpvEvent != null)
                    {
                        EventReceived?.Invoke(this, mpvEvent);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse incoming mpv line: {Line}", line);
        }
    }

    private void CancelPendingRequests()
    {
        foreach (var (id, tcs) in _pendingRequests)
        {
            tcs.TrySetCanceled();
        }
        _pendingRequests.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts?.Cancel();

        CancelPendingRequests();

        if (_readLoopTask != null)
        {
            try { await _readLoopTask; } catch { /* ignore */ }
        }

        _reader?.Dispose();
        if (_writer != null)
        {
            await _writer.DisposeAsync();
        }
        if (_pipeClient != null)
        {
            await _pipeClient.DisposeAsync();
        }

        _disposeCts?.Dispose();
        _writeLock.Dispose();
    }
}
