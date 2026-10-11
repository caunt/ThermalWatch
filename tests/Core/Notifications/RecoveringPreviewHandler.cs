using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ThermalWatch.Tests.Core.Notifications;

internal sealed class RecoveringPreviewHandler(TimeSpan? responseDelay = null) : HttpMessageHandler
{
    private readonly TimeSpan _responseDelay = responseDelay ?? TimeSpan.Zero;
    private int _activeRequestCount;
    private int _maximumConcurrentRequests;

    public bool IsPreviewAvailable { get; set; }

    public int MaximumConcurrentRequests => Volatile.Read(ref _maximumConcurrentRequests);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri requestUri = Assert.IsType<Uri>(request.RequestUri);
        int activeRequestCount = Interlocked.Increment(ref _activeRequestCount);
        UpdateMaximum(ref _maximumConcurrentRequests, activeRequestCount);

        try
        {
            if (_responseDelay > TimeSpan.Zero)
                await Task.Delay(_responseDelay, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            HttpContent content;

            if (requestUri.AbsolutePath.EndsWith(value: ".xml", StringComparison.Ordinal))
            {
                content = new StringContent(content: "<Domains><Domain>2026-07-23</Domain></Domains>", Encoding.UTF8, mediaType: "application/xml");
            }
            else
            {
                string layers = ReadQueryValue(requestUri, name: "LAYERS");
                bool isComposite = layers.Contains(value: ',', StringComparison.Ordinal);

                byte[] bytes = isComposite
                    ? PngTestData.CreateSolidRgba(width: 900, height: 600, red: 30, green: 80, blue: 40, alpha: 255)
                    : PngTestData.CreateSolidRgba(
                        width: 64,
                        height: 64,
                        IsPreviewAvailable ? (byte)30 : (byte)0,
                        IsPreviewAvailable ? (byte)80 : (byte)0,
                        IsPreviewAvailable ? (byte)40 : (byte)0,
                        alpha: 255
                    );

                content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: "image/png");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
        }
        finally
        {
            Assert.True(Interlocked.Decrement(ref _activeRequestCount) >= 0);
        }
    }

    private static string ReadQueryValue(Uri uri, string name)
    {
        foreach (string item in uri.Query.TrimStart(trimChar: '?').Split(separator: '&'))
        {
            string[] pair = item.Split(separator: '=', count: 2);

            if (pair.Length == 2 && pair[0].Equals(name, StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair[1]);
        }

        throw new InvalidOperationException($"Missing {name} query value.");
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int current = Volatile.Read(ref maximum);

        while (candidate > current)
        {
            int observed = Interlocked.CompareExchange(ref maximum, candidate, current);

            if (observed == current)
                return;

            current = observed;
        }
    }
}
