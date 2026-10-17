using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimePlayer.Core.Playback.Mpv;

/// <summary>
/// Outgoing command sent over the mpv JSON IPC named pipe.
/// </summary>
public record MpvCommandRequest
{
    [JsonPropertyName("command")]
    public required IReadOnlyList<object> Command { get; init; }

    [JsonPropertyName("request_id")]
    public int RequestId { get; init; }
}

/// <summary>
/// Response received from mpv following a command execution.
/// </summary>
public record MpvResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("request_id")]
    public int? RequestId { get; init; }

    [JsonIgnore]
    public bool IsSuccess => string.Equals(Error, "success", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Asynchronous event broadcast by mpv over the JSON IPC channel.
/// </summary>
public record MpvEvent
{
    [JsonPropertyName("event")]
    public string? EventName { get; init; }

    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// Event arguments for property change notifications from mpv.
/// </summary>
public sealed class MpvPropertyChangeEventArgs : EventArgs
{
    public int PropertyId { get; }
    public string PropertyName { get; }
    public JsonElement? Value { get; }

    public MpvPropertyChangeEventArgs(int propertyId, string propertyName, JsonElement? value)
    {
        PropertyId = propertyId;
        PropertyName = propertyName;
        Value = value;
    }
}
