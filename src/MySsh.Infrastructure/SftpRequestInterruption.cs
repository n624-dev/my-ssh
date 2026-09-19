namespace MySsh.Infrastructure;

/// <summary>A request was started but no complete, matching reply was received.</summary>
public interface ISftpRequestInterruption
{
    uint RequestId { get; }
    byte RequestType { get; }
    bool OutcomeUnknown { get; }
}

public sealed class SftpRequestInterruptedException : IOException, ISftpRequestInterruption
{
    public uint RequestId { get; }
    public byte RequestType { get; }
    public bool OutcomeUnknown { get; }

    internal SftpRequestInterruptedException(uint id, byte type, bool outcomeUnknown, Exception inner)
        : base(Describe(id, outcomeUnknown), inner)
    {
        RequestId = id;
        RequestType = type;
        OutcomeUnknown = outcomeUnknown;
    }

    internal static string Describe(uint id, bool unknown) =>
        $"SFTP request {id} was interrupted; this session was discarded. " +
        (unknown ? "The server may have performed the operation. Reconnect and inspect its result before retrying."
                 : "Reconnect before issuing another request.");
}

public sealed class SftpRequestCanceledException : OperationCanceledException, ISftpRequestInterruption
{
    public uint RequestId { get; }
    public byte RequestType { get; }
    public bool OutcomeUnknown { get; }

    internal SftpRequestCanceledException(uint id, byte type, bool outcomeUnknown,
        Exception inner, CancellationToken cancellationToken)
        : base(SftpRequestInterruptedException.Describe(id, outcomeUnknown), inner, cancellationToken)
    {
        RequestId = id;
        RequestType = type;
        OutcomeUnknown = outcomeUnknown;
    }
}
