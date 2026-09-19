using System.ComponentModel;
using System.Runtime.InteropServices;
using MySsh.Core;

namespace MySsh.Infrastructure;

internal static class WindowsEntryKind
{
    internal static EntryKind Read(string path, FileAttributes attributes)
    {
        if (!attributes.HasFlag(FileAttributes.ReparsePoint)) return Classify(attributes, 0);
        // FindFirstFile reports the tag without opening/hydrating the target.
        var handle = FindFirstFileW(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), out var data);
        if (handle == new IntPtr(-1))
            throw new IOException("Cannot inspect the reparse point.", new Win32Exception(Marshal.GetLastPInvokeError()));
        try { return Classify(data.Attributes, data.ReparseTag); }
        finally { FindClose(handle); }
    }

    internal static EntryKind Classify(FileAttributes attributes, uint tag)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (tag is 0xA000000C or 0xA0000003) return EntryKind.SymbolicLink; // symlink / junction
            // CLOUD and CLOUD_1..F are placeholders, not name-surrogate links.
            if ((tag & 0xFFFF0FFF) != 0x9000001A) return EntryKind.Other;
        }
        return attributes.HasFlag(FileAttributes.Directory) ? EntryKind.Directory : EntryKind.File;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint SizeHigh, SizeLow, ReparseTag, Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string path, out FindData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
