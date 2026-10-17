namespace TimePlayer.Core.Playback.Mpv;

/// <summary>
/// Asynchronous client contract for communication with the mpv media player over local Windows Named Pipes.
/// </summary>
public interface IMpvIpcClient : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the client is actively connected to the mpv named pipe.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Event raised when mpv emits a playback lifecycle event (file-loaded, end-file, etc.).
    /// </summary>
    event EventHandler<MpvEvent>? EventReceived;

    /// <summary>
    /// Event raised when an observed mpv property value changes (time-pos, pause, duration, etc.).
    /// </summary>
    event EventHandler<MpvPropertyChangeEventArgs>? PropertyChanged;

    /// <summary>
    /// Event raised when the named pipe connection is disconnected or closed.
    /// </summary>
    event EventHandler? Disconnected;

    /// <summary>
    /// Connects to a strictly local named pipe exposed by the mpv process.
    /// </summary>
    Task ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a low-level command array to mpv and awaits the correlated response.
    /// </summary>
    Task<MpvResponse> SendCommandAsync(IReadOnlyList<object> command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets an mpv property value via the set_property command.
    /// </summary>
    Task<bool> SetPropertyAsync(string propertyName, object value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current value of an mpv property via the get_property command.
    /// </summary>
    Task<T?> GetPropertyAsync<T>(string propertyName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an observed property with mpv to receive automatic change notifications.
    /// </summary>
    Task<bool> ObservePropertyAsync(int propertyId, string propertyName, CancellationToken cancellationToken = default);
}
