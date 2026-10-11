namespace ThermalWatch.Core;

internal static class HttpContentReader
{
    public static async Task<byte[]?> ReadLimitedBytesAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        await using (stream.ConfigureAwait(continueOnCapturedContext: false))
        {
            using var result = new MemoryStream();

            byte[] buffer = new byte[8192];

            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            while (read > 0)
            {
                if (result.Length + read > maximumBytes)
                    return null;

                await result.WriteAsync(buffer.AsMemory(start: 0, read), cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);

                read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }

            return result.ToArray();
        }
    }
}
