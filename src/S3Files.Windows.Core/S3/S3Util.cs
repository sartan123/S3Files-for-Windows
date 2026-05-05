using System.Text;

namespace S3Files.Windows.S3;

internal static class S3Util
{
    /// <summary>
    /// Length of the ProjFS contentId we derive from an ETag. ProjFS allows up to 128 bytes;
    /// 16 bytes is enough to make placeholders comparable across runs without bloating each
    /// placeholder's metadata.
    /// </summary>
    public const int ContentIdLength = 16;

    public static string ToS3Key(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? string.Empty : relativePath.Replace('\\', '/');

    public static string ToRelativePath(string s3Key) =>
        s3Key.Replace('/', '\\');

    public static string NormalizePrefix(string relativeDirectory)
    {
        var prefix = ToS3Key(relativeDirectory);
        return prefix.Length > 0 && !prefix.EndsWith('/') ? prefix + '/' : prefix;
    }

    /// <summary>
    /// Derives a stable, fixed-size ProjFS contentId from an S3 ETag. Surrounding quotes on
    /// the ETag (S3's wire format) are stripped before hashing into the buffer.
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
}
