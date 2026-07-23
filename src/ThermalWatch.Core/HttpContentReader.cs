namespace ThermalWatch.Core;

internal static class HttpContentReader
{
    public static async Task<byte[]?> ReadLimitedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var result = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return result.ToArray();

                if (result.Length + read > maximumBytes)
                    return null;

                result.Write(buffer, offset: 0, read);
            }
        }
    }
}
