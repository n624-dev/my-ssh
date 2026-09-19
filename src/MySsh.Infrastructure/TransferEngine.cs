using MySsh.Core;

namespace MySsh.Infrastructure;

public sealed partial class TransferEngine
{
    private const int BufferSize = 256 * 1024;

    public async Task CopyAsync(IFileSystem source, string sourcePath,
        IFileSystem destination, string destinationPath, TransferOptions options,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken,
        TransferResumeState? resume = null)
    {
        using var resumeLease = resume?.Enter(source, sourcePath, destination, destinationPath);
        var root = await source.StatAsync(sourcePath, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Source does not exist.", sourcePath);
        await TransferGuard.ValidateAsync(source, sourcePath, destination, destinationPath,
            root.Kind, cancellationToken).ConfigureAwait(false);
        var plan = new List<(FileEntry Entry, string Source, string Destination)>();
        await BuildPlanAsync(source, destination, root, sourcePath, destinationPath,
            options, plan, cancellationToken).ConfigureAwait(false);
        ValidateDestinationNames(plan.Select(item => (item.Source, item.Destination)), destination.PathComparison);

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
            var receipt = resume is null ? TransferResumeState.ResumeResult.NotRecorded :
                await resume.VerifyAsync(source, item.Source, destination, item.Destination,
                    options.Conflict, cancellationToken).ConfigureAwait(false);
            if (receipt == TransferResumeState.ResumeResult.Verified)
            {
                if (item.Entry.Kind == EntryKind.File) transferred += item.Entry.Length;
            }
            else if (receipt == TransferResumeState.ResumeResult.Skipped)
            {
                skipped++;
            }
            else switch (item.Entry.Kind)
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
                    else
                    {
                        if (result == DirectoryResult.Created) resume?.RecordDirectory(item.Source, item.Destination);
                        if (result == DirectoryResult.Created || resume?.CreatedDirectory(item.Source, item.Destination) == true)
                            createdDirectories.Add((item.Entry, item.Destination));
                    }
                    break;
                }
                case EntryKind.File:
                {
                    var beforeFile = transferred;
                    var result = await CopyFileAsync(source, item.Source, item.Entry,
                        destination, item.Destination, options,
                        currentFileBytes => progress?.Report(new(item.Source, item.Destination,
                            beforeFile + currentFileBytes, totalBytes, completed, plan.Count, TransferState.Running)),
                        cancellationToken, resume).ConfigureAwait(false);
                    if (result.Skipped) skipped++;
                    else transferred += result.LogicalBytes;
                    break;
                }
                case EntryKind.SymbolicLink:
                    if (!await CopyLinkAsync(source, item.Source, item.Entry, destination, item.Destination,
                        options, cancellationToken, resume).ConfigureAwait(false)) skipped++;
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
                // Revalidate resumed destinations too, before deleting any source.
                foreach (var item in plan)
                {
                    await VerifyMoveSourceAsync(source, item.Source, item.Entry, cancellationToken).ConfigureAwait(false);
                    if (resume is not null && item.Entry.Kind is EntryKind.File or EntryKind.SymbolicLink)
                        await resume.VerifyAsync(source, item.Source, destination, item.Destination,
                            ConflictAction.Ask, cancellationToken).ConfigureAwait(false);
                }
                // Delete only the copied plan, never newly created source entries.
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

    private static async Task<bool> CopyLinkAsync(IFileSystem source, string sourcePath, FileEntry sourceEntry,
        IFileSystem destination, string destinationPath, TransferOptions options, CancellationToken ct, TransferResumeState? resume)
    {
        var existing = await destination.StatAsync(destinationPath, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (options.Conflict == ConflictAction.Skip) return false;
            if (options.Conflict == ConflictAction.Cancel) throw new OperationCanceledException(ct);
            if (options.Conflict != ConflictAction.Overwrite) throw new TransferConflictException(destinationPath);
        }
        var version = await DestinationSnapshot.CaptureAsync(destination, destinationPath, existing, ct).ConfigureAwait(false);
        var target = await source.ReadLinkAsync(sourcePath, ct).ConfigureAwait(false);
        var temporary = destination.Join(destination.Parent(destinationPath), TransferTemporaryNames.Link());
        await destination.CreateLinkAsync(temporary, target, ct).ConfigureAwait(false);
        try
        {
            FileEntry? committedMetadata = null;
            if (resume is not null)
            {
                if (target != sourceEntry.LinkTarget ||
                    !DestinationSnapshot.SameMetadata(sourceEntry, await source.StatAsync(sourcePath, ct).ConfigureAwait(false)))
                    throw new IOException("Source link changed while copying; it was not committed.");
                committedMetadata = await destination.StatAsync(temporary, ct).ConfigureAwait(false)
                    ?? throw new IOException("Temporary link disappeared before commit.");
            }
            await version.VerifyAsync(destination, destinationPath, ct).ConfigureAwait(false);
            await destination.RenameAsync(temporary, destinationPath, existing is not null, ct).ConfigureAwait(false);
            if (committedMetadata is not null) resume!.Record(sourcePath, sourceEntry, destinationPath, committedMetadata, null);
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
