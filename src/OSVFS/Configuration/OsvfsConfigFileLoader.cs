using OSVFS.ObjectStore;
using Tomlyn;
using Tomlyn.Model;

namespace OSVFS.Configuration;

/// <summary>
/// Loads <c>osvfs.toml</c> from the project-local working directory and the
/// user-global <c>%APPDATA%\OSVFS\config.toml</c>, then merges them with the
/// project file taking precedence. Tomlyn is used through the AOT-safe
/// <see cref="TomlTable"/> API; reflection-based model binding is avoided.
/// </summary>
internal static class OsvfsConfigFileLoader
{
    /// <summary>
    /// File name searched for in the current working directory.
    /// </summary>
    public const string ProjectFileName = "osvfs.toml";

    /// <summary>
    /// File name searched for under <c>%APPDATA%\OSVFS\</c>.
    /// </summary>
    public const string UserFileName = "config.toml";

    /// <summary>
    /// Subdirectory under <c>%APPDATA%</c> that holds the user-global config.
    /// </summary>
    public const string UserFolderName = "OSVFS";

    /// <summary>
    /// Locates the standard config files and returns the merged result, or null
    /// when neither file exists. Project-local entries override user-global ones.
    /// </summary>
    public static OsvfsConfigFile? LoadFromDefaultLocations()
        => LoadFromPaths(GetUserConfigPath(), GetProjectConfigPath());

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
    /// Returns the absolute path to <c>./osvfs.toml</c> in the current working
    /// directory.
    /// </summary>
    public static string GetProjectConfigPath()
        => Path.Combine(Environment.CurrentDirectory, ProjectFileName);

    /// <summary>
    /// Loads zero, one, or both files in priority order (user file applied first,
    /// project file overlaid on top) and returns the merged config. Missing files
    /// are silently skipped; malformed files throw <see cref="OsvfsConfigException"/>.
    /// </summary>
    public static OsvfsConfigFile? LoadFromPaths(string? userPath, string? projectPath)
    {
        var userConfig = TryLoadFile(userPath);
        var projectConfig = TryLoadFile(projectPath);

        if (userConfig is null && projectConfig is null) return null;
        if (userConfig is null) return projectConfig;
        if (projectConfig is null) return userConfig;
        return userConfig.MergeOverlay(projectConfig);
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
    /// Parses raw TOML text. Exposed for unit tests so they do not need real files.
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
        return new OsvfsConfigFile
        {
            Provider = ReadProvider(table, sourcePath),
            Bucket = ReadString(table, "bucket", sourcePath),
            RootFolder = ReadString(table, "root-folder", "root_folder", sourcePath),
            EndpointUrl = ReadString(table, "endpoint-url", "endpoint_url", sourcePath),
            Region = ReadString(table, "region", sourcePath),
            Prefix = ReadString(table, "prefix", sourcePath),
            Verbose = ReadBool(table, "verbose", sourcePath),
            ReadOnly = ReadBool(table, "read-only", "read_only", sourcePath),
            SyncIntervalSeconds = ReadInt(table, "sync-interval-seconds", "sync_interval_seconds", sourcePath),
            ChangeSource = ReadChangeSource(table, sourcePath),
            SyncMode = ReadSyncMode(table, sourcePath),
            EventQueue = ReadString(table, "event-queue", "event_queue", sourcePath),
            AwsProfile = ReadString(table, "aws-profile", "aws_profile", sourcePath),
            BandwidthUp = ReadString(table, "bandwidth-up", "bandwidth_up", sourcePath),
            BandwidthDown = ReadString(table, "bandwidth-down", "bandwidth_down", sourcePath),
            MultipartThreshold = ReadString(table, "multipart-threshold", "multipart_threshold", sourcePath),
            MultipartPartSize = ReadString(table, "multipart-part-size", "multipart_part_size", sourcePath),
            LogFormat = ReadLogFormat(table, sourcePath),
            AllowUnversioned = ReadBool(table, "allow-unversioned", "allow_unversioned", sourcePath),
        };
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
