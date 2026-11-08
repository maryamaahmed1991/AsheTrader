using System.Security.Cryptography;
using System.Text;

namespace AsheTrader.Core;

/// <summary>
/// Optimistic file persistence for multiple game clients sharing a settings directory.
/// All writers must use this store's lock file. A stale instance never overwrites a
/// different disk revision: the caller must reload and ask the user to reapply the edit.
/// JSON parsing and schema validation belong to the caller.
/// </summary>
public sealed class SettingsFileStore
{
    private readonly string filePath;
    private readonly object gate = new();
    private bool loaded;
    private byte[]? baselineHash;

    public SettingsFileStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        this.filePath = Path.GetFullPath(filePath);
    }

    /// <summary>
    /// Reads a snapshot and remembers its exact bytes. Returns null when absent.
    /// Loading does not create a directory, settings file, or lock file.
    /// An atomic replacement during the read leaves this instance with either complete revision.
    /// </summary>
    public string? Load()
    {
        lock (gate)
        {
            var bytes = ReadBytesOrMissing();
            if (bytes is null)
            {
                baselineHash = null;
                loaded = true;
                return null;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            baselineHash = SHA256.HashData(bytes);
            loaded = true;
            return content;
        }
    }

    /// <summary>
    /// Saves only when disk still matches the last Load or successful save.
    /// A failed save preserves the baseline, so a caller cannot accidentally retry over a conflict.
    /// </summary>
    public bool TrySave(string json, out string error)
    {
        ArgumentNullException.ThrowIfNull(json);
        lock (gate)
        {
            if (!loaded)
            {
                error = "Settings must be loaded before they can be saved.";
                return false;
            }

            string? temporaryPath = null;
            try
            {
                var directory = Path.GetDirectoryName(filePath)!;
                Directory.CreateDirectory(directory);
                using var writerLock = AcquireWriterLock();
                var currentBytes = ReadBytesOrMissing();
                var currentHash = currentBytes is null ? null : SHA256.HashData(currentBytes);
                if (!SameRevision(baselineHash, currentHash))
                {
                    error = "Settings changed in another client. Reload the latest settings and reapply your last edit; nothing was overwritten.";
                    return false;
                }

                var bytes = new UTF8Encoding(false, true).GetBytes(json);
                temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                // Both files are in one directory/filesystem. Other cooperating writers are
                // excluded until the new revision is fully installed and recorded locally.
                File.Move(temporaryPath, filePath, overwrite: true);
                temporaryPath = null;
                baselineHash = SHA256.HashData(bytes);
                error = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EncoderFallbackException)
            {
                error = $"Could not save settings: {exception.Message}";
                return false;
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { /* Preserve the original error. */ }
                    catch (UnauthorizedAccessException) { /* Preserve the original error. */ }
                }
            }
        }
    }

    private FileStream AcquireWriterLock()
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(25);
            }
        }
    }

    private byte[]? ReadBytesOrMissing()
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool SameRevision(byte[]? first, byte[]? second) =>
        first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);
}
