using OSVFS.ProjFs;
using System.Runtime.ExceptionServices;
using Xunit;

namespace OSVFS.UnitTests.ProjFs;

/// <summary>
/// Verifies that <see cref="AdsMetadataStore"/> persists and recovers a
/// key=value map through a real NTFS alternate data stream. Skipped on
/// non-NTFS volumes by xUnit when the underlying FileStream open fails.
/// </summary>
public sealed class AdsMetadataStoreTests
{
    [Fact]
    public void Write_then_TryRead_round_trips_the_dictionary()
    {
        using var file = new TempFile();
        var input = new Dictionary<string, string>
        {
            ["tag"] = "hello",
            ["author"] = "alice",
        };

        AdsMetadataStore.Write(file.Path, AdsMetadataStore.UserMetaStreamName, input);
        var roundTrip = AdsMetadataStore.TryRead(file.Path, AdsMetadataStore.UserMetaStreamName);

        Assert.NotNull(roundTrip);
        Assert.Equal("hello", roundTrip!["tag"]);
        Assert.Equal("alice", roundTrip["author"]);
    }

    [Fact]
    public void TryRead_returns_null_when_no_stream_attached()
    {
        using var file = new TempFile();

        var result = AdsMetadataStore.TryRead(file.Path, AdsMetadataStore.UserMetaStreamName);

        Assert.Null(result);
    }

    [Fact]
    public void TryRead_does_not_raise_first_chance_FileNotFoundException_for_missing_stream()
    {
        // Regression: brand-new local files (e.g., .python_history) have no
        // metadata stream; the helper used to throw FileNotFoundException
        // internally and rely on the catch to swallow it, which surfaced as
        // noise under "break on first-chance exceptions". The pre-check should
        // now bypass FileStream.Open entirely.
        using var file = new TempFile();
        var seen = new List<FirstChanceExceptionEventArgs>();
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, e) => seen.Add(e);
        AppDomain.CurrentDomain.FirstChanceException += handler;
        try
        {
            AdsMetadataStore.TryRead(file.Path, AdsMetadataStore.UserMetaStreamName);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        Assert.DoesNotContain(seen, e => e.Exception is FileNotFoundException);
    }

    [Fact]
    public void TryRead_returns_null_when_base_file_does_not_exist()
    {
        var nonexistent = Path.Combine(
            Path.GetTempPath(), "osvfs-ads-missing-" + Guid.NewGuid().ToString("N") + ".tmp");

        var result = AdsMetadataStore.TryRead(nonexistent, AdsMetadataStore.UserMetaStreamName);

        Assert.Null(result);
    }

    [Fact]
    public void Write_with_empty_dictionary_removes_existing_stream()
    {
        using var file = new TempFile();
        AdsMetadataStore.Write(
            file.Path,
            AdsMetadataStore.UserMetaStreamName,
            new Dictionary<string, string> { ["k"] = "v" });

        // Empty entries should drop the stream so a stale snapshot can't outlive its source.
        AdsMetadataStore.Write(
            file.Path, AdsMetadataStore.UserMetaStreamName, new Dictionary<string, string>());

        Assert.Null(AdsMetadataStore.TryRead(file.Path, AdsMetadataStore.UserMetaStreamName));
    }

    [Fact]
    public void Write_overwrites_previous_contents()
    {
        using var file = new TempFile();
        AdsMetadataStore.Write(
            file.Path,
            AdsMetadataStore.UserMetaStreamName,
            new Dictionary<string, string> { ["k"] = "v1" });

        AdsMetadataStore.Write(
            file.Path,
            AdsMetadataStore.UserMetaStreamName,
            new Dictionary<string, string> { ["k"] = "v2", ["other"] = "value" });

        var read = AdsMetadataStore.TryRead(file.Path, AdsMetadataStore.UserMetaStreamName);
        Assert.NotNull(read);
        Assert.Equal("v2", read!["k"]);
        Assert.Equal("value", read["other"]);
    }

    [Fact]
    public void TryRead_skips_lines_without_separator()
    {
        using var file = new TempFile();
        var streamPath = $"{file.Path}:{AdsMetadataStore.UserMetaStreamName}";
        File.WriteAllText(streamPath, "valid=ok\nbroken-line\n=missingkey\nnext=fine\n");

        var read = AdsMetadataStore.TryRead(file.Path, AdsMetadataStore.UserMetaStreamName);

        Assert.NotNull(read);
        Assert.Equal(2, read!.Count);
        Assert.Equal("ok", read["valid"]);
        Assert.Equal("fine", read["next"]);
    }

    /// <summary>
    /// Per-test temp file living on whichever drive Path.GetTempPath()
    /// resolves to. Auto-cleans on dispose, including any ADS attached to it.
    /// </summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "osvfs-ads-" + Guid.NewGuid().ToString("N") + ".tmp");

        public TempFile() => File.WriteAllText(Path, string.Empty);

        public void Dispose()
        {
            try { File.Delete(Path); }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }
}
