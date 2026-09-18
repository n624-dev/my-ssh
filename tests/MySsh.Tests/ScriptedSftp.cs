using System.Buffers.Binary;
using System.Text;
using MySsh.Core;
using MySsh.Infrastructure;

// Real SFTP encoding/parser over owned streams, without an SSH process or network.
internal static class ScriptedSftp
{
    public static async Task<SftpFileSystem> ConnectAsync(params byte[][] packets)
    {
        var session = await SftpSession.ConnectAsync(new MemoryStream(),
            new MemoryStream(packets.SelectMany(p => p).ToArray()), CancellationToken.None);
        return new SftpFileSystem(new Connection("scripted.test", "test"), session);
    }

    public static byte[] Packet(byte type, params byte[][] fields)
    {
        var payload = fields.SelectMany(f => f).ToArray();
        return U32(checked((uint)payload.Length + 1)).Concat(new[] { type }).Concat(payload).ToArray();
    }
    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
    public static byte[] Blob(byte[] bytes) => U32(checked((uint)bytes.Length)).Concat(bytes).ToArray();
    public static byte[] Text(string value) => Blob(Encoding.UTF8.GetBytes(value));
    public static byte[] Version() => Packet(2, U32(3));
    public static byte[] Handle(uint id) => Packet(102, U32(id), Blob([1]));
    public static byte[] Status(uint id, uint code, string message = "") => Packet(101, U32(id), U32(code), Text(message), Text(""));
}
