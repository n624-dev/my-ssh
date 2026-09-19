using System.Text;

namespace MySsh.Infrastructure;

/// <summary>Strict Unicode decoding with a byte-preserving no-change round trip.</summary>
internal sealed class TextFileDocument
{
    internal const int MaximumSourceBytes = 4 * 1024 * 1024;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private readonly byte[] _originalBytes;
    private readonly string[] _endings;
    private readonly string[] _lines;
    internal string EditorText { get; }

    private TextFileDocument(Encoding encoding, byte[] preamble, byte[] original, string decoded)
    {
        _encoding = encoding;
        _preamble = preamble;
        _originalBytes = original;
        EditorText = Normalize(decoded);
        _lines = EditorText.Split('\n');
        var endings = new List<string>();
        for (var i = 0; i < decoded.Length; i++)
        {
            if (decoded[i] == '\r')
            {
                if (i + 1 < decoded.Length && decoded[i + 1] == '\n') { endings.Add("\r\n"); i++; }
                else endings.Add("\r");
            }
            else if (decoded[i] == '\n') endings.Add("\n");
        }
        _endings = endings.ToArray();
    }

    internal static TextFileDocument Read(string path)
    {
        using var input = File.OpenRead(path);
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (bytes.Length < MaximumSourceBytes)
        {
            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, MaximumSourceBytes - bytes.Length));
            if (count == 0) return Decode(bytes.ToArray());
            bytes.Write(buffer, 0, count);
        }
        if (input.ReadByte() != -1) throw new IOException("Built-in editing is limited to 4 MiB. Use an external editor; the draft is retained.");
        return Decode(bytes.ToArray());
    }

    internal static TextFileDocument Decode(byte[] bytes)
    {
        Encoding encoding;
        var offset = 0;
        // UTF-32 LE shares the first two BOM bytes with UTF-16 LE: longest first.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }))
        { encoding = new UTF32Encoding(false, true, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
        { encoding = new UTF32Encoding(true, true, true); offset = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        { encoding = new UTF8Encoding(true, true); offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
        else encoding = new UTF8Encoding(false, true);
        try
        {
            var text = encoding.GetString(bytes, offset, bytes.Length - offset);
            if (text.Contains('\0')) throw new IOException("This file contains binary NUL data; use an external editor. The draft is retained.");
            return new(encoding, bytes[..offset], bytes.ToArray(), text);
        }
        catch (DecoderFallbackException ex)
        {
            throw new IOException("The file is not valid UTF-8 or BOM-marked Unicode. Configure an external editor for other encodings; the draft is retained.", ex);
        }
    }

    internal byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) throw new IOException("Binary NUL data cannot be saved by the built-in editor.");
        var normalized = Normalize(text);
        if (normalized == EditorText) return _originalBytes.ToArray();
        var rendered = RestoreLineEndings(normalized);
        try
        {
            var body = _encoding.GetBytes(rendered);
            var bytes = new byte[_preamble.Length + body.Length];
            _preamble.CopyTo(bytes, 0);
            body.CopyTo(bytes, _preamble.Length);
            return bytes;
        }
        catch (EncoderFallbackException ex)
        {
            throw new IOException("The edited text contains invalid Unicode. The previous draft was not overwritten.", ex);
        }
    }

    private string RestoreLineEndings(string normalized)
    {
        var lines = normalized.Split('\n');
        var preferred = _endings.GroupBy(value => value).OrderByDescending(group => group.Count()).FirstOrDefault()?.Key ?? "\n";
        var prefix = 0;
        while (prefix < Math.Min(lines.Length, _lines.Length) && lines[prefix] == _lines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(lines.Length, _lines.Length) - prefix &&
               lines[^(suffix + 1)] == _lines[^(suffix + 1)]) suffix++;
        var output = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            output.Append(lines[i]);
            if (i == lines.Length - 1) break;
            var ending = preferred;
            if (lines.Length == _lines.Length || i < prefix)
            {
                if (i < _endings.Length) ending = _endings[i];
            }
            else if (i >= lines.Length - suffix)
            {
                var originalIndex = _lines.Length - (lines.Length - i);
                if (originalIndex >= 0 && originalIndex < _endings.Length) ending = _endings[originalIndex];
            }
            output.Append(ending);
        }
        return output.ToString();
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
}
