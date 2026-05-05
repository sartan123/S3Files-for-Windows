namespace S3Files.Windows;

internal sealed class ProjFsProviderOptions
{
    public required string S3Bucket { get; init; }

    public required string VirtRoot { get; init; }

    public string? EndpointUrl { get; init; }

    public bool Verbose { get; init; }

    public bool ReadOnly { get; init; }
}
