using System.Text.Json;
using MySsh.Core;

namespace MySsh.Infrastructure;

/// <summary>A recoverable edit. Only a successful save or explicit discard removes the draft.</summary>
public sealed class EditSession
{
    private readonly IFileSystem _fileSystem;
    private TextFileDocument? _textDocument;
    public FileEntry Original { get; }
    public string DraftDirectory { get; }
    public string DraftPath { get; }
    public bool Saved { get; private set; }

    private EditSession(IFileSystem fileSystem, FileEntry original, string directory, string path)
    {
        _fileSystem = fileSystem;
        Original = original;
        DraftDirectory = directory;
        DraftPath = path;
    }

    public static async Task<EditSession> OpenAsync(IFileSystem fileSystem, string path,
        string draftsRoot, string connectionDescription, CancellationToken ct)
    {
        var original = await fileSystem.StatAsync(path, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException(path);
        if (original.Kind != EntryKind.File) throw new IOException("Only regular files can be edited.");
        PrivateStorage.EnsureDirectory(draftsRoot);
        var directory = Path.Combine(Path.GetFullPath(draftsRoot), Guid.NewGuid().ToString("N"));
        PrivateStorage.EnsureDirectory(directory);
        var extension = Path.GetExtension(original.Name);
        if (extension.Length == 0 || extension.Length > 16 || extension.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            extension = ".txt";
        var draft = new EditSession(fileSystem, original, directory, Path.Combine(directory, "content" + extension));
        try
        {
            using (var manifest = PrivateStorage.CreateFile(Path.Combine(directory, "recovery.json")))
            {
                JsonSerializer.Serialize(manifest, new
                {
                    SourcePath = path, original.Name, SourceIsRemote = fileSystem.IsRemote,
                    Connection = connectionDescription, original.Length, original.Modified,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                manifest.Flush(true);
            }
            using var local = new LocalFileSystem();
            await new TransferEngine().CopyAsync(fileSystem, path, local, draft.DraftPath,
                new(PreserveMetadata: false), null, ct).ConfigureAwait(false);
            return draft;
        }
        catch
        {
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public string ReadText()
    {
        _textDocument = TextFileDocument.Read(DraftPath);
        return _textDocument.EditorText;
    }

    public void WriteText(string text)
    {
        // Encode before opening the pending file. Invalid Unicode or an unknown
        // source encoding must not replace even the last recoverable draft.
        var document = _textDocument ??= TextFileDocument.Read(DraftPath);
        var bytes = document.Encode(text);
        var pending = Path.Combine(DraftDirectory, "save-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = PrivateStorage.CreateFile(pending))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            File.Move(pending, DraftPath, true);
            _textDocument = TextFileDocument.Decode(bytes);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    public async Task SaveAsync(string? destination, CancellationToken ct)
    {
        if (Saved) throw new InvalidOperationException("This edit was already saved.");
        var path = destination ?? Original.Path;
        var replacingOriginal = path.Equals(Original.Path, _fileSystem.PathComparison);
        if (replacingOriginal)
        {
            var current = await _fileSystem.StatAsync(Original.Path, ct).ConfigureAwait(false);
            if (current is null || current.Kind != Original.Kind || current.Length != Original.Length ||
                current.Modified != Original.Modified || current.Mode != Original.Mode)
                throw new IOException("The original file or its permissions changed. Your draft is retained; use Save as or keep it for recovery.");
        }
        // Preserve target permissions before commit, not the private draft mode.
        await using var contents = new EditContentFileSystem(Original.Mode);
        await new TransferEngine().CopyAsync(contents, DraftPath, _fileSystem, path,
            new(PreserveMetadata: true, Conflict: replacingOriginal ? ConflictAction.Overwrite : ConflictAction.Ask),
            null, ct).ConfigureAwait(false);
        Saved = true;
        try { Discard(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Discard() => Directory.Delete(DraftDirectory, true);
}
