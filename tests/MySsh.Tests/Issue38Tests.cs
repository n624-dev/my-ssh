using System.Text;
using MySsh.Infrastructure;

internal sealed class Issue38Tests : IRegressionCase
{
    public string Name => "#38 built-in editing preserves Unicode encodings, BOMs and line endings";
    public async Task RunAsync()
    {
        using var root = new TestDirectory();
        using var local = new LocalFileSystem();
        Encoding[] encodings = [new UTF8Encoding(false, true), new UTF8Encoding(true, true),
            new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true)];
        const string original = "first 日本語🚀\r\nsecond\r\n";
        for (var i = 0; i < encodings.Length; i++)
        {
            var encoding = encodings[i];
            var path = root.File("encoding-" + i + ".txt");
            var originalBytes = Bytes(encoding, original);
            await File.WriteAllBytesAsync(path, originalBytes);
            var session = await EditSession.OpenAsync(local, path, root.File("drafts"), "LOCAL", CancellationToken.None);
            var text = session.ReadText();
            RegressionCases.Check(text == original.Replace("\r\n", "\n"), "Editor text was decoded incorrectly.");
            session.WriteText(text);
            RegressionCases.Check((await File.ReadAllBytesAsync(session.DraftPath)).SequenceEqual(originalBytes), "No-change save changed encoding, BOM or line endings.");
            session.WriteText(text.Replace("second", "changed 日本語"));
            var expected = Bytes(encoding, original.Replace("second", "changed 日本語"));
            RegressionCases.Check((await File.ReadAllBytesAsync(session.DraftPath)).SequenceEqual(expected), "An edit changed the original file format.");
            await session.SaveAsync(path, CancellationToken.None);
            RegressionCases.Check((await File.ReadAllBytesAsync(path)).SequenceEqual(expected), "Uploading the draft changed its encoding.");
            var empty = Bytes(encoding, "");
            var emptyDocument = TextFileDocument.Decode(empty);
            RegressionCases.Check(emptyDocument.Encode(emptyDocument.EditorText).SequenceEqual(empty), "An empty BOM-marked document lost its preamble.");
        }

        var mixed = Encoding.UTF8.GetBytes("one\r\ntwo\nthree\rfour");
        var document = TextFileDocument.Decode(mixed);
        RegressionCases.Check(document.Encode(document.EditorText).SequenceEqual(mixed), "Mixed endings changed without edits.");
        RegressionCases.Check(Encoding.UTF8.GetString(document.Encode(document.EditorText.Replace("two", "TWO"))) == "one\r\nTWO\nthree\rfour",
            "Editing one line rewrote unrelated mixed line endings.");
        RegressionCases.Check(Encoding.UTF8.GetString(document.Encode("one\ninserted\ntwo\nthree\nfour")) == "one\r\ninserted\r\ntwo\nthree\rfour",
            "Line insertion did not retain unchanged prefix/suffix endings.");
        var noFinalNewline = TextFileDocument.Decode(Encoding.UTF8.GetBytes("single line"));
        RegressionCases.Check(Encoding.UTF8.GetString(noFinalNewline.Encode("edited line")) == "edited line", "A final newline was added implicitly.");

        byte[][] invalid = [[0xFF], [0xFF, 0xFE, 0x61], [0xFF, 0xFE, 0, 0xD8], [0x41, 0, 0x42]];
        foreach (var bytes in invalid)
        {
            var path = root.File(Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllBytesAsync(path, bytes);
            var session = await EditSession.OpenAsync(local, path, root.File("drafts"), "LOCAL", CancellationToken.None);
            ExpectInvalid(() => session.ReadText());
            ExpectInvalid(() => session.WriteText("replacement"));
            RegressionCases.Check(File.ReadAllBytes(path).SequenceEqual(bytes) && File.ReadAllBytes(session.DraftPath).SequenceEqual(bytes),
                "Failed decoding replaced original or recovery data.");
        }
        ExpectInvalid(() => TextFileDocument.Decode(Encoding.UTF8.GetBytes("valid")).Encode("\uD800"));
        var oversized = root.File("large.txt");
        await File.WriteAllBytesAsync(oversized, new byte[TextFileDocument.MaximumSourceBytes + 1]);
        ExpectInvalid(() => TextFileDocument.Read(oversized));
    }

    private static byte[] Bytes(Encoding encoding, string value) => encoding.GetPreamble().Concat(encoding.GetBytes(value)).ToArray();
    private static void ExpectInvalid(Action operation)
    {
        try { operation(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Unsupported or invalid text was silently accepted.");
    }
}
