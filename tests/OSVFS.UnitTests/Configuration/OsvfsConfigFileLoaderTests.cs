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
    public void Parse_legacy_single_mount_form_returns_one_default_mount_with_all_keys()
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
            max-concurrent-uploads = 6
            max-concurrent-downloads = 12
            max-multipart-parts = 20
            log-format = "json"
            """;

        var config = OsvfsConfigFileLoader.ParseContent(toml, "test.toml");

        Assert.True(config.Verbose);
        Assert.Equal(LogFormat.Json, config.LogFormat);
        var mount = Assert.Single(config.Mounts);
        Assert.Equal(OsvfsConfigFileLoader.LegacyDefaultMountName, mount.Name);
        Assert.Equal(ObjectStoreProvider.S3, mount.Provider);
        Assert.Equal("my-bucket", mount.Bucket);
        Assert.Equal("C:/mount", mount.RootFolder);
        Assert.Equal("http://localhost:4566", mount.EndpointUrl);
        Assert.Equal("ap-northeast-1", mount.Region);
        Assert.Equal("team-a/", mount.Prefix);
        Assert.True(mount.ReadOnly);
        Assert.Equal(15, mount.SyncIntervalSeconds);
        Assert.Equal("prod", mount.AwsProfile);
        Assert.Equal("5M", mount.BandwidthUp);
        Assert.Equal("10M", mount.BandwidthDown);
        Assert.Equal("16M", mount.MultipartThreshold);
        Assert.Equal("32M", mount.MultipartPartSize);
        Assert.Equal(6, mount.MaxConcurrentUploads);
        Assert.Equal(12, mount.MaxConcurrentDownloads);
        Assert.Equal(20, mount.MaxMultipartParts);
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
    public void Parse_legacy_form_accepts_snake_case_aliases()
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

        var mount = Assert.Single(config.Mounts);
        Assert.Equal("C:/r", mount.RootFolder);
        Assert.Equal("http://e", mount.EndpointUrl);
        Assert.True(mount.ReadOnly);
        Assert.Equal(7, mount.SyncIntervalSeconds);
        Assert.Equal("p", mount.AwsProfile);
        Assert.Equal("1M", mount.BandwidthUp);
        Assert.Equal("2M", mount.BandwidthDown);
        Assert.Equal("8M", mount.MultipartThreshold);
        Assert.Equal("16M", mount.MultipartPartSize);
    }

    [Fact]
    public void Parse_kebab_key_wins_over_snake_alias_when_both_present()
    {
        const string toml = """
            root-folder = "kebab"
            root_folder = "snake"
            """;

        var config = OsvfsConfigFileLoader.ParseContent(toml, "test.toml");

        var mount = Assert.Single(config.Mounts);
        Assert.Equal("kebab", mount.RootFolder);
    }

    [Fact]
    public void Parse_provider_is_case_insensitive()
    {
        var config = OsvfsConfigFileLoader.ParseContent("provider = \"S3\"", "test.toml");
        var mount = Assert.Single(config.Mounts);
        Assert.Equal(ObjectStoreProvider.S3, mount.Provider);
    }

    [Fact]
    public void Parse_unknown_provider_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("provider = \"sftp\"", "test.toml"));
        Assert.Contains("sftp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_telemetry_section_returns_full_record()
    {
        const string toml = """
            [telemetry]
            otlp-endpoint = "http://collector:4317"
            otlp-protocol = "http-protobuf"
            service-name = "osvfs-prod"
            """;

        var config = OsvfsConfigFileLoader.ParseContent(toml, "test.toml");

        Assert.NotNull(config.Telemetry);
        Assert.Equal("http://collector:4317", config.Telemetry!.OtlpEndpoint);
        Assert.Equal(OtlpProtocolKind.HttpProtobuf, config.Telemetry.OtlpProtocol);
        Assert.Equal("osvfs-prod", config.Telemetry.ServiceName);
    }

    [Fact]
    public void Parse_telemetry_section_accepts_snake_aliases()
    {
        const string toml = """
            [telemetry]
            otlp_endpoint = "http://collector:4318"
            otlp_protocol = "grpc"
            service_name = "osvfs-staging"
            """;

        var config = OsvfsConfigFileLoader.ParseContent(toml, "test.toml");

        Assert.NotNull(config.Telemetry);
        Assert.Equal("http://collector:4318", config.Telemetry!.OtlpEndpoint);
        Assert.Equal(OtlpProtocolKind.Grpc, config.Telemetry.OtlpProtocol);
        Assert.Equal("osvfs-staging", config.Telemetry.ServiceName);
    }

    [Fact]
    public void Parse_telemetry_unknown_protocol_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent(
                "[telemetry]\notlp-protocol = \"thrift\"", "test.toml"));
        Assert.Contains("thrift", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_no_telemetry_section_returns_null()
    {
        var config = OsvfsConfigFileLoader.ParseContent("verbose = true", "test.toml");
        Assert.Null(config.Telemetry);
    }

    [Fact]
    public void MergeOverlay_telemetry_overlay_wins_per_key()
    {
        var user = new OsvfsConfigFile
        {
            Telemetry = new OsvfsTelemetryConfig
            {
                OtlpEndpoint = "http://user:4317",
                OtlpProtocol = OtlpProtocolKind.Grpc,
                ServiceName = "user-name",
            },
        };
        var project = new OsvfsConfigFile
        {
            Telemetry = new OsvfsTelemetryConfig
            {
                OtlpEndpoint = "http://project:4317",
                // Protocol intentionally omitted to assert per-key precedence.
            },
        };

        var merged = user.MergeOverlay(project);

        Assert.NotNull(merged.Telemetry);
        Assert.Equal("http://project:4317", merged.Telemetry!.OtlpEndpoint);
        Assert.Equal(OtlpProtocolKind.Grpc, merged.Telemetry.OtlpProtocol);
        Assert.Equal("user-name", merged.Telemetry.ServiceName);
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
    public void Parse_empty_returns_no_mounts_and_null_process_fields()
    {
        var config = OsvfsConfigFileLoader.ParseContent(string.Empty, "test.toml");

        Assert.Null(config.Verbose);
        Assert.Null(config.LogFormat);
        Assert.Empty(config.Mounts);
    }

    [Fact]
    public void Parse_change_source_kebab_and_snake_aliases_accepted()
    {
        var kebab = OsvfsConfigFileLoader.ParseContent("change-source = \"events\"", "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent("change_source = \"polling\"", "test.toml");

        Assert.Equal(ChangeSourceKind.Events, Assert.Single(kebab.Mounts).ChangeSource);
        Assert.Equal(ChangeSourceKind.Polling, Assert.Single(snake.Mounts).ChangeSource);
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

        Assert.Equal(SyncMode.OnDemand, Assert.Single(kebab.Mounts).SyncMode);
        Assert.Equal(SyncMode.Full, Assert.Single(snake.Mounts).SyncMode);
    }

    [Fact]
    public void Parse_sync_mode_accepts_both_dashed_and_dashless_spellings()
    {
        var dashed = OsvfsConfigFileLoader.ParseContent("sync-mode = \"on-demand\"", "test.toml");
        var dashless = OsvfsConfigFileLoader.ParseContent("sync-mode = \"ondemand\"", "test.toml");

        Assert.Equal(SyncMode.OnDemand, Assert.Single(dashed.Mounts).SyncMode);
        Assert.Equal(SyncMode.OnDemand, Assert.Single(dashless.Mounts).SyncMode);
    }

    [Fact]
    public void Parse_sync_mode_is_case_insensitive()
    {
        var config = OsvfsConfigFileLoader.ParseContent("sync-mode = \"FULL\"", "test.toml");
        Assert.Equal(SyncMode.Full, Assert.Single(config.Mounts).SyncMode);
    }

    [Fact]
    public void Parse_unknown_sync_mode_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("sync-mode = \"streaming\"", "test.toml"));
        Assert.Contains("streaming", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeOverlay_sync_mode_overrides_user_mount()
    {
        // Process-level (verbose / log-format) merge per key; the mount list
        // wins outright when the project file has any entries.
        var user = new OsvfsConfigFile
        {
            Mounts = [new OsvfsMountConfig { SyncMode = SyncMode.OnDemand }],
        };
        var project = new OsvfsConfigFile
        {
            Mounts = [new OsvfsMountConfig { SyncMode = SyncMode.Full }],
        };

        var merged = user.MergeOverlay(project);

        var mount = Assert.Single(merged.Mounts);
        Assert.Equal(SyncMode.Full, mount.SyncMode);
    }

    [Fact]
    public void Parse_event_queue_kebab_and_snake_aliases_accepted()
    {
        var kebab = OsvfsConfigFileLoader.ParseContent("event-queue = \"q1\"", "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent("event_queue = \"q2\"", "test.toml");

        Assert.Equal("q1", Assert.Single(kebab.Mounts).EventQueue);
        Assert.Equal("q2", Assert.Single(snake.Mounts).EventQueue);
    }

    [Fact]
    public void Parse_allow_unversioned_kebab_and_snake_aliases_accepted()
    {
        var kebab = OsvfsConfigFileLoader.ParseContent("allow-unversioned = true", "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent("allow_unversioned = true", "test.toml");

        Assert.True(Assert.Single(kebab.Mounts).AllowUnversioned);
        Assert.True(Assert.Single(snake.Mounts).AllowUnversioned);
    }

    [Fact]
    public void Parse_retry_max_attempts_kebab_and_snake_aliases_accepted()
    {
        var kebab = OsvfsConfigFileLoader.ParseContent("retry-max-attempts = 5", "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent("retry_max_attempts = 7", "test.toml");

        Assert.Equal(5, Assert.Single(kebab.Mounts).RetryMaxAttempts);
        Assert.Equal(7, Assert.Single(snake.Mounts).RetryMaxAttempts);
    }

    [Fact]
    public void Parse_retry_max_attempts_type_mismatch_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("retry-max-attempts = \"5\"", "test.toml"));
        Assert.Contains("retry-max-attempts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_concurrency_settings_kebab_and_snake_aliases_accepted()
    {
        const string kebabToml = """
            max-concurrent-uploads = 8
            max-concurrent-downloads = 16
            max-multipart-parts = 20
            """;
        const string snakeToml = """
            max_concurrent_uploads = 2
            max_concurrent_downloads = 3
            max_multipart_parts = 4
            """;

        var kebab = OsvfsConfigFileLoader.ParseContent(kebabToml, "test.toml");
        var snake = OsvfsConfigFileLoader.ParseContent(snakeToml, "test.toml");

        var kebabMount = Assert.Single(kebab.Mounts);
        Assert.Equal(8, kebabMount.MaxConcurrentUploads);
        Assert.Equal(16, kebabMount.MaxConcurrentDownloads);
        Assert.Equal(20, kebabMount.MaxMultipartParts);

        var snakeMount = Assert.Single(snake.Mounts);
        Assert.Equal(2, snakeMount.MaxConcurrentUploads);
        Assert.Equal(3, snakeMount.MaxConcurrentDownloads);
        Assert.Equal(4, snakeMount.MaxMultipartParts);
    }

    [Fact]
    public void Parse_concurrency_settings_type_mismatch_throws()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("max-concurrent-uploads = \"4\"", "test.toml"));
        Assert.Contains("max-concurrent-uploads", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_concurrency_settings_rejected_at_root_when_mount_array_present()
    {
        // Stray top-level concurrency keys mixed with [[mount]] entries are
        // ambiguous; the loader rejects them rather than silently dropping.
        const string toml = """
            max-concurrent-uploads = 4

            [[mount]]
            name = "a"
            bucket = "x"
            root-folder = "C:/x"
            """;
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent(toml, "test.toml"));
        Assert.Contains("max-concurrent-uploads", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_mount_array_returns_named_entries_in_declaration_order()
    {
        const string toml = """
            verbose = true

            [[mount]]
            name = "personal"
            bucket = "my-personal"
            root-folder = "C:/mounts/personal"

            [[mount]]
            name = "work"
            bucket = "my-work"
            root-folder = "C:/mounts/work"
            prefix = "team-a/"
            """;

        var config = OsvfsConfigFileLoader.ParseContent(toml, "test.toml");

        Assert.True(config.Verbose);
        Assert.Equal(2, config.Mounts.Count);
        Assert.Equal("personal", config.Mounts[0].Name);
        Assert.Equal("my-personal", config.Mounts[0].Bucket);
        Assert.Equal("C:/mounts/personal", config.Mounts[0].RootFolder);
        Assert.Equal("work", config.Mounts[1].Name);
        Assert.Equal("my-work", config.Mounts[1].Bucket);
        Assert.Equal("team-a/", config.Mounts[1].Prefix);
    }

    [Fact]
    public void Parse_mount_array_without_explicit_name_synthesizes_indexed_name()
    {
        // Operators may forget to set 'name'; the loader tags such entries with
        // a 'mount[i]' fallback so the duplicate-name check (and CLI selection)
        // still has a stable handle.
        const string toml = """
            [[mount]]
            bucket = "first"

            [[mount]]
            bucket = "second"
            """;

        var config = OsvfsConfigFileLoader.ParseContent(toml, "test.toml");

        Assert.Equal(2, config.Mounts.Count);
        Assert.Equal("mount[0]", config.Mounts[0].Name);
        Assert.Equal("mount[1]", config.Mounts[1].Name);
    }

    [Fact]
    public void Parse_duplicate_mount_names_throws()
    {
        const string toml = """
            [[mount]]
            name = "alpha"
            bucket = "first"

            [[mount]]
            name = "alpha"
            bucket = "second"
            """;

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent(toml, "test.toml"));
        Assert.Contains("duplicate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("alpha", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_mount_array_alongside_root_level_keys_throws()
    {
        // Mixing the legacy single-mount form with the [[mount]] array would
        // make precedence ambiguous; the parser refuses to guess.
        const string toml = """
            bucket = "stray"

            [[mount]]
            name = "alpha"
            bucket = "alpha-bucket"
            """;

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent(toml, "test.toml"));
        Assert.Contains("bucket", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_mount_must_be_table_array()
    {
        // 'mount' as a single inline table (not [[mount]]) is rejected so the
        // operator gets an explicit error instead of having keys silently
        // disappear.
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.ParseContent("mount = \"oops\"", "test.toml"));
        Assert.Contains("mount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeOverlay_project_mount_list_replaces_user_when_project_has_entries()
    {
        var user = new OsvfsConfigFile
        {
            Verbose = false,
            Mounts =
            [
                new OsvfsMountConfig { Name = "user-only", Bucket = "user-bucket" },
            ],
        };
        var project = new OsvfsConfigFile
        {
            Verbose = true,
            Mounts =
            [
                new OsvfsMountConfig { Name = "alpha", Bucket = "alpha-bucket" },
                new OsvfsMountConfig { Name = "beta", Bucket = "beta-bucket" },
            ],
        };

        var merged = user.MergeOverlay(project);

        Assert.True(merged.Verbose);
        Assert.Equal(2, merged.Mounts.Count);
        Assert.Equal("alpha", merged.Mounts[0].Name);
        Assert.Equal("beta", merged.Mounts[1].Name);
    }

    [Fact]
    public void MergeOverlay_project_keeps_user_mounts_when_project_has_none()
    {
        var user = new OsvfsConfigFile
        {
            Mounts =
            [
                new OsvfsMountConfig { Name = "user-only", Bucket = "user-bucket" },
            ],
        };
        // Project sets a process-level field but no mounts; user mounts should
        // survive so the user can keep their default mount list across both
        // files.
        var project = new OsvfsConfigFile { Verbose = true };

        var merged = user.MergeOverlay(project);

        Assert.True(merged.Verbose);
        Assert.Equal("user-bucket", Assert.Single(merged.Mounts).Bucket);
    }

    [Fact]
    public void TryLoadFile_missing_path_returns_null()
    {
        Assert.Null(OsvfsConfigFileLoader.TryLoadFile(null));
        Assert.Null(OsvfsConfigFileLoader.TryLoadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".toml")));
    }

    [Fact]
    public void LoadFromPaths_returns_null_when_no_source_exists()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".toml");
        Assert.Null(OsvfsConfigFileLoader.LoadFromPaths(missing, missing, null));
    }

    [Fact]
    public void LoadFromPaths_user_legacy_mount_replaces_exe_local_legacy_mount()
    {
        using var fs = new TempFiles();
        var exeLocalPath = fs.Write("exe.toml", """
            bucket = "exe-bucket"
            region = "us-east-1"
            verbose = false
            """);
        var userPath = fs.Write("user.toml", """
            bucket = "user-bucket"
            verbose = true
            """);

        var merged = OsvfsConfigFileLoader.LoadFromPaths(exeLocalPath, userPath, null);

        Assert.NotNull(merged);
        // User-global keys overlay on top of the exe-adjacent baseline.
        Assert.True(merged.Verbose);
        var mount = Assert.Single(merged.Mounts);
        // User defines its own legacy mount, so the exe-adjacent file's region
        // is not carried over (mount lists merge as a unit, not field-by-field).
        Assert.Equal("user-bucket", mount.Bucket);
        Assert.Null(mount.Region);
    }

    [Fact]
    public void LoadFromPaths_returns_exe_local_when_user_and_cli_missing()
    {
        using var fs = new TempFiles();
        var exeLocalPath = fs.Write("exe.toml", "bucket = \"exe-only\"");

        var merged = OsvfsConfigFileLoader.LoadFromPaths(
            exeLocalPath, fs.PathFor("absent.toml"), null);

        Assert.NotNull(merged);
        Assert.Equal("exe-only", Assert.Single(merged.Mounts).Bucket);
    }

    [Fact]
    public void LoadFromPaths_cli_config_overrides_user_and_exe_local()
    {
        using var fs = new TempFiles();
        var exeLocalPath = fs.Write("exe.toml", """
            verbose = false
            bucket = "exe-bucket"
            """);
        var userPath = fs.Write("user.toml", "bucket = \"user-bucket\"");
        var cliPath = fs.Write("cli.toml", """
            verbose = true
            bucket = "cli-bucket"
            """);

        var merged = OsvfsConfigFileLoader.LoadFromPaths(exeLocalPath, userPath, cliPath);

        Assert.NotNull(merged);
        Assert.True(merged.Verbose);
        Assert.Equal("cli-bucket", Assert.Single(merged.Mounts).Bucket);
    }

    [Fact]
    public void LoadFromPaths_missing_cli_config_throws()
    {
        // The CLI flag is an explicit operator request; a missing file is a
        // configuration error rather than a silent fallback to the next
        // source.
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".toml");

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsConfigFileLoader.LoadFromPaths(null, null, missing));
        Assert.Contains("--config", ex.Message, StringComparison.Ordinal);
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
