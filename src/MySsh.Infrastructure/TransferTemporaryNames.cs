using System.Security.Cryptography;
using System.Text;
using MySsh.Core;

namespace MySsh.Infrastructure;

internal static class TransferTemporaryNames
{
    public static string Partial(string destinationPath, string sourcePath, FileEntry source)
    {
        var identity = $"{sourcePath}\0{source.Length}\0{source.Modified.UtcTicks}";
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 8)).ToLowerInvariant();
        var name = destinationPath.Replace('\\', '/').Split('/').Last();
        var legacy = $".{name}.my-ssh-part-{suffix}";
        // Preserve resumability of existing short names. Leave conservative headroom
        // below the common 255-byte component limit for all newly generated names.
        if (Encoding.UTF8.GetByteCount(legacy) <= 200) return legacy;
        // Include the full destination: otherwise equal source identities copied to
        // two different long target names in one directory would share a partial.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(destinationPath + "\0" + identity));
        return ".my-ssh-part-" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    public static string Link() => ".my-ssh-link-" + Guid.NewGuid().ToString("N");
}
