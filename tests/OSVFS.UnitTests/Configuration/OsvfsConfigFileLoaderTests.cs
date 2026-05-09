using OSVFS.Configuration;
using OSVFS.ObjectStore;
using Xunit;

namespace OSVFS.UnitTests.Configuration;

/// <summary>
/// Exercises the TOML parser, alias handling, and project-vs-user file priority logic
/// driving <see cref="OsvfsConfigFileLoader"/>. Real files are written to a per-test
/// temp directory to also cover the disk paths.
/// </summary>
public class OsvfsConfigFileLoaderTests
{
    [Fact]
    public void Parse_returns_all_known_kebab_case_keys()
    {
        const string toml = """
            provider = "s3"
            bucket = "my-bucket"
            root-folder = "C:/mount"
            endpoint-url = "http://localhost:4566"
            region = "ap-northeast-1"
            prefix = "team-a/"
            verbose = true
            read-only = true
            sync-interval-seconds = 15
            aws-profile = "prod"
            bandwidth-up = "5M"
            bandwidth-down = "10M"
            multipart-threshold = "16M"
            multipart-part-size = "32M"
            log-format = "json"
            """;

        var config = OsvfsConfigFileLoader.ParseContent(toml, "test.toml");

        Assert.Equal(ObjectStoreProvider.S3, config.Provider);
        Assert.Equal("my-bucket", config.Bucket);
        Assert.Equal("C:/mount", config.RootFolder);
        Assert.Equal("http://localhost:4566", config.EndpointUrl);
        Assert.Equal("ap-northeast-1", config.Region);
        Assert.Equal("team-a/", config.Prefix);
        Assert.True(config.Verbose);
        Assert.True(config.ReadOnly);
        Assert.Equal(15, config.SyncIntervalSeconds);
        Assert.Equal("prod", config.AwsProfile);
        Assert.Equal("5M", config.BandwidthUp);
        Assert.Equal("10M", config.BandwidthDown);
        Assert.Equal("16M", config.MultipartThreshold);
        Assert.Equal("32M", config.MultipartPartSize);
        Assert.Equal(LogFormat.Json, config.LogFormat);
    }

    [Fact]
    public void Parse_log_format_is_case_insensitive()
    {
        var config = OsvfsConfigFileLoader.ParseContent("log-format = \"JSON\"", "test.toml");
        Assert.Equal(LogFormat.Json, config.LogFormat);
    }

    [Fact]
    public void Parse_log_format_snake_alias_accepted()
    {
        var config = OsvfsConfigFileLoader.ParseContent("log_format = \"text\"", "test.toml");
        Assert.Equal(LogFormat.Text, config.LogFormat);
    }

    [Fact]
    public void Parse_unknown_log_format_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("log-format = \"xml\"", "test.toml"));
        Assert.Contains("xml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeOverlay_log_format_overrides_user_value()
    {
        var user = new OsvfsConfigFile { LogFormat = LogFormat.Text };
        var project = new OsvfsConfigFile { LogFormat = LogFormat.Json };

        var merged = user.MergeOverlay(project);

        Assert.Equal(LogFormat.Json, merged.LogFormat);
    }

    [Fact]
    public void Parse_accepts_snake_case_aliases()
    {
        const string toml = """
            bucket = "b"
            root_folder = "C:/r"
            endpoint_url = "http://e"
            read_only = true
            sync_interval_seconds = 7
            aws_profile = "p"
            bandwidth_up = "1M"
            bandwidth_down = "2M"
            multipart_threshold = "8M"
            multipart_part_size = "16M"
            """;

        var config = OsvfsConfigFileLoader.ParseContent(toml, "test.toml");

        Assert.Equal("C:/r", config.RootFolder);
        Assert.Equal("http://e", config.EndpointUrl);
        Assert.True(config.ReadOnly);
        Assert.Equal(7, config.SyncIntervalSeconds);
        Assert.Equal("p", config.AwsProfile);
        Assert.Equal("1M", config.BandwidthUp);
        Assert.Equal("2M", config.BandwidthDown);
        Assert.Equal("8M", config.MultipartThreshold);
        Assert.Equal("16M", config.MultipartPartSize);
    }

    [Fact]
    public void Parse_kebab_key_wins_over_snake_alias_when_both_present()
    {
        const string toml = """
            root-folder = "kebab"
            root_folder = "snake"
            """;

        var config = OsvfsConfigFileLoader.ParseContent(toml, "test.toml");

        Assert.Equal("kebab", config.RootFolder);
    }

    [Fact]
    public void Parse_provider_is_case_insensitive()
    {
        var config = OsvfsConfigFileLoader.ParseContent("provider = \"S3\"", "test.toml");
        Assert.Equal(ObjectStoreProvider.S3, config.Provider);
    }

    [Fact]
    public void Parse_unknown_provider_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("provider = \"sftp\"", "test.toml"));
        Assert.Contains("sftp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_type_mismatch_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("bucket = 42", "test.toml"));
        Assert.Contains("bucket", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_malformed_toml_throws()
    {
        Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("bucket = ", "test.toml"));
    }

    [Fact]
    public void Parse_empty_returns_all_nulls()
    {
        var config = OsvfsConfigFileLoader.ParseContent(string.Empty, "test.toml");

        Assert.Null(config.Provider);
        Assert.Null(config.Bucket);
        Assert.Null(config.RootFolder);
        Assert.Null(config.Verbose);
        Assert.Null(config.SyncIntervalSeconds);
        Assert.Null(config.AllowUnversioned);
    }

    [Fact]
    public void Parse_change_source_kebab_and_snake_aliases_accepted()
    {
        var kebab = OsvfsConfigFileLoader.ParseContent("change-source = \"events\"", "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent("change_source = \"polling\"", "test.toml");

        Assert.Equal(ChangeSourceKind.Events, kebab.ChangeSource);
        Assert.Equal(ChangeSourceKind.Polling, snake.ChangeSource);
    }

    [Fact]
    public void Parse_unknown_change_source_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("change-source = \"webhook\"", "test.toml"));
        Assert.Contains("webhook", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_sync_mode_kebab_and_snake_aliases_accepted()
    {
        var kebab = OsvfsConfigFileLoader.ParseContent("sync-mode = \"on-demand\"", "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent("sync_mode = \"full\"", "test.toml");

        Assert.Equal(SyncMode.OnDemand, kebab.SyncMode);
        Assert.Equal(SyncMode.Full, snake.SyncMode);
    }

    [Fact]
    public void Parse_sync_mode_accepts_both_dashed_and_dashless_spellings()
    {
        var dashed = OsvfsConfigFileLoader.ParseContent("sync-mode = \"on-demand\"", "test.toml");
        var dashless = OsvfsConfigFileLoader.ParseContent("sync-mode = \"ondemand\"", "test.toml");

        Assert.Equal(SyncMode.OnDemand, dashed.SyncMode);
        Assert.Equal(SyncMode.OnDemand, dashless.SyncMode);
    }

    [Fact]
    public void Parse_sync_mode_is_case_insensitive()
    {
        var config = OsvfsConfigFileLoader.ParseContent("sync-mode = \"FULL\"", "test.toml");
        Assert.Equal(SyncMode.Full, config.SyncMode);
    }

    [Fact]
    public void Parse_unknown_sync_mode_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("sync-mode = \"streaming\"", "test.toml"));
        Assert.Contains("streaming", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeOverlay_sync_mode_overrides_user_value()
    {
        var user = new OsvfsConfigFile { SyncMode = SyncMode.OnDemand };
        var project = new OsvfsConfigFile { SyncMode = SyncMode.Full };

        var merged = user.MergeOverlay(project);

        Assert.Equal(SyncMode.Full, merged.SyncMode);
    }

    [Fact]
    public void Parse_event_queue_kebab_and_snake_aliases_accepted()
    {
        var kebab = OsvfsConfigFileLoader.ParseContent("event-queue = \"q1\"", "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent("event_queue = \"q2\"", "test.toml");

        Assert.Equal("q1", kebab.EventQueue);
        Assert.Equal("q2", snake.EventQueue);
    }

    [Fact]
    public void Parse_allow_unversioned_kebab_and_snake_aliases_accepted()
    {
        var kebab = OsvfsConfigFileLoader.ParseContent("allow-unversioned = true", "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent("allow_unversioned = true", "test.toml");

        Assert.True(kebab.AllowUnversioned);
        Assert.True(snake.AllowUnversioned);
    }

    [Fact]
    public void Parse_retry_max_attempts_kebab_and_snake_aliases_accepted()
    {
        var kebab = OsvfsConfigFileLoader.ParseContent("retry-max-attempts = 5", "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent("retry_max_attempts = 7", "test.toml");

        Assert.Equal(5, kebab.RetryMaxAttempts);
        Assert.Equal(7, snake.RetryMaxAttempts);
    }

    [Fact]
    public void Parse_retry_max_attempts_type_mismatch_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("retry-max-attempts = \"5\"", "test.toml"));
        Assert.Contains("retry-max-attempts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeOverlay_retry_max_attempts_overrides_user_value()
    {
        var user = new OsvfsConfigFile { RetryMaxAttempts = 3 };
        var project = new OsvfsConfigFile { RetryMaxAttempts = 8 };

        var merged = user.MergeOverlay(project);

        Assert.Equal(8, merged.RetryMaxAttempts);
    }

    [Fact]
    public void MergeOverlay_overlay_overrides_user_per_key()
    {
        var user = new OsvfsConfigFile { Bucket = "user", Region = "us-east-1" };
        var project = new OsvfsConfigFile { Bucket = "project", Verbose = true };

        var merged = user.MergeOverlay(project);

        Assert.Equal("project", merged.Bucket);
        Assert.Equal("us-east-1", merged.Region);
        Assert.True(merged.Verbose);
    }

    [Fact]
    public void TryLoadFile_missing_path_returns_null()
    {
        Assert.Null(OsvfsConfigFileLoader.TryLoadFile(null));
        Assert.Null(OsvfsConfigFileLoader.TryLoadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".toml")));
    }

    [Fact]
    public void LoadFromPaths_returns_null_when_neither_file_exists()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".toml");
        Assert.Null(OsvfsConfigFileLoader.LoadFromPaths(missing, missing));
    }

    [Fact]
    public void LoadFromPaths_project_file_overrides_user_file_per_key()
    {
        using var fs = new TempFiles();
        var userPath = fs.Write("user.toml", """
            bucket = "user-bucket"
            region = "us-east-1"
            verbose = false
            """);
        var projectPath = fs.Write("project.toml", """
            bucket = "project-bucket"
            verbose = true
            """);

        var merged = OsvfsConfigFileLoader.LoadFromPaths(userPath, projectPath);

        Assert.NotNull(merged);
        Assert.Equal("project-bucket", merged.Bucket);
        Assert.Equal("us-east-1", merged.Region);
        Assert.True(merged.Verbose);
    }

    [Fact]
    public void LoadFromPaths_returns_user_file_when_project_missing()
    {
        using var fs = new TempFiles();
        var userPath = fs.Write("user.toml", "bucket = \"user-only\"");

        var merged = OsvfsConfigFileLoader.LoadFromPaths(userPath, fs.PathFor("absent.toml"));

        Assert.NotNull(merged);
        Assert.Equal("user-only", merged.Bucket);
    }

    /// <summary>
    /// Per-test temp directory that auto-cleans on dispose; keeps file-system tests
    /// isolated from each other and from the user's real %APPDATA%/CWD.
    /// </summary>
    private sealed class TempFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "osvfs-cfg-" + Guid.NewGuid().ToString("N"));

        public TempFiles() => Directory.CreateDirectory(_root);

        public string PathFor(string fileName) => Path.Combine(_root, fileName);

        public string Write(string fileName, string content)
        {
            var path = PathFor(fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
