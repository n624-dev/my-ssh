using System.Net.Sockets;
using System.Runtime.InteropServices;
using MySsh.Core;
using MySsh.Infrastructure;

internal sealed class Issue26Tests : IRegressionCase
{
    public string Name => "#26 FIFO, socket and device entries are rejected without opening them";

    public async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var root = new TestDirectory();
        using var fs = new LocalFileSystem();
        var fifo = root.File("pipe");
        RegressionCases.Check(Mkfifo(fifo, 0x180) == 0, "Cannot create FIFO fixture.");
        var socketPath = root.File("socket");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        foreach (var path in new[] { fifo, socketPath, "/dev/null" })
        {
            var entry = await fs.StatAsync(path, CancellationToken.None);
            RegressionCases.Check(entry?.Kind == EntryKind.Other, "A special entry was classified as regular: " + path);
            await Task.Run(async () =>
            {
                await RegressionCases.ThrowsAsync<IOException>(() => fs.OpenReadAsync(path, CancellationToken.None));
                await RegressionCases.ThrowsAsync<IOException>(() => fs.OpenWriteAsync(path, false, CancellationToken.None));
                await RegressionCases.ThrowsAsync<IOException>(() => new TransferEngine().CopyAsync(fs, path, fs,
                    root.File("copy"), new(), null, CancellationToken.None));
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        var entries = await fs.ListAsync(root.Path, CancellationToken.None);
        RegressionCases.Check(entries.All(e => e.Kind == EntryKind.Other), "Listing lost special entry types.");
        var regular = root.File("regular");
        await File.WriteAllTextAsync(regular, "normal");
        RegressionCases.Check((await fs.StatAsync(regular, CancellationToken.None))?.Kind == EntryKind.File, "Regular files no longer work.");
        var link = root.File("link");
        File.CreateSymbolicLink(link, regular);
        RegressionCases.Check((await fs.StatAsync(link, CancellationToken.None))?.Kind == EntryKind.SymbolicLink, "statx followed the symbolic link.");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
