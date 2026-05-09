using OSVFS.ObjectStore.S3;

namespace OSVFS;

/// <summary>
/// Validates the multipart-upload knobs (<c>multipart-threshold</c> /
/// <c>multipart-part-size</c> in <c>osvfs.toml</c>) against the S3 per-part
/// bounds. Returns a human-readable error string on violation, or null when
/// the settings are acceptable. Both inputs may be null, meaning "use the
/// backend default".
/// </summary>
internal static class MultipartSettingsValidator
{
    /// <summary>
    /// Returns null when the supplied threshold and part size are valid, or a
    /// description of the first violation. Bytes-per-part must satisfy S3's
    /// 5 MiB &le; size &le; 5 GiB invariant; threshold must be positive.
    /// </summary>
    public static string? Validate(long? thresholdBytes, long? partSizeBytes)
    {
        if (thresholdBytes is <= 0)
        {
            return $"'multipart-threshold' must be positive (got {thresholdBytes}).";
        }
        if (partSizeBytes is { } size)
        {
            if (size < S3Backend.MinMultipartPartSizeBytes)
            {
                return $"'multipart-part-size' must be at least 5 MiB (got {size}).";
            }
            if (size > S3Backend.MaxMultipartPartSizeBytes)
            {
                return $"'multipart-part-size' must be at most 5 GiB (got {size}).";
            }
        }
        return null;
    }
}
