using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed partial class TransferEngine
{
    private static async Task<FileCopyResult> CopyFileAsync(IFileSystem source, string sourcePath,
        FileEntry sourceEntry, IFileSystem destination, string destinationPath, TransferOptions options,
        Action<long> reportFileBytes, CancellationToken ct)
    {
        var existing = await destination.StatAsync(destinationPath, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (options.Conflict == ConflictAction.Skip) return new(0, true);
            if (options.Conflict == ConflictAction.Cancel) throw new OperationCanceledException(ct);
            if (options.Conflict != ConflictAction.Overwrite || existing.Kind != EntryKind.File)
                throw new TransferConflictException(destinationPath);
        }
        var version = await DestinationSnapshot.CaptureAsync(destination, destinationPath, existing, ct).ConfigureAwait(false);
        var partial = destination.Join(destination.Parent(destinationPath),
            TransferTemporaryNames.Partial(destinationPath, sourcePath, sourceEntry));
        var partialEntry = await destination.StatAsync(partial, ct).ConfigureAwait(false);
        long offset = 0;
        var input = await source.OpenReadAsync(sourcePath, ct).ConfigureAwait(false);
        await using var inputLifetime = input.ConfigureAwait(false);
        if (partialEntry is { Kind: EntryKind.File } && partialEntry.Length <= sourceEntry.Length)
        {
            if (options.VerifyResumePrefix)
                await VerifyPrefixAsync(input, destination, partial, partialEntry.Length, ct).ConfigureAwait(false);
            offset = partialEntry.Length;
            input.Seek(offset, SeekOrigin.Begin);
            reportFileBytes(offset);
        }
        else if (partialEntry is not null)
            throw new IOException($"Cannot resume because the partial destination is not a compatible regular file: {partial}");

        var output = await destination.OpenWriteAsync(partial, partialEntry is null, ct).ConfigureAwait(false);
        try
        {
            output.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[BufferSize];
            // The plan's size is the bound. A growing source must not keep this
            // read-to-EOF operation running forever or inflate progress past 100%.
            while (offset < sourceEntry.Length)
            {
                var wanted = (int)Math.Min(buffer.Length, sourceEntry.Length - offset);
                var count = await input.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
                if (count == 0) throw new IOException("The source became shorter during the transfer.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                offset += count;
                reportFileBytes(offset);
            }
            if (await input.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0)
                throw new IOException("The source grew during the transfer.");
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { await output.DisposeAsync().ConfigureAwait(false); } catch { }
            throw;
        }
        // Successful CLOSE is mandatory before any verification/commit (#1).
        await output.DisposeAsync().ConfigureAwait(false);
        var finalPartial = await destination.StatAsync(partial, ct).ConfigureAwait(false)
            ?? throw new IOException("The transferred partial file disappeared before commit.");
        if (finalPartial.Kind != EntryKind.File || finalPartial.Length != sourceEntry.Length)
            throw new IOException($"Transferred file mismatch: expected a regular file of {sourceEntry.Length} bytes.");

        // Verify ordinary Copy as well as Move, including resumed prefixes.
        await SourceIntegrity.VerifyAsync(source, sourcePath, sourceEntry, destination, partial, ct).ConfigureAwait(false);
        if (options.PreserveMetadata)
            await destination.SetMetadataAsync(partial, sourceEntry.Modified, sourceEntry.Mode, ct).ConfigureAwait(false);
        await version.VerifyAsync(destination, destinationPath, ct).ConfigureAwait(false);
        await destination.RenameAsync(partial, destinationPath, existing is not null, ct).ConfigureAwait(false);
        return new(sourceEntry.Length, false);
    }
}
