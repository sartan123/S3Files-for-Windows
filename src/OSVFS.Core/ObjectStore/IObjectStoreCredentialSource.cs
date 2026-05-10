namespace OSVFS.ObjectStore;

/// <summary>
/// Marker for a credential source that the host carries through
/// <c>ProjFsProviderOptions</c> to the active backend. Each provider has its
/// own concrete implementation — <see cref="AwsCredentialSource"/> for S3
/// today, with <c>GcsCredentialSource</c> / <c>AzureCredentialSource</c>
/// landing alongside the matching backends in Phase 2. Carrying the seam as
/// an opaque interface keeps host-level options provider-agnostic at compile
/// time; the backend factory casts the value to the shape its backend
/// expects, and a mismatch (e.g. GCS credentials handed to S3) fails loudly
/// at startup rather than corrupting an upload.
/// </summary>
internal interface IObjectStoreCredentialSource
{
    /// <summary>
    /// Human-readable description of the resolution path (e.g.
    /// <c>"OSVFS profile 'prod'"</c> or <c>"shared profile 'osvfs-login' (sso)"</c>).
    /// Surfaced by the doctor and the mount-startup log message; included on
    /// every credential source so the host can log it without knowing the
    /// concrete provider type.
    /// </summary>
    string Description { get; }
}
