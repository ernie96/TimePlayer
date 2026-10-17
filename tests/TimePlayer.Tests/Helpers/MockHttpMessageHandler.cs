using System.Net;
using System.Net.Http;

namespace TimePlayer.Tests.Helpers;

public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public List<HttpRequestMessage> Requests { get; } = new();

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public static MockHttpMessageHandler StringResponse(string responseContent, HttpStatusCode statusCode = HttpStatusCode.OK, string mediaType = "application/json")
    {
        return new MockHttpMessageHandler(req => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseContent, System.Text.Encoding.UTF8, mediaType)
        });
    }

    public static MockHttpMessageHandler Status(HttpStatusCode statusCode)
    {
        return new MockHttpMessageHandler(req => new HttpResponseMessage(statusCode));
    }

    public static MockHttpMessageHandler Throws(Exception exception)
    {
        return new MockHttpMessageHandler(req => throw exception);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_handler(request));
    }
}
