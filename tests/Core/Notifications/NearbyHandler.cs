using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;

namespace ThermalWatch.Tests.Core.Notifications;

internal sealed class NearbyHandler([StringSyntax(StringSyntaxAttribute.Json)] string responseJson = NearbyHandler.EmptyResponse) : HttpMessageHandler
{
    [StringSyntax(StringSyntaxAttribute.Json)]
    private const string EmptyResponse = """{"elements":[]}""";
    public List<string> Queries { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.NotNull(request.Content);
        string body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        Assert.StartsWith(expectedStartString: "data=", body, StringComparison.Ordinal);
        Queries.Add(Uri.UnescapeDataString(body[5..].Replace(oldChar: '+', newChar: ' ')));

        return new(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, mediaType: "application/json") };
    }
}
