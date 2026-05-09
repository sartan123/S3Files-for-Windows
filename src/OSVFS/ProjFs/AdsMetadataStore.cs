using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;

namespace OSVFS.ProjFs;

/// <summary>
/// Reads and writes a key=value, UTF-8, line-delimited dictionary into an NTFS
/// alternate data stream (ADS) attached to a file. ProjFS placeholders accept
/// ADS writes without breaking the placeholder, which makes ADS a convenient
/// out-of-band channel for metadata that S3 keeps next to the object body
/// (HTTP headers, <c>x-amz-meta-*</c> user metadata).
/// </summary>
internal static partial class AdsMetadataStore
{
    /// <summary>
    /// ADS name suffix used for S3 user metadata (the <c>x-amz-meta-*</c>
    /// headers). Stored in lowercase form, one <c>key=value</c> entry per line.
    /// </summary>
    public const string UserMetaStreamName = "osvfs-user-meta";

    /// <summary>
    /// Persists <paramref name="entries"/> into the ADS named by
    /// <paramref name="streamName"/> attached to <paramref name="filePath"/>.
    /// A null/empty map removes the existing stream so a stale snapshot can
    /// never out-live the metadata it described. Logging-only on failure: the
    /// upload path tolerates a missing stream by sending no user metadata.
    /// </summary>
    public static void Write(
        string filePath,
        string streamName,
        IReadOnlyDictionary<string, string>? entries,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(streamName)) return;

        var streamPath = $"{filePath}:{streamName}";

        if (entries is null || entries.Count == 0)
        {
            TryDeleteStream(streamPath, logger);
            return;
        }

        try
        {
            using var fs = new FileStream(
                streamPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs, Utf8NoBom);
            foreach (var (key, value) in entries)
            {
                if (string.IsNullOrEmpty(key)) continue;
                // The wire format is intentionally trivial: one key=value per line, no
                // escaping. AWS already strips CR/LF from header values, and we lowercase
                // the keys before writing, so neither side can contain a newline or '=' that
                // would confuse the reader.
                writer.Write(key);
                writer.Write('=');
                writer.WriteLine(value ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(
                ex, "Failed to write ADS metadata stream {Stream}.", streamPath);
        }
    }

    /// <summary>
    /// Reads the (key, value) lines previously written by
    /// <see cref="Write"/>, returning null when the stream is absent or
    /// unreadable. Lines without a '=' separator are skipped silently so a
    /// malformed entry can't poison the whole upload.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? TryRead(
        string filePath, string streamName, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(streamName)) return null;

        var streamPath = $"{filePath}:{streamName}";

        // Pre-check via GetFileAttributes so a brand-new local file with no
        // metadata stream attached doesn't raise a FileNotFoundException on every
        // upload — the previous catch was correct but noisy under "break on
        // first-chance exceptions".
        if (!StreamExists(streamPath)) return null;

        try
        {
            using var fs = new FileStream(
                streamPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Utf8NoBom);

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                var sep = line.IndexOf('=');
                if (sep <= 0) continue;
                var key = line[..sep];
                var value = line[(sep + 1)..];
                dict[key] = value;
            }
            return dict.Count == 0 ? null : dict;
        }
        catch (FileNotFoundException)
        {
            // No stream attached yet — treat as "no metadata recorded".
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            // Path itself is gone (e.g. file was deleted concurrently).
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(
                ex, "Failed to read ADS metadata stream {Stream}.", streamPath);
            return null;
        }
    }

    /// <summary>
    /// Best-effort removal of an ADS. The existence check up front avoids the
    /// usual <see cref="FileNotFoundException"/> noise from <c>File.Delete</c>
    /// when there was nothing to drop in the first place.
    /// </summary>
    private static void TryDeleteStream(string streamPath, ILogger? logger)
    {
        if (!StreamExists(streamPath)) return;

        try
        {
            File.Delete(streamPath);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to delete ADS metadata stream {Stream}.", streamPath);
        }
    }

    /// <summary>
    /// Win32 sentinel returned by <c>GetFileAttributesW</c> when the path
    /// (including any ADS suffix) does not resolve to an entry.
    /// </summary>
    private const uint InvalidFileAttributes = 0xFFFFFFFF;

    /// <summary>
    /// True when an entry exists at <paramref name="streamPath"/>. Works for
    /// both ordinary file paths and the <c>file:streamName</c> form because
    /// <c>GetFileAttributesW</c> resolves the named stream directly. Uses a
    /// Win32 call instead of <see cref="File.Exists(string?)"/> because the
    /// managed helper rejects paths whose colon is not in the drive position.
    /// </summary>
    private static bool StreamExists(string streamPath) =>
        GetFileAttributesW(streamPath) != InvalidFileAttributes;

    /// <summary>
    /// P/Invoke wrapper for kernel32!GetFileAttributesW; returns
    /// <see cref="InvalidFileAttributes"/> on any failure (most often
    /// "stream not found"). Native AOT-friendly because <c>LibraryImport</c>
    /// generates the marshalling at compile time.
    /// </summary>
    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFileAttributesW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial uint GetFileAttributesW(string lpFileName);

    /// <summary>
    /// UTF-8 encoder that does not emit a byte-order mark — keeps the stream
    /// content byte-for-byte identical to a plain text editor's view.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
