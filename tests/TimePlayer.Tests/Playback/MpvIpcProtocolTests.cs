using System.Text.Json;
using TimePlayer.Core.Playback.Mpv;
using Xunit;

namespace TimePlayer.Tests.Playback;

public class MpvIpcProtocolTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Serialize_CommandRequest_GeneratesExpectedJson()
    {
        var request = new MpvCommandRequest
        {
            Command = new object[] { "loadfile", "https://example.com/stream.mp4", "replace" },
            RequestId = 101
        };

        var json = JsonSerializer.Serialize(request, Options);

        Assert.Contains("\"command\":[\"loadfile\",\"https://example.com/stream.mp4\",\"replace\"]", json);
        Assert.Contains("\"request_id\":101", json);
    }

    [Fact]
    public void Deserialize_SuccessResponse_ParsesCorrectly()
    {
        var json = """
        {
            "error": "success",
            "data": 120.5,
            "request_id": 101
        }
        """;

        var response = JsonSerializer.Deserialize<MpvResponse>(json, Options);

        Assert.NotNull(response);
        Assert.True(response.IsSuccess);
        Assert.Equal(101, response.RequestId);
        Assert.Equal(120.5, response.Data!.Value.GetDouble());
    }

    [Fact]
    public void Deserialize_ErrorResponse_ParsesCorrectly()
    {
        var json = """
        {
            "error": "property not found",
            "request_id": 102
        }
        """;

        var response = JsonSerializer.Deserialize<MpvResponse>(json, Options);

        Assert.NotNull(response);
        Assert.False(response.IsSuccess);
        Assert.Equal("property not found", response.Error);
        Assert.Equal(102, response.RequestId);
    }

    [Fact]
    public void Deserialize_PropertyChangeEvent_ParsesCorrectly()
    {
        var json = """
        {
            "event": "property-change",
            "id": 1,
            "name": "pause",
            "data": true
        }
        """;

        var mpvEvent = JsonSerializer.Deserialize<MpvEvent>(json, Options);

        Assert.NotNull(mpvEvent);
        Assert.Equal("property-change", mpvEvent.EventName);
        Assert.Equal(1, mpvEvent.Id);
        Assert.Equal("pause", mpvEvent.Name);
        Assert.True(mpvEvent.Data!.Value.GetBoolean());
    }

    [Fact]
    public void Deserialize_EndFileEvent_ParsesReasonAndError()
    {
        var json = """
        {
            "event": "end-file",
            "reason": "error",
            "error": "Failed to open stream"
        }
        """;

        var mpvEvent = JsonSerializer.Deserialize<MpvEvent>(json, Options);

        Assert.NotNull(mpvEvent);
        Assert.Equal("end-file", mpvEvent.EventName);
        Assert.Equal("error", mpvEvent.Reason);
        Assert.Equal("Failed to open stream", mpvEvent.Error);
    }
}
