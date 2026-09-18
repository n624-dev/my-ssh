using System.Text;
using System.Text.Json;
using MySsh.Core;

namespace MySsh.Infrastructure;

/// <summary>A recoverable edit. Only a successful save or explicit discard removes the draft.</summary>
public sealed class EditSession
{
    private readonly IFileSystem _fileSystem;
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
        CreatePrivateDirectory(draftsRoot);
        var directory = Path.Combine(Path.GetFullPath(draftsRoot), Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(directory);
        var extension = Path.GetExtension(original.Name);
        if (extension.Length == 0 || extension.Length > 16 || extension.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            extension = ".txt";
        var draft = new EditSession(fileSystem, original, directory, Path.Combine(directory, "content" + extension));
        try
        {
            using (var manifest = CreatePrivateFile(Path.Combine(directory, "recovery.json")))
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
            // No editor has received this draft yet, so there are no user edits to lose.
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public string ReadText()
    {
        using var input = File.OpenRead(DraftPath);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }

    public void WriteText(string text)
    {
        var pending = Path.Combine(DraftDirectory, "save-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = CreatePrivateFile(pending))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                output.Write(bytes);
                output.Flush(true);
            }
            File.Move(pending, DraftPath, true);
        }
        finally
        {
            // DraftPath still contains the previous durable draft if replacement failed.
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
        // Preserve the target's permissions, not the private local draft's permissions.
        // TransferEngine applies these to the partial file BEFORE the atomic rename;
        // a chmod failure therefore cannot publish a file with incorrect permissions.
        await using var contents = new EditContentFileSystem(Original.Mode);
        await new TransferEngine().CopyAsync(contents, DraftPath, _fileSystem, path,
            new(PreserveMetadata: true, Conflict: replacingOriginal ? ConflictAction.Overwrite : ConflictAction.Ask),
            null, ct).ConfigureAwait(false);
        Saved = true;
        // A cleanup error after commit is not a failed save and must not trigger another overwrite.
        try { Discard(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Discard() => Directory.Delete(DraftDirectory, true);

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}
