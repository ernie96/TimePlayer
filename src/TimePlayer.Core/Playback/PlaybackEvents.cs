using TimePlayer.Core.Models;

namespace TimePlayer.Core.Playback;

public sealed class PlaybackStateChangedEventArgs : EventArgs
{
    public PlaybackState OldState { get; }
    public PlaybackState NewState { get; }

    public PlaybackStateChangedEventArgs(PlaybackState oldState, PlaybackState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}

public sealed class PlaybackPositionChangedEventArgs : EventArgs
{
    public TimeSpan Position { get; }
    public TimeSpan Duration { get; }

    public PlaybackPositionChangedEventArgs(TimeSpan position, TimeSpan duration)
    {
        Position = position;
        Duration = duration;
    }
}

public sealed class PlaybackErrorEventArgs : EventArgs
{
    public string Message { get; }
    public Exception? Exception { get; }

    public PlaybackErrorEventArgs(string message, Exception? exception = null)
    {
        Message = message;
        Exception = exception;
    }
}
