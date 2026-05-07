using System.Text;

namespace OSVFS.ObjectStore;

/// <summary>
/// Conversion helpers between Windows-style relative paths and object-store keys.
/// Forward-slash key separators are the convention shared by S3, GCS, and Azure Blob
/// (Azure uses them as virtual path separators inside flat blob names), so this util
/// is provider-neutral.
/// </summary>
internal static class KeyPath
{
    /// <summary>
    /// Length of the ProjFS contentId we derive from an ETag. ProjFS allows up to 128 bytes;
    /// 16 bytes is enough to make placeholders comparable across runs without bloating each
    /// placeholder's metadata.
    /// </summary>
    public const int ContentIdLength = 16;

    /// <summary>
    /// Converts a Windows relative path to an object-store key by swapping separators.
    /// </summary>
    public static string ToObjectKey(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? string.Empty : relativePath.Replace('\\', '/');

    /// <summary>
    /// Converts an object-store key to a Windows relative path by swapping separators.
    /// </summary>
    public static string ToRelativePath(string objectKey) =>
        objectKey.Replace('/', '\\');

    /// <summary>
    /// Normalizes a relative directory to a key prefix terminated with a trailing
    /// slash; the empty input is returned as the empty string.
    /// </summary>
    public static string NormalizePrefix(string relativeDirectory)
    {
        var prefix = ToObjectKey(relativeDirectory);
        return prefix.Length > 0 && !prefix.EndsWith('/') ? prefix + '/' : prefix;
    }

    /// <summary>
    /// Derives a stable, fixed-size ProjFS contentId from a provider ETag. Surrounding
    /// quotes (S3's wire format) are stripped before hashing into the buffer.
    /// </summary>
    public static byte[] BuildContentId(string? etag)
    {
        var result = new byte[ContentIdLength];
        if (string.IsNullOrEmpty(etag)) return result;

        var trimmed = etag.AsSpan().Trim('"');
        var byteCount = Encoding.UTF8.GetByteCount(trimmed);
        Span<byte> bytes = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(trimmed, bytes);
        bytes[..Math.Min(bytes.Length, result.Length)].CopyTo(result);
        return result;
    }

    /// <summary>
    /// Returns the trailing-slash-terminated linked prefix, or the empty string when
    /// no prefix is configured. Always lives in object-store key form (forward-slash separated).
    /// </summary>
    public static string NormalizeKeyPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return string.Empty;
        var trimmed = prefix.Replace('\\', '/').Trim('/');
        return trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>
    /// Maps a virt-root-relative key (or empty) to the full key applied against the
    /// bucket/container. The empty input maps to the prefix itself (typically used as a list root).
    /// </summary>
    public static string FullKey(string keyPrefix, string relativeKey) =>
        relativeKey.Length == 0 ? keyPrefix : keyPrefix + relativeKey;

    /// <summary>
    /// Maps a virt-root-relative directory to the full prefix used for List operations.
    /// The empty directory yields the linked prefix itself, so an empty bucket and an empty
    /// linked prefix both list the same way.
    /// </summary>
    public static string FullPrefix(string keyPrefix, string relativeDirectory) =>
        keyPrefix + NormalizePrefix(relativeDirectory);

    /// <summary>
    /// Strips the linked prefix back off a full key. Defensive: keys that don't
    /// start with the prefix are returned unchanged so a misrouted result is surfaced rather
    /// than silently mapped to a bogus relative path.
    /// </summary>
    public static string StripPrefix(string keyPrefix, string fullKey)
    {
        if (keyPrefix.Length == 0) return fullKey;
        return fullKey.StartsWith(keyPrefix, StringComparison.Ordinal)
            ? fullKey[keyPrefix.Length..]
            : fullKey;
    }
}
