using System.Text.Json;
using TimePlayer.Core.Playback.Mpv;

namespace TimePlayer.Tests.Helpers;

public class FakeMpvIpcClient : IMpvIpcClient
{
    public bool IsConnected { get; set; } = true;
    public List<IReadOnlyList<object>> SentCommands { get; } = new();
    public Dictionary<string, object> Properties { get; } = new();

    public event EventHandler<MpvEvent>? EventReceived;
    public event EventHandler<MpvPropertyChangeEventArgs>? PropertyChanged;
    public event EventHandler? Disconnected;

    public void RaiseEvent(string eventName, string? reason = null, string? error = null, long? entryId = null)
    {
        EventReceived?.Invoke(this, new MpvEvent
        {
            EventName = eventName,
            Reason = reason,
            Error = error,
            PlaylistEntryId = entryId
        });
    }

    public void RaisePropertyChange(int id, string name, JsonElement data)
    {
        PropertyChanged?.Invoke(this, new MpvPropertyChangeEventArgs(id, name, data));
    }

    public void RaiseDisconnected()
    {
        IsConnected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public Task ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<MpvResponse> SendCommandAsync(IReadOnlyList<object> command, CancellationToken cancellationToken = default)
    {
        SentCommands.Add(command);
        return Task.FromResult(new MpvResponse { Error = "success" });
    }

    public Task<bool> SetPropertyAsync(string propertyName, object value, CancellationToken cancellationToken = default)
    {
        Properties[propertyName] = value;
        SentCommands.Add(new object[] { "set_property", propertyName, value });
        return Task.FromResult(true);
    }

    public Task<T?> GetPropertyAsync<T>(string propertyName, CancellationToken cancellationToken = default)
    {
        if (Properties.TryGetValue(propertyName, out var val) && val is T typed)
            return Task.FromResult<T?>(typed);
        return Task.FromResult<T?>(default);
    }

    public Task<bool> ObservePropertyAsync(int propertyId, string propertyName, CancellationToken cancellationToken = default)
    {
        SentCommands.Add(new object[] { "observe_property", propertyId, propertyName });
        return Task.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
