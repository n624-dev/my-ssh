using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed partial class TransferEngine
{
    private const int BufferSize = 256 * 1024;

    public async Task CopyAsync(IFileSystem source, string sourcePath,
        IFileSystem destination, string destinationPath, TransferOptions options,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var root = await source.StatAsync(sourcePath, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Source does not exist.", sourcePath);
        await TransferGuard.ValidateAsync(source, sourcePath, destination, destinationPath,
            root.Kind, cancellationToken).ConfigureAwait(false);
        var plan = new List<(FileEntry Entry, string Source, string Destination)>();
        await BuildPlanAsync(source, destination, root, sourcePath, destinationPath,
            options, plan, cancellationToken).ConfigureAwait(false);

        var totalBytes = plan.Where(x => x.Entry.Kind == EntryKind.File).Sum(x => x.Entry.Length);
        var skippedDirectories = new List<string>();
        var createdDirectories = new List<(FileEntry Entry, string Destination)>();
        long transferred = 0;
        var completed = 0;
        var skipped = 0;

        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skippedDirectories.Any(parent => IsBelow(destination, item.Destination, parent)))
            {
                skipped++;
                completed++;
                continue;
            }
            progress?.Report(new(item.Source, item.Destination, transferred, totalBytes,
                completed, plan.Count, TransferState.Running));
            switch (item.Entry.Kind)
            {
                case EntryKind.Directory:
                {
                    var result = await EnsureDirectoryAsync(destination, item.Destination,
                        options, cancellationToken).ConfigureAwait(false);
                    if (result == DirectoryResult.Skipped)
                    {
                        skippedDirectories.Add(item.Destination);
                        skipped++;
                    }
                    else if (result == DirectoryResult.Created)
                        createdDirectories.Add((item.Entry, item.Destination));
                    break;
                }
                case EntryKind.File:
                {
                    var beforeFile = transferred;
                    var result = await CopyFileAsync(source, item.Source, item.Entry,
                        destination, item.Destination, options,
                        currentFileBytes => progress?.Report(new(item.Source, item.Destination,
                            beforeFile + currentFileBytes, totalBytes, completed, plan.Count, TransferState.Running)),
                        cancellationToken).ConfigureAwait(false);
                    if (result.Skipped) skipped++;
                    else transferred += result.LogicalBytes;
                    break;
                }
                case EntryKind.SymbolicLink:
                    if (!await CopyLinkAsync(source, item.Source, destination, item.Destination,
                        options, cancellationToken).ConfigureAwait(false)) skipped++;
                    break;
                default:
                    throw new IOException($"Unsupported entry type: {item.Source}");
            }
            completed++;
            progress?.Report(new(item.Source, item.Destination, transferred, totalBytes,
                completed, plan.Count, TransferState.Running,
                skipped > 0 ? $"Skipped entries: {skipped}" : ""));
        }
        if (options.PreserveMetadata)
            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
                await destination.SetMetadataAsync(directory.Destination, directory.Entry.Modified,
                    directory.Entry.Mode, cancellationToken).ConfigureAwait(false);

        if (options.Move)
        {
            if (skipped > 0)
            {
                var reason = new IOException($"{skipped} entry or subtree was skipped, so the source was retained to prevent data loss.");
                progress?.Report(new(sourcePath, destinationPath, transferred, totalBytes,
                    completed, plan.Count, TransferState.Partial, reason.Message));
                throw new PartialMoveException(sourcePath, destinationPath, reason);
            }
            try
            {
                // Delete only the copied plan, never newly created source entries.
                foreach (var item in plan)
                    await VerifyMoveSourceAsync(source, item.Source, item.Entry, cancellationToken).ConfigureAwait(false);
                foreach (var item in plan.AsEnumerable().Reverse())
                {
                    await VerifyMoveSourceAsync(source, item.Source, item.Entry, cancellationToken).ConfigureAwait(false);
                    await source.DeleteAsync(item.Source, item.Entry.Kind == EntryKind.Directory,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                progress?.Report(new(sourcePath, destinationPath, transferred, totalBytes,
                    completed, plan.Count, TransferState.Partial, "Copy completed, but the source could not be removed."));
                throw new PartialMoveException(sourcePath, destinationPath, ex);
            }
        }
        progress?.Report(new(sourcePath, destinationPath, transferred, totalBytes,
            completed, plan.Count, TransferState.Completed,
            skipped > 0 ? $"Completed with {skipped} skipped entry or subtree." : "Completed"));
    }

    private static async Task BuildPlanAsync(IFileSystem source, IFileSystem destination,
        FileEntry entry, string sourcePath, string destinationPath, TransferOptions options,
        List<(FileEntry Entry, string Source, string Destination)> plan, CancellationToken ct)
    {
        plan.Add((entry, sourcePath, destinationPath));
        if (entry.Kind != EntryKind.Directory) return;
        foreach (var child in (await source.ListAsync(sourcePath, ct).ConfigureAwait(false))
            .OrderBy(x => x.Kind == EntryKind.Directory ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (child.Kind == EntryKind.SymbolicLink && options.FollowSymbolicLinks)
                throw new IOException("Following symbolic links during recursive copies is disabled to prevent cycles. Copy the link itself instead.");
            await BuildPlanAsync(source, destination, child, child.Path, destination.Join(destinationPath, child.Name),
                options, plan, ct).ConfigureAwait(false);
        }
    }

    private static async Task<DirectoryResult> EnsureDirectoryAsync(IFileSystem destination,
        string path, TransferOptions options, CancellationToken ct)
    {
        var existing = await destination.StatAsync(path, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await destination.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
            return DirectoryResult.Created;
        }
        if (existing.Kind == EntryKind.Directory) return DirectoryResult.Existing;
        if (options.Conflict == ConflictAction.Skip) return DirectoryResult.Skipped;
        if (options.Conflict == ConflictAction.Cancel) throw new OperationCanceledException(ct);
        throw new TransferConflictException(path);
    }

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
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                offset += count;
                reportFileBytes(offset);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Error cleanup must not hide the original failure.
            try { await output.DisposeAsync().ConfigureAwait(false); } catch { }
            throw;
        }
        // Successful CLOSE is mandatory before commit (#1).
        await output.DisposeAsync().ConfigureAwait(false);
        var finalPartial = await destination.StatAsync(partial, ct).ConfigureAwait(false)
            ?? throw new IOException("The transferred partial file disappeared before commit.");
        if (finalPartial.Length != sourceEntry.Length)
            throw new IOException($"Transferred length mismatch: expected {sourceEntry.Length}, got {finalPartial.Length}.");
        if (options.PreserveMetadata)
            await destination.SetMetadataAsync(partial, sourceEntry.Modified, sourceEntry.Mode, ct).ConfigureAwait(false);
        await destination.RenameAsync(partial, destinationPath, existing is not null, ct).ConfigureAwait(false);
        return new(sourceEntry.Length, false);
    }

    private static async Task VerifyPrefixAsync(Stream source, IFileSystem destination,
        string partial, long length, CancellationToken ct)
    {
        var current = await destination.OpenReadAsync(partial, ct).ConfigureAwait(false);
        await using var currentLifetime = current.ConfigureAwait(false);
        var left = length;
        var sourceBuffer = new byte[BufferSize];
        var destinationBuffer = new byte[BufferSize];
        while (left > 0)
        {
            var wanted = (int)Math.Min(BufferSize, left);
            await ReadExactlyAsync(source, sourceBuffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
            await ReadExactlyAsync(current, destinationBuffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
            if (!sourceBuffer.AsSpan(0, wanted).SequenceEqual(destinationBuffer.AsSpan(0, wanted)))
                throw new IOException($"Partial file does not match the source; automatic resume was stopped: {partial}");
            left -= wanted;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Unexpected end of file while verifying resume data.");
            offset += count;
        }
    }

    private static async Task<bool> CopyLinkAsync(IFileSystem source, string sourcePath,
        IFileSystem destination, string destinationPath, TransferOptions options, CancellationToken ct)
    {
        var existing = await destination.StatAsync(destinationPath, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (options.Conflict == ConflictAction.Skip) return false;
            if (options.Conflict == ConflictAction.Cancel) throw new OperationCanceledException(ct);
            if (options.Conflict != ConflictAction.Overwrite) throw new TransferConflictException(destinationPath);
        }
        var target = await source.ReadLinkAsync(sourcePath, ct).ConfigureAwait(false);
        var temporary = destination.Join(destination.Parent(destinationPath), TransferTemporaryNames.Link());
        await destination.CreateLinkAsync(temporary, target, ct).ConfigureAwait(false);
        try
        {
            await destination.RenameAsync(temporary, destinationPath, existing is not null, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            try { await destination.DeleteAsync(temporary, false, CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static bool IsBelow(IFileSystem fs, string path, string ancestor)
    {
        if (path.Equals(ancestor, fs.PathComparison)) return false;
        var current = path;
        while (true)
        {
            var parent = fs.Parent(current);
            if (parent.Equals(ancestor, fs.PathComparison)) return true;
            if (parent.Equals(current, fs.PathComparison)) return false;
            current = parent;
        }
    }

    private static async Task VerifyMoveSourceAsync(IFileSystem fs, string path, FileEntry copied, CancellationToken ct)
    {
        var entry = await fs.StatAsync(path, ct).ConfigureAwait(false);
        if (entry is null || entry.Kind != copied.Kind ||
            (entry.Kind == EntryKind.File && (entry.Length != copied.Length || entry.Modified != copied.Modified)) ||
            (entry.Kind == EntryKind.SymbolicLink && entry.LinkTarget != copied.LinkTarget))
            throw new IOException($"Source changed during the move; it was not deleted: {path}");
    }

    private enum DirectoryResult { Created, Existing, Skipped }
    private readonly record struct FileCopyResult(long LogicalBytes, bool Skipped);
}
