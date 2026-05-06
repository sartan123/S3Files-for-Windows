namespace S3Files.Windows;

internal sealed class ProjFsProviderOptions
{
    public required string S3Bucket { get; init; }

    public required string VirtRoot { get; init; }

    public string? EndpointUrl { get; init; }

    public string? Region { get; init; }

    public string? KeyPrefix { get; init; }

    public bool Verbose { get; init; }

    public bool ReadOnly { get; init; }

    public int SyncIntervalSeconds { get; init; } = 30;
}
