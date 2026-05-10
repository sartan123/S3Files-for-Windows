namespace OSVFS.ObjectStore.AzureBlob;

/// <summary>
/// Azure-side counterpart of <see cref="AwsCredentialSource"/>. Carries the
/// resolution path the host picked through the provider-neutral
/// <see cref="IObjectStoreCredentialSource"/> seam, and surfaces the concrete
/// data the backend needs to construct the SDK clients.
/// </summary>
/// <remarks>
/// Step 2A only ships the connection-string branch; SAS / Managed Identity /
/// <c>DefaultAzureCredential</c> branches land in Step 2B (#52). The shape
/// (<see cref="ConnectionString"/> property + <see cref="Description"/>)
/// stays append-only so adding a branch does not force callers to recompile.
/// </remarks>
internal sealed class AzureCredentialSource : IObjectStoreCredentialSource
{
    /// <summary>
    /// Connection string carrying account name + key + endpoints, when this
    /// source represents the connection-string branch. Null on every other
    /// branch (added in Step 2B).
    /// </summary>
    public string? ConnectionString { get; }

    /// <summary>
    /// Human-readable description of the resolution path (e.g.
    /// <c>"connection string ('AccountName=...')"</c> or
    /// <c>"DefaultAzureCredential chain"</c>). Surfaced by the doctor and the
    /// mount-startup log message; mirrors the AWS-side wording so multi-cloud
    /// logs stay consistent.
    /// </summary>
    public string Description { get; }

    private AzureCredentialSource(string? connectionString, string description)
    {
        ConnectionString = connectionString;
        Description = description;
    }

    /// <summary>
    /// Wraps an Azure Storage connection string. The connection string is
    /// what Azurite hands out by default (<c>UseDevelopmentStorage=true</c>)
    /// and what most operators paste from the Azure Portal "Access keys"
    /// blade, so it is the lowest-friction starting point.
    /// </summary>
    public static AzureCredentialSource FromConnectionString(
        string connectionString, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new AzureCredentialSource(connectionString, description);
    }
}
