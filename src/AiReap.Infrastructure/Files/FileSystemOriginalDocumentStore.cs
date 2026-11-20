using AiReap.Application.Files;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Files;

// IOriginalDocumentStore on a private directory (OriginalDocumentOptions.RootPath), outside the
// repository and outside anything the API or web app serves. A file is {root}/{key[0..2]}/{key}.bin:
// the server-made key only, never a user's file name, and a key of any other shape is refused before
// a path is built. Writes go to a temporary file that is renamed into place, so a reader never sees a
// partial file and an interrupted write leaves only a *.tmp that ListKeysWrittenBeforeAsync removes.
// Files are not encrypted by AI-REAP; see docs/SOURCE_DOCUMENT_RETENTION.md (use an encrypted volume).
public class FileSystemOriginalDocumentStore : IOriginalDocumentStore
{
    private const string Extension = ".bin";
    private const string TempExtension = ".tmp";

    private readonly string _root;

    public FileSystemOriginalDocumentStore(IOptions<OriginalDocumentOptions> options)
        : this(options.Value.RootPath)
    {
    }

    public FileSystemOriginalDocumentStore(string? rootPath)
    {
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath) ? DefaultRoot() : rootPath);
    }

    // %LOCALAPPDATA%/AiReap/source-documents/{database name}: one directory per database (a name made
    // safe as a folder name, or a hash of the connection when there is no usable name).
    public static string DefaultRoot(string? connectionString = null)
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "AiReap", "source-documents");
        string? database = null;
        try
        {
            database = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString ?? string.Empty).InitialCatalog;
        }
        catch (ArgumentException)
        {
        }

        var folder = !string.IsNullOrWhiteSpace(database) && database.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && database.Trim('.').Length > 0
            ? database
            : OriginalDocumentFormats.Sha256(System.Text.Encoding.UTF8.GetBytes(connectionString ?? string.Empty))[..16];
        return Path.Combine(baseDir, folder);
    }

    public async Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + TempExtension;
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // A key is new (OriginalDocumentKeys.NewKey), so an existing file means a key was reused.
            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysWrittenBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        if (!Directory.Exists(_root))
        {
            return Task.FromResult<IReadOnlyList<string>>(keys);
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(file) >= cutoffUtc)
            {
                continue;
            }

            var name = Path.GetFileName(file);
            if (name.EndsWith(TempExtension, StringComparison.Ordinal))
            {
                TryDelete(file); // left by an interrupted write
            }
            else if (name.EndsWith(Extension, StringComparison.Ordinal) && OriginalDocumentKeys.IsValid(name[..^Extension.Length]))
            {
                keys.Add(name[..^Extension.Length]);
            }
            // Anything else was not put here by this store and is left alone.
        }

        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    // The owner is recorded in {root}/.aireap-owner the first time it is asked; afterwards only that
    // owner gets true. Created exclusively, so two databases starting together cannot both claim it.
    public async Task<bool> IsOwnedByAsync(string owner, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var marker = Path.Combine(_root, ".aireap-owner");
        try
        {
            await using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(owner), cancellationToken);
            return true;
        }
        catch (IOException) when (File.Exists(marker))
        {
            return string.Equals((await File.ReadAllTextAsync(marker, cancellationToken)).Trim(), owner, StringComparison.Ordinal);
        }
    }

    private string PathFor(string key)
    {
        if (!OriginalDocumentKeys.IsValid(key))
        {
            throw new ArgumentException("Not a storage key made by OriginalDocumentKeys.NewKey.", nameof(key));
        }

        var path = Path.GetFullPath(Path.Combine(_root, key[..2], key + Extension));
        // Unreachable for a valid key; kept so a future change to the key shape cannot escape the root.
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The storage key resolves outside the document store.", nameof(key));
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
