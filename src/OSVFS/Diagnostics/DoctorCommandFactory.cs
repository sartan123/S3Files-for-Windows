using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using OSVFS.Configuration;
using OSVFS.Credentials;
using OSVFS.Diagnostics.Checks;
using OSVFS.ObjectStore;
using System.CommandLine;
using System.Runtime.Versioning;

namespace OSVFS.Diagnostics;

/// <summary>
/// Builds the <c>osvfs doctor</c> subcommand. The doctor performs a sequence
/// of read-only environment checks (ProjFS feature, ProjFS smoke test, S3
/// bucket access, bucket versioning, AWS credential resolution) and prints a
/// colored summary so an operator can triage "I can't mount" before opening
/// the actual provider.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DoctorCommandFactory
{
    /// <summary>
    /// Constructs the <c>doctor</c> command. Each <c>--bucket</c> /
    /// <c>--region</c> / <c>--profile</c> / <c>--endpoint-url</c> flag is
    /// optional: when omitted, the doctor falls back to the first mount
    /// declared in <c>osvfs.toml</c> so the operator can simply run
    /// <c>osvfs doctor</c> and have it pick up their existing config.
    /// </summary>
    public static Command Build(
        IAwsCredentialStore credentialStore, MountCliOptions cliOptions)
    {
        var bucket = new Option<string?>("--bucket")
        {
            Description =
                "Bucket to test against. Defaults to the first [[mount]] entry in osvfs.toml; " +
                "explicit value wins.",
        };
        var region = new Option<string?>("--region")
        {
            Description = "AWS region for the test client. Defaults to the matching mount in osvfs.toml.",
        };
        var profile = new Option<string?>("--profile")
        {
            Description =
                "OSVFS credential profile (set with 'osvfs credentials set'). When omitted, " +
                "the SDK's default chain (env, profile, IMDS) is used.",
        };
        var endpointUrl = new Option<string?>("--endpoint-url")
        {
            Description =
                "S3 endpoint override (LocalStack, MinIO). Defaults to the matching mount in " +
                "osvfs.toml.",
        };

        var command = new Command(
            "doctor",
            "Run environment self-checks (ProjFS, S3 bucket, credentials) and exit. " +
            "Returns 0 when everything passes, 2 when at least one check requires action.")
        {
            bucket,
            region,
            profile,
            endpointUrl,
        };
        cliOptions.AddTo(command);

        command.SetAction(parseResult => Run(
            parseResult, cliOptions, credentialStore, bucket, region, profile, endpointUrl));

        return command;
    }

    /// <summary>
    /// Action body for the <c>doctor</c> command. Materializes the inputs,
    /// builds every check in execution order, hands them to
    /// <see cref="OsvfsDoctor"/>, and renders the result.
    /// </summary>
    private static int Run(
        ParseResult parseResult,
        MountCliOptions cliOptions,
        IAwsCredentialStore credentialStore,
        Option<string?> bucketOption,
        Option<string?> regionOption,
        Option<string?> profileOption,
        Option<string?> endpointUrlOption)
    {
        var cliBucket = parseResult.GetValue(bucketOption);
        var cliRegion = parseResult.GetValue(regionOption);
        var cliProfile = parseResult.GetValue(profileOption);
        var cliEndpointUrl = parseResult.GetValue(endpointUrlOption);
        var cliConfigPath = parseResult.GetValue(cliOptions.ConfigPath);

        OsvfsConfigFile? fileConfig = null;
        try
        {
            fileConfig = OsvfsConfigFileLoader.LoadFromDefaultLocations(cliConfigPath);
        }
        catch (OsvfsConfigException ex)
        {
            // A broken config file should not stop the doctor from doing the rest of its
            // checks — note the parse failure and proceed without config-derived defaults.
            Console.Error.WriteLine($"Warning: ignoring osvfs.toml ({ex.Message})");
        }

        var firstMount = fileConfig?.Mounts.Count > 0 ? fileConfig.Mounts[0] : null;
        var bucket = cliBucket ?? firstMount?.Bucket;
        var region = cliRegion ?? firstMount?.Region;
        var endpointUrl = cliEndpointUrl ?? firstMount?.EndpointUrl;
        var profileName = cliProfile ?? firstMount?.AwsProfile;

        var (awsCredentials, credentialSource) =
            ResolveCredentials(credentialStore, profileName);

        // The AWS clients live for the duration of the doctor run only.
        AmazonS3Client? s3 = null;
        try
        {
            var checks = new List<IDoctorCheck>
            {
                new ProjFsFeatureCheck(),
                new ProjFsStartCheck(),
                new AwsCredentialsCheck(awsCredentials, credentialSource),
            };

            if (!string.IsNullOrEmpty(bucket))
            {
                s3 = BuildS3Client(awsCredentials, region, endpointUrl);
                checks.Add(new BucketAccessCheck(s3, bucket));
                checks.Add(new BucketVersioningCheck(s3, bucket));
            }
            else
            {
                checks.Add(new SkippedCheck(
                    "Bucket access (HeadBucket)",
                    "No bucket configured. Pass --bucket <name> or add a [[mount]] entry to osvfs.toml."));
                checks.Add(new SkippedCheck(
                    "Bucket versioning",
                    "Skipped because no bucket was supplied."));
            }

            var doctor = new OsvfsDoctor(checks);
            var results = doctor.RunAllAsync(CancellationToken.None).GetAwaiter().GetResult();
            DoctorRenderer.Render(results, Console.Out);
            return OsvfsDoctor.ToExitCode(results);
        }
        finally
        {
            s3?.Dispose();
        }
    }

    /// <summary>
    /// Resolves the AWS credentials the bucket-side checks should use. When
    /// <paramref name="profileName"/> matches an entry in the OSVFS DPAPI
    /// store, those credentials are returned; otherwise we hand over the
    /// SDK's <see cref="FallbackCredentialsFactory"/> which walks env / profile
    /// / IMDS in turn. Any resolution failure is allowed to bubble up; it is
    /// converted into a check failure inside <see cref="AwsCredentialsCheck"/>.
    /// </summary>
    private static (AWSCredentials Credentials, string Source) ResolveCredentials(
        IAwsCredentialStore store, string? profileName) =>
        ResolveCredentials(store, DefaultSharedProfileResolver.Instance, profileName);

    /// <summary>
    /// Test-friendly resolver overload that mirrors the production resolution chain
    /// (OSVFS DPAPI store → shared SDK profile via <paramref name="sharedProfileResolver"/>
    /// → SDK default credential chain) used by <see cref="MountOptionsBuilder"/>.
    /// </summary>
    internal static (AWSCredentials Credentials, string Source) ResolveCredentials(
        IAwsCredentialStore store, ISharedProfileResolver sharedProfileResolver, string? profileName)
    {
        if (!string.IsNullOrEmpty(profileName))
        {
            var stored = store.Load(profileName);
            if (stored is not null)
            {
                AWSCredentials creds = string.IsNullOrEmpty(stored.SessionToken)
                    ? new BasicAWSCredentials(stored.AccessKeyId, stored.SecretAccessKey)
                    : new SessionAWSCredentials(stored.AccessKeyId, stored.SecretAccessKey, stored.SessionToken);
                return (creds, $"OSVFS profile '{profileName}'");
            }

            var shared = sharedProfileResolver.Resolve(profileName);
            if (shared is not null)
            {
                return (shared.Credentials, shared.Description);
            }
            // Fall through: the named profile is unknown to both stores. Report the
            // SDK default chain so the operator sees what the bucket-side checks
            // will actually use, alongside the attempted profile name.
        }
        // DefaultAWSCredentialsIdentityResolver replaces FallbackCredentialsFactory in
        // AWSSDK v4 — it walks the same chain (env vars → profile → IMDS) and returns
        // a refreshing AWSCredentials wrapper.
        return (DefaultAWSCredentialsIdentityResolver.GetCredentials(null), "SDK default credential chain");
    }

    /// <summary>
    /// Builds a short-lived S3 client mirroring the conventions used by
    /// <c>S3Backend.CreateClient</c>: path-style + relaxed checksum negotiation
    /// when an endpoint override is set, RegionEndpoint applied for signing.
    /// </summary>
    private static AmazonS3Client BuildS3Client(
        AWSCredentials credentials, string? region, string? endpointUrl)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = !string.IsNullOrEmpty(endpointUrl),
        };
        if (!string.IsNullOrEmpty(endpointUrl))
        {
            config.ServiceURL = endpointUrl;
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        if (!string.IsNullOrEmpty(region))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }
        return new AmazonS3Client(credentials, config);
    }

    /// <summary>
    /// Tiny placeholder check used when a prerequisite (e.g. <c>--bucket</c>)
    /// was not supplied. Always reports <see cref="DoctorCheckStatus.Skipped"/>
    /// so the operator sees the missing input rather than a misleading "ok".
    /// </summary>
    private sealed class SkippedCheck : IDoctorCheck
    {
        public string Name { get; }

        private readonly string message;

        /// <summary>
        /// Constructs the skip note.
        /// </summary>
        public SkippedCheck(string name, string message)
        {
            Name = name;
            this.message = message;
        }

        /// <inheritdoc/>
        public Task<DoctorResult> RunAsync(CancellationToken ct) =>
            Task.FromResult(new DoctorResult(Name, DoctorCheckStatus.Skipped, message));
    }
}
