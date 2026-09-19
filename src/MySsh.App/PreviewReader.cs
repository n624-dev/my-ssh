namespace MySsh.App;

internal static class PreviewReader
{
    internal const int Limit = 1024 * 1024;

    internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var contents = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (contents.Length < Limit)
        {
            var wanted = (int)Math.Min(buffer.Length, Limit - contents.Length);
            var count = await stream.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
            if (count == 0) return contents.ToArray();
            contents.Write(buffer, 0, count);
        }
        // A single probe distinguishes an exact-limit file from a growing or
        // unbounded stream. It is never appended to preview contents.
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0)
            throw new IOException("Text preview is limited to 1 MiB; the file exceeds that limit.");
        return contents.ToArray();
    }
}
