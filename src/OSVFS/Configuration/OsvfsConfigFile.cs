namespace OSVFS.Configuration;

/// <summary>
/// Strongly-typed view of an <c>osvfs.toml</c> file. Splits the file into
/// process-level settings (<see cref="Verbose"/>, <see cref="LogFormat"/> —
/// applied to the whole CLI invocation) and a list of per-mount entries in
/// <see cref="Mounts"/>. The legacy single-mount form (root-level
/// <c>bucket</c> / <c>root-folder</c> / etc.) is normalized into a single
/// <c>"default"</c> entry by the loader so callers always see a uniform shape.
/// </summary>
internal sealed class OsvfsConfigFile
{
    /// <summary>
    /// When true, raises log verbosity to Debug. Process-wide setting that
    /// applies to every mount started from this configuration.
    /// </summary>
    public bool? Verbose { get; init; }

    /// <summary>
    /// Console log output format. Null falls back to <see cref="LogFormat.Text"/>.
    /// Process-wide.
    /// </summary>
    public LogFormat? LogFormat { get; init; }

    /// <summary>
    /// Mount entries declared in the file. Order is preserved from the source
    /// document so <c>mount-all</c> starts mounts in declaration order.
    /// </summary>
    public IReadOnlyList<OsvfsMountConfig> Mounts { get; init; } = [];

    /// <summary>
    /// Optional <c>[telemetry]</c> section. Null when the file omits it
    /// (telemetry stays off in that case).
    /// </summary>
    public OsvfsTelemetryConfig? Telemetry { get; init; }

    /// <summary>
    /// Returns a copy of this config with values from <paramref name="overlay"/> taking
    /// precedence wherever they are non-null. Process-level fields merge per
    /// key; the <see cref="Mounts"/> list from <paramref name="overlay"/> wins
    /// outright when non-empty (project-local config typically defines the
    /// authoritative mount set), otherwise the user-global list is preserved.
    /// </summary>
    public OsvfsConfigFile MergeOverlay(OsvfsConfigFile overlay) => new()
    {
        Verbose = overlay.Verbose ?? Verbose,
        LogFormat = overlay.LogFormat ?? LogFormat,
        Mounts = overlay.Mounts.Count > 0 ? overlay.Mounts : Mounts,
        Telemetry = MergeTelemetry(Telemetry, overlay.Telemetry),
    };

    /// <summary>
    /// Folds the overlay's telemetry block onto the base. Either side
    /// being null collapses to the other; both non-null delegates to
    /// <see cref="OsvfsTelemetryConfig.MergeOverlay"/>.
    /// </summary>
    private static OsvfsTelemetryConfig? MergeTelemetry(
        OsvfsTelemetryConfig? @base, OsvfsTelemetryConfig? overlay)
    {
        if (@base is null) return overlay;
        if (overlay is null) return @base;
        return @base.MergeOverlay(overlay);
    }
}
