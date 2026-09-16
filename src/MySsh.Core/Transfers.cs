namespace MySsh.Core;

public enum ConflictAction { Ask, Overwrite, Skip, Rename, Cancel }
public enum TransferState { Queued, Running, Paused, Completed, Failed, Cancelled, Partial }

public sealed record TransferProgress(
    string Source,
    string Destination,
    long BytesTransferred,
    long? TotalBytes,
    int CompletedEntries,
    int TotalEntries,
    TransferState State,
    string Message = "");

public sealed record TransferOptions(
    bool Move = false,
    bool PreserveMetadata = true,
    ConflictAction Conflict = ConflictAction.Ask,
    bool FollowSymbolicLinks = false,
    bool VerifyResumePrefix = true);

public sealed class TransferConflictException(string destination) : IOException($"Destination already exists: {destination}")
{
    public string Destination { get; } = destination;
}

public sealed class PartialMoveException(string source, string destination, Exception deleteError)
    : IOException($"Copied successfully but could not delete source: {source} -> {destination}", deleteError)
{
    public string SourcePath { get; } = source;
    public string DestinationPath { get; } = destination;
}
