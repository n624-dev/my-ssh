namespace MySsh.Core;

/// <summary>An optional capability for changing permissions without touching timestamps.</summary>
public interface IPermissionFileSystem
{
    Task SetPermissionsAsync(string path, uint mode, CancellationToken cancellationToken);
}

public static class PermissionMode
{
    public static void Validate(uint mode)
    {
        if (mode > 0x1FF) throw new ArgumentOutOfRangeException(nameof(mode), "Only permission bits 000-777 are supported.");
    }
}
