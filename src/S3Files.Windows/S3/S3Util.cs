namespace S3Files.Windows.S3;

internal static class S3Util
{
    public static string ToS3Key(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? string.Empty : relativePath.Replace('\\', '/');

    public static string ToRelativePath(string s3Key) =>
        s3Key.Replace('/', '\\');

    public static string NormalizePrefix(string relativeDirectory)
    {
        var prefix = ToS3Key(relativeDirectory);
        return prefix.Length > 0 && !prefix.EndsWith('/') ? prefix + '/' : prefix;
    }
}
