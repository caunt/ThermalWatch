using System.Net;

namespace ThermalWatch.Tests.Core.Notifications;

internal sealed class NotFoundHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestCount++;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
