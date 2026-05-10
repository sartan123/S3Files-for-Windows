using OSVFS.ObjectStore;
using Tomlyn;
using Tomlyn.Model;

namespace OSVFS.Configuration;

/// <summary>
/// Loads <c>osvfs.toml</c> from up to three sources and merges them in
/// increasing-priority order: the file shipped next to <c>osvfs.exe</c>, the
/// user-global <c>%APPDATA%\OSVFS\config.toml</c>, and the optional file
/// pointed at by <c>--config &lt;path&gt;</c>. Tomlyn is used through the
/// AOT-safe <see cref="TomlTable"/> API; reflection-based model binding is
/// avoided.
/// </summary>
internal static class OsvfsConfigFileLoader
{
    /// <summary>
    /// File name searched for next to <c>osvfs.exe</c>.
    /// </summary>
    public const string ExeLocalFileName = "osvfs.toml";

    /// <summary>
    /// File name searched for under <c>%APPDATA%\OSVFS\</c>.
    /// </summary>
    public const string UserFileName = "config.toml";

    /// <summary>
    /// Subdirectory under <c>%APPDATA%</c> that holds the user-global config.
    /// </summary>
    public const string UserFolderName = "OSVFS";

    /// <summary>
    /// Default mount name applied to the legacy single-mount form when no
    /// explicit <c>[[mount]]</c> array is present. Operators can address this
    /// mount on the CLI as <c>osvfs mount --name default</c>.
    /// </summary>
    public const string LegacyDefaultMountName = "default";

    /// <summary>
    /// Locates the standard config files and returns the merged result, or null
    /// when no source contributed any keys. Sources are merged in this priority
    /// order (later sources override earlier ones on a per-key basis):
    /// <list type="number">
    ///   <item><description>The file shipped next to <c>osvfs.exe</c> (lowest priority — acts as a baseline / packaged default).</description></item>
    ///   <item><description><c>%APPDATA%\OSVFS\config.toml</c> (user-global overrides).</description></item>
    ///   <item><description><paramref name="cliConfigPath"/> when supplied via <c>--config &lt;path&gt;</c> (highest priority).</description></item>
    /// </list>
    /// A missing exe-adjacent or user-global file is silently skipped; a missing
    /// <paramref name="cliConfigPath"/> throws <see cref="OsvfsConfigException"/>
    /// because the operator explicitly asked for it.
    /// </summary>
    public static OsvfsConfigFile? LoadFromDefaultLocations(string? cliConfigPath = null)
        => LoadFromPaths(GetExeLocalConfigPath(), GetUserConfigPath(), cliConfigPath);

    /// <summary>
    /// Returns the absolute path to <c>%APPDATA%\OSVFS\config.toml</c>, or null
    /// when the <c>APPDATA</c> environment variable is not set (non-Windows hosts
    /// or stripped service accounts).
    /// </summary>
    public static string? GetUserConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData)
            ? null
            : Path.Combine(appData, UserFolderName, UserFileName);
    }

    /// <summary>
    /// Returns the absolute path to <c>osvfs.toml</c> next to <c>osvfs.exe</c>.
    /// Uses <see cref="AppContext.BaseDirectory"/> so the lookup is independent
    /// of the operator's current working directory and works the same in both
    /// Native AOT publish output and <c>dotnet run</c>.
    /// </summary>
    public static string GetExeLocalConfigPath()
        => Path.Combine(AppContext.BaseDirectory, ExeLocalFileName);

    /// <summary>
    /// Loads each non-null source in turn and overlays them in increasing
    /// priority order: <paramref name="exeLocalPath"/> first, then
    /// <paramref name="userPath"/>, then <paramref name="cliConfigPath"/>.
    /// Missing exe-adjacent or user-global files are silently skipped; a
    /// missing <paramref name="cliConfigPath"/> throws because the operator
    /// passed <c>--config</c> explicitly.
    /// </summary>
    public static OsvfsConfigFile? LoadFromPaths(
        string? exeLocalPath, string? userPath, string? cliConfigPath)
    {
        var exeLocal = TryLoadFile(exeLocalPath);
        var user = TryLoadFile(userPath);
        var cli = LoadCliConfigFile(cliConfigPath);

        OsvfsConfigFile? merged = null;
        Overlay(ref merged, exeLocal);
        Overlay(ref merged, user);
        Overlay(ref merged, cli);
        return merged;
    }

    /// <summary>
    /// Loads the file requested via <c>--config</c>. Unlike the default
    /// sources, a missing path is treated as a fatal user error.
    /// </summary>
    private static OsvfsConfigFile? LoadCliConfigFile(string? cliConfigPath)
    {
        if (string.IsNullOrEmpty(cliConfigPath)) return null;
        if (!File.Exists(cliConfigPath))
        {
            throw new OsvfsConfigException(
                $"OSVFS config file '{cliConfigPath}' (passed via --config) does not exist.");
        }
        return TryLoadFile(cliConfigPath);
    }

    /// <summary>
    /// Folds <paramref name="overlay"/> on top of <paramref name="merged"/>,
    /// keeping <paramref name="merged"/> as the running accumulator. A null
    /// overlay leaves the accumulator untouched.
    /// </summary>
    private static void Overlay(ref OsvfsConfigFile? merged, OsvfsConfigFile? overlay)
    {
        if (overlay is null) return;
        merged = merged is null ? overlay : merged.MergeOverlay(overlay);
    }

    /// <summary>
    /// Reads and parses a single TOML file. Returns null when <paramref name="path"/>
    /// is null or the file does not exist; throws <see cref="OsvfsConfigException"/>
    /// when the file is unreadable or contains TOML syntax errors.
    /// </summary>
    public static OsvfsConfigFile? TryLoadFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OsvfsConfigException($"Failed to read OSVFS config file '{path}': {ex.Message}", ex);
        }

        return ParseContent(content, path);
    }

    /// <summary>
    /// Parses raw TOML text. Exposed for unit tests so they do not need real
    /// files. Both the modern <c>[[mount]]</c> array form and the legacy
    /// flat (single-mount) form are accepted; the legacy form synthesizes a
    /// single mount entry named <c>"default"</c>.
    /// </summary>
    public static OsvfsConfigFile ParseContent(string content, string sourcePath)
    {
        var doc = Toml.Parse(content, sourcePath);
        if (doc.HasErrors)
        {
            var first = doc.Diagnostics[0];
            throw new OsvfsConfigException(
                $"OSVFS config file '{sourcePath}' has syntax errors: {first.Message} at {first.Span}");
        }

        var table = doc.ToModel();

        var mounts = ReadMountArray(table, sourcePath);
        if (mounts.Count == 0)
        {
            // Legacy / backward-compat path: keys at the root of the file describe a
            // single mount. Promote them into a synthetic "default" entry so the
            // rest of the host code only ever has to deal with the array shape.
            var legacy = ReadMountFromTable(table, sourcePath, LegacyDefaultMountName);
            if (HasAnyMountField(legacy))
            {
                mounts = [legacy];
            }
        }
        else
        {
            EnsureMountFieldsAbsentAtRoot(table, sourcePath);
        }

        EnsureMountNamesUnique(mounts, sourcePath);

        return new OsvfsConfigFile
        {
            Verbose = ReadBool(table, "verbose", sourcePath),
            LogFormat = ReadLogFormat(table, sourcePath),
            Mounts = mounts,
            Telemetry = ReadTelemetry(table, sourcePath),
        };
    }

    /// <summary>
    /// Reads the <c>[telemetry]</c> sub-table when present. Returns null
    /// when the section is absent so the merged config keeps the lower
    /// layer's value untouched. All recognized keys are nullable inside
    /// the returned record so a partial section can still merge cleanly.
    /// </summary>
    private static OsvfsTelemetryConfig? ReadTelemetry(TomlTable table, string sourcePath)
    {
        if (!table.TryGetValue("telemetry", out var raw) || raw is null)
        {
            return null;
        }
        if (raw is not TomlTable section)
        {
            throw new OsvfsConfigException(
                $"OSVFS config file '{sourcePath}': 'telemetry' must be a TOML table, " +
                $"got {raw.GetType().Name}.");
        }

        return new OsvfsTelemetryConfig
        {
            OtlpEndpoint = ReadString(section, "otlp-endpoint", "otlp_endpoint", sourcePath),
            OtlpProtocol = ReadOtlpProtocol(section, sourcePath),
            ServiceName = ReadString(section, "service-name", "service_name", sourcePath),
            MetricsListen = ReadString(section, "metrics-listen", "metrics_listen", sourcePath),
        };
    }

    /// <summary>
    /// Reads <c>otlp-protocol</c> as a case-insensitive enum literal.
    /// Accepts <c>grpc</c> / <c>http-protobuf</c> (kebab) and falls
    /// through to the standard enum names.
    /// </summary>
    private static OtlpProtocolKind? ReadOtlpProtocol(TomlTable table, string sourcePath)
    {
        var raw = ReadString(table, "otlp-protocol", "otlp_protocol", sourcePath);
        if (raw is null) return null;
        var normalized = raw.Replace("-", string.Empty);
        if (Enum.TryParse<OtlpProtocolKind>(normalized, ignoreCase: true, out var parsed))
            return parsed;
        throw new OsvfsConfigException(
            $"OSVFS config file '{sourcePath}': unknown otlp-protocol '{raw}'. Expected one of: " +
            "grpc, http-protobuf.");
    }

    /// <summary>
    /// Reads the <c>[[mount]]</c> array of tables when present; returns an
    /// empty list otherwise. Each entry is parsed as an
    /// <see cref="OsvfsMountConfig"/> with all non-mount keys (<c>verbose</c>,
    /// <c>log-format</c>) intentionally rejected so misplacement is caught
    /// at parse time.
    /// </summary>
    private static List<OsvfsMountConfig> ReadMountArray(TomlTable table, string sourcePath)
    {
        if (!table.TryGetValue("mount", out var raw) || raw is null)
        {
            return [];
        }
        if (raw is not TomlTableArray array)
        {
            throw new OsvfsConfigException(
                $"OSVFS config file '{sourcePath}': 'mount' must be a TOML table array ([[mount]]), " +
                $"got {raw.GetType().Name}.");
        }

        var result = new List<OsvfsMountConfig>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var entry = array[i];
            // The array entries themselves are TomlTables; index them as such so the
            // shared key readers can be reused for both the legacy root-level form and
            // each [[mount]] sub-table.
            var fallbackName = $"mount[{i}]";
            result.Add(ReadMountFromTable(entry, sourcePath, fallbackName));
        }
        return result;
    }

    /// <summary>
    /// Materializes a <see cref="OsvfsMountConfig"/> from a TOML table. The
    /// table is interpreted as either the legacy root document (when
    /// <paramref name="defaultName"/> is the legacy default) or a single
    /// <c>[[mount]]</c> entry, so the same helper can drive both code paths.
    /// </summary>
    private static OsvfsMountConfig ReadMountFromTable(TomlTable table, string sourcePath, string defaultName)
    {
        var name = ReadString(table, "name", sourcePath) ?? defaultName;
        return new OsvfsMountConfig
        {
            Name = name,
            Provider = ReadProvider(table, sourcePath),
            Bucket = ReadString(table, "bucket", sourcePath),
            RootFolder = ReadString(table, "root-folder", "root_folder", sourcePath),
            EndpointUrl = ReadString(table, "endpoint-url", "endpoint_url", sourcePath),
            Region = ReadString(table, "region", sourcePath),
            Prefix = ReadString(table, "prefix", sourcePath),
            ReadOnly = ReadBool(table, "read-only", "read_only", sourcePath),
            SyncIntervalSeconds = ReadInt(table, "sync-interval-seconds", "sync_interval_seconds", sourcePath),
            ChangeSource = ReadChangeSource(table, sourcePath),
            SyncMode = ReadSyncMode(table, sourcePath),
            EventQueue = ReadString(table, "event-queue", "event_queue", sourcePath),
            AwsProfile = ReadString(table, "aws-profile", "aws_profile", sourcePath),
            ConnectionString = ReadString(table, "connection-string", "connection_string", sourcePath),
            BandwidthUp = ReadString(table, "bandwidth-up", "bandwidth_up", sourcePath),
            BandwidthDown = ReadString(table, "bandwidth-down", "bandwidth_down", sourcePath),
            MultipartThreshold = ReadString(table, "multipart-threshold", "multipart_threshold", sourcePath),
            MultipartPartSize = ReadString(table, "multipart-part-size", "multipart_part_size", sourcePath),
            RetryMaxAttempts = ReadInt(table, "retry-max-attempts", "retry_max_attempts", sourcePath),
            MaxConcurrentUploads = ReadInt(table, "max-concurrent-uploads", "max_concurrent_uploads", sourcePath),
            MaxConcurrentDownloads = ReadInt(table, "max-concurrent-downloads", "max_concurrent_downloads", sourcePath),
            MaxMultipartParts = ReadInt(table, "max-multipart-parts", "max_multipart_parts", sourcePath),
            AllowUnversioned = ReadBool(table, "allow-unversioned", "allow_unversioned", sourcePath),
        };
    }

    /// <summary>
    /// True when the synthesized legacy mount has at least one field set;
    /// distinguishes "no mount keys at all" (return empty list) from "operator
    /// is using the legacy single-mount form" (return single entry).
    /// </summary>
    private static bool HasAnyMountField(OsvfsMountConfig mount) =>
        mount.Provider is not null
        || mount.Bucket is not null
        || mount.RootFolder is not null
        || mount.EndpointUrl is not null
        || mount.Region is not null
        || mount.Prefix is not null
        || mount.ReadOnly is not null
        || mount.SyncIntervalSeconds is not null
        || mount.ChangeSource is not null
        || mount.SyncMode is not null
        || mount.EventQueue is not null
        || mount.AwsProfile is not null
        || mount.ConnectionString is not null
        || mount.BandwidthUp is not null
        || mount.BandwidthDown is not null
        || mount.MultipartThreshold is not null
        || mount.MultipartPartSize is not null
        || mount.RetryMaxAttempts is not null
        || mount.MaxConcurrentUploads is not null
        || mount.MaxConcurrentDownloads is not null
        || mount.MaxMultipartParts is not null
        || mount.AllowUnversioned is not null;

    /// <summary>
    /// When <c>[[mount]]</c> entries are declared, refuse to silently merge
    /// stray top-level mount keys (e.g. a top-level <c>bucket = "..."</c>
    /// alongside the array): the precedence would be ambiguous and the
    /// operator likely intended one form or the other.
    /// </summary>
    private static void EnsureMountFieldsAbsentAtRoot(TomlTable table, string sourcePath)
    {
        // Keys that belong to a mount and would silently be ignored if mixed
        // with [[mount]]. 'name' is fine at the root only inside an entry,
        // never at the document root, so it's listed here too.
        ReadOnlySpan<string> rootMountKeys =
        [
            "name",
            "provider",
            "bucket",
            "root-folder", "root_folder",
            "endpoint-url", "endpoint_url",
            "region",
            "prefix",
            "read-only", "read_only",
            "sync-interval-seconds", "sync_interval_seconds",
            "change-source", "change_source",
            "sync-mode", "sync_mode",
            "event-queue", "event_queue",
            "aws-profile", "aws_profile",
            "connection-string", "connection_string",
            "bandwidth-up", "bandwidth_up",
            "bandwidth-down", "bandwidth_down",
            "multipart-threshold", "multipart_threshold",
            "multipart-part-size", "multipart_part_size",
            "retry-max-attempts", "retry_max_attempts",
            "max-concurrent-uploads", "max_concurrent_uploads",
            "max-concurrent-downloads", "max_concurrent_downloads",
            "max-multipart-parts", "max_multipart_parts",
            "allow-unversioned", "allow_unversioned",
        ];

        foreach (var key in rootMountKeys)
        {
            if (table.ContainsKey(key))
            {
                throw new OsvfsConfigException(
                    $"OSVFS config file '{sourcePath}': mount key '{key}' is set at the document " +
                    "root alongside [[mount]] entries. Move the key inside one of the [[mount]] " +
                    "tables, or remove the [[mount]] array to use the legacy single-mount form.");
            }
        }
    }

    /// <summary>
    /// Mount names address individual entries on the CLI; duplicates are
    /// rejected at parse time so an ambiguous <c>--name</c> never reaches
    /// runtime.
    /// </summary>
    private static void EnsureMountNamesUnique(List<OsvfsMountConfig> mounts, string sourcePath)
    {
        if (mounts.Count < 2) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in mounts)
        {
            if (!seen.Add(mount.Name))
            {
                throw new OsvfsConfigException(
                    $"OSVFS config file '{sourcePath}': duplicate mount name '{mount.Name}'. " +
                    "Each [[mount]] entry must have a unique 'name'.");
            }
        }
    }

    /// <summary>
    /// Returns the value at <paramref name="key"/> coerced to a string, or null
    /// when absent. Throws <see cref="OsvfsConfigException"/> on type mismatch.
    /// </summary>
    private static string? ReadString(TomlTable table, string key, string sourcePath)
    {
        if (!table.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            null => null,
            string s => s,
            _ => throw new OsvfsConfigException(
                $"OSVFS config file '{sourcePath}': key '{key}' must be a string, got {raw.GetType().Name}."),
        };
    }

    /// <summary>
    /// Reads a string under either <paramref name="primaryKey"/> or
    /// <paramref name="aliasKey"/>; the primary form wins on collision.
    /// </summary>
    private static string? ReadString(TomlTable table, string primaryKey, string aliasKey, string sourcePath)
        => ReadString(table, primaryKey, sourcePath) ?? ReadString(table, aliasKey, sourcePath);

    /// <summary>
    /// Returns the value at <paramref name="key"/> coerced to a bool, or null when absent.
    /// </summary>
    private static bool? ReadBool(TomlTable table, string key, string sourcePath)
    {
        if (!table.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            null => null,
            bool b => b,
            _ => throw new OsvfsConfigException(
                $"OSVFS config file '{sourcePath}': key '{key}' must be a boolean, got {raw.GetType().Name}."),
        };
    }

    /// <summary>
    /// Reads a bool under either the primary or alias key.
    /// </summary>
    private static bool? ReadBool(TomlTable table, string primaryKey, string aliasKey, string sourcePath)
        => ReadBool(table, primaryKey, sourcePath) ?? ReadBool(table, aliasKey, sourcePath);

    /// <summary>
    /// Returns the value at <paramref name="key"/> coerced to an int, or null when absent.
    /// </summary>
    private static int? ReadInt(TomlTable table, string key, string sourcePath)
    {
        if (!table.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            null => null,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            int i => i,
            _ => throw new OsvfsConfigException(
                $"OSVFS config file '{sourcePath}': key '{key}' must be an integer in the Int32 range."),
        };
    }

    /// <summary>
    /// Reads an int under either the primary or alias key.
    /// </summary>
    private static int? ReadInt(TomlTable table, string primaryKey, string aliasKey, string sourcePath)
        => ReadInt(table, primaryKey, sourcePath) ?? ReadInt(table, aliasKey, sourcePath);

    /// <summary>
    /// Reads <c>provider</c> as a case-insensitive enum literal. Accepts the same
    /// tokens as the <c>--provider</c> CLI flag.
    /// </summary>
    private static ObjectStoreProvider? ReadProvider(TomlTable table, string sourcePath)
    {
        var raw = ReadString(table, "provider", sourcePath);
        if (raw is null) return null;
        if (Enum.TryParse<ObjectStoreProvider>(raw, ignoreCase: true, out var parsed))
            return parsed;
        throw new OsvfsConfigException(
            $"OSVFS config file '{sourcePath}': unknown provider '{raw}'. Expected one of: " +
            string.Join(", ", Enum.GetNames<ObjectStoreProvider>()));
    }

    /// <summary>
    /// Reads <c>log-format</c> as a case-insensitive enum literal. Accepts the same
    /// tokens as the <c>--log-format</c> CLI flag.
    /// </summary>
    private static LogFormat? ReadLogFormat(TomlTable table, string sourcePath)
    {
        var raw = ReadString(table, "log-format", "log_format", sourcePath);
        if (raw is null) return null;
        if (Enum.TryParse<LogFormat>(raw, ignoreCase: true, out var parsed))
            return parsed;
        throw new OsvfsConfigException(
            $"OSVFS config file '{sourcePath}': unknown log-format '{raw}'. Expected one of: " +
            string.Join(", ", Enum.GetNames<LogFormat>()).ToLowerInvariant());
    }

    /// <summary>
    /// Reads <c>change-source</c> as a case-insensitive enum literal. Accepts the
    /// same tokens as the <c>--change-source</c> CLI flag.
    /// </summary>
    private static ChangeSourceKind? ReadChangeSource(TomlTable table, string sourcePath)
    {
        var raw = ReadString(table, "change-source", "change_source", sourcePath);
        if (raw is null) return null;
        if (Enum.TryParse<ChangeSourceKind>(raw, ignoreCase: true, out var parsed))
            return parsed;
        throw new OsvfsConfigException(
            $"OSVFS config file '{sourcePath}': unknown change-source '{raw}'. Expected one of: " +
            string.Join(", ", Enum.GetNames<ChangeSourceKind>()).ToLowerInvariant());
    }

    /// <summary>
    /// Reads <c>sync-mode</c> as a case-insensitive enum literal. Accepts the
    /// same tokens as the <c>--sync-mode</c> CLI flag (<c>on-demand</c> / <c>full</c>).
    /// </summary>
    private static SyncMode? ReadSyncMode(TomlTable table, string sourcePath)
    {
        var raw = ReadString(table, "sync-mode", "sync_mode", sourcePath);
        if (raw is null) return null;
        // Accept the kebab-case literal "on-demand" used on the CLI; the enum form has no dash.
        var normalized = raw.Replace("-", string.Empty);
        if (Enum.TryParse<SyncMode>(normalized, ignoreCase: true, out var parsed))
            return parsed;
        throw new OsvfsConfigException(
            $"OSVFS config file '{sourcePath}': unknown sync-mode '{raw}'. Expected one of: on-demand, full.");
    }
}

/// <summary>
/// Thrown when an OSVFS config file is unreadable, malformed, or contains
/// unsupported values. Surfaced to <c>Program.cs</c> as a fatal startup error.
/// </summary>
internal sealed class OsvfsConfigException : Exception
{
    /// <summary>
    /// Initializes a new instance with the supplied human-readable message.
    /// </summary>
    public OsvfsConfigException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance wrapping a lower-level I/O or parser failure.
    /// </summary>
    public OsvfsConfigException(string message, Exception inner) : base(message, inner) { }
}
