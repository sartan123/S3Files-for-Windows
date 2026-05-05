namespace S3Files.Windows.S3;

internal readonly record struct S3ObjectInfo(
    string Key,
    string RelativePath,
    long Size,
    DateTimeOffset LastModified,
    string ETag,
    bool IsDirectory);
