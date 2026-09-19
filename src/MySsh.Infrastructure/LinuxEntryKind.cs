using System.ComponentModel;
using System.Runtime.InteropServices;
using MySsh.Core;

namespace MySsh.Infrastructure;

internal static class LinuxEntryKind
{
    internal static EntryKind Read(string path)
    {
        // statx has the same fixed ABI on Linux x64 and arm64. Do not open the
        // entry: opening a FIFO for reading can wait indefinitely for a writer.
        if (Statx(-100, path, 0x100 | 0x800, 1, out var status) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 20) throw new FileNotFoundException("File no longer exists.", path);
            throw new IOException("Cannot inspect the Linux file type.", new Win32Exception(error));
        }
        if ((status.Mask & 1) == 0) throw new IOException("The filesystem did not report the file type.");
        return (status.Mode & 0xF000) switch
        {
            0x8000 => EntryKind.File,
            0x4000 => EntryKind.Directory,
            0xA000 => EntryKind.SymbolicLink,
            _ => EntryKind.Other
        };
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxBuffer status);
}
