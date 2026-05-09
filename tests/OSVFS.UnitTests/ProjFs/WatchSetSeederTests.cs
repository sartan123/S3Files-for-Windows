using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.ProjFs;
using OSVFS.Sync;
using System.Runtime.CompilerServices;
using Xunit;

namespace OSVFS.UnitTests.ProjFs;

/// <summary>
/// Verifies that <see cref="WatchSetSeeder"/> reconstructs the on-demand watch
/// set after restart by walking placeholders left on disk by the prior process.
/// </summary>
public sealed class WatchSetSeederTests
{
    [Fact]
    public void Seed_registers_root_plus_every_subdirectory_relative_to_virt_root()
    {
        using var fs = new TempVirtRoot();
        fs.CreateDir("a");
        fs.CreateDir("a/b");
        fs.CreateDir("a/b/c");
        fs.CreateDir("d");

        var registrar = new RecordingRegistrar();
        var watcher = NewWatcher(registrar);

        var count = WatchSetSeeder.Seed(fs.Root, watcher, NullLogger.Instance);

        // 1 root + a, a/b, a/b/c, d → 5 entries
        Assert.Equal(5, count);
        Assert.Contains("", registrar.Registered);
        Assert.Contains("a", registrar.Registered);
        Assert.Contains(Path.Combine("a", "b"), registrar.Registered);
        Assert.Contains(Path.Combine("a", "b", "c"), registrar.Registered);
        Assert.Contains("d", registrar.Registered);
    }

    [Fact]
    public void Seed_skips_lost_and_found_directory()
    {
        using var fs = new TempVirtRoot();
        fs.CreateDir("a");
        fs.CreateDir(ObjectStoreChangeWatcher.LostAndFoundDirectoryName);
        fs.CreateDir($"{ObjectStoreChangeWatcher.LostAndFoundDirectoryName}/inside");

        var registrar = new RecordingRegistrar();
        var watcher = NewWatcher(registrar);

        WatchSetSeeder.Seed(fs.Root, watcher, NullLogger.Instance);

        Assert.Contains("", registrar.Registered);
        Assert.Contains("a", registrar.Registered);
        Assert.DoesNotContain(
            registrar.Registered,
            r => r.Contains(ObjectStoreChangeWatcher.LostAndFoundDirectoryName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Seed_returns_zero_when_source_does_not_support_directory_registration()
    {
        using var fs = new TempVirtRoot();
        fs.CreateDir("a");

        var watcher = NewWatcherWithoutRegistrar();

        var count = WatchSetSeeder.Seed(fs.Root, watcher, NullLogger.Instance);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Seed_returns_zero_when_virt_root_does_not_exist()
    {
        var registrar = new RecordingRegistrar();
        var watcher = NewWatcher(registrar);

        var count = WatchSetSeeder.Seed(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            watcher,
            NullLogger.Instance);

        Assert.Equal(0, count);
        Assert.Empty(registrar.Registered);
    }

    private static ObjectStoreChangeWatcher NewWatcher(RecordingRegistrar registrar)
    {
        var source = new RegistrarOnlyChangeSource(registrar);
        return new ObjectStoreChangeWatcher(
            source,
            new NoopSink(),
            new NoopQuarantine(),
            NullLogger<ObjectStoreChangeWatcher>.Instance);
    }

    private static ObjectStoreChangeWatcher NewWatcherWithoutRegistrar()
    {
        var source = new BareChangeSource();
        return new ObjectStoreChangeWatcher(
            source,
            new NoopSink(),
            new NoopQuarantine(),
            NullLogger<ObjectStoreChangeWatcher>.Instance);
    }

    /// <summary>
    /// Per-test temp tree used as a fake virt-root. Auto-cleans on dispose.
    /// </summary>
    private sealed class TempVirtRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "osvfs-seeder-" + Guid.NewGuid().ToString("N"));

        public TempVirtRoot() => Directory.CreateDirectory(Root);

        public void CreateDir(string relative) =>
            Directory.CreateDirectory(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// Captures every <see cref="IDirectoryWatchRegistrar.RegisterWatchedDirectory"/>
    /// call so the test can assert which paths the seeder discovered.
    /// </summary>
    private sealed class RecordingRegistrar
    {
        public List<string> Registered { get; } = new();

        public void Add(string relative) => Registered.Add(relative);
    }

    /// <summary>
    /// Source that exposes <see cref="IDirectoryWatchRegistrar"/> but never yields events.
    /// </summary>
    private sealed class RegistrarOnlyChangeSource(RecordingRegistrar registrar)
        : IChangeSource, IDirectoryWatchRegistrar
    {
        public int WatchedDirectoryCount => registrar.Registered.Count;

        public void RegisterWatchedDirectory(string relativeDirectory) =>
            registrar.Add(relativeDirectory);

        public IAsyncEnumerable<ObjectChangeEvent> WatchAsync(CancellationToken ct) =>
            EmptyAsync(ct);

        private static async IAsyncEnumerable<ObjectChangeEvent> EmptyAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Source that does NOT implement <see cref="IDirectoryWatchRegistrar"/> — used to
    /// drive the "watcher reports unsupported registration" branch.
    /// </summary>
    private sealed class BareChangeSource : IChangeSource
    {
        public IAsyncEnumerable<ObjectChangeEvent> WatchAsync(CancellationToken ct) =>
            EmptyAsync(ct);

        private static async IAsyncEnumerable<ObjectChangeEvent> EmptyAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            ct.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopSink : IProjFsCommandSink
    {
        public bool TryWritePlaceholder(
            string relativePath, long size, DateTimeOffset lastModified, byte[] contentId, bool isDirectory) => true;

        public ProjFsUpdateOutcome TryUpdateFile(
            string relativePath, long size, DateTimeOffset lastModified, byte[] contentId)
            => ProjFsUpdateOutcome.Updated;

        public ProjFsUpdateOutcome TryDeleteFile(string relativePath, bool allowDirty) =>
            ProjFsUpdateOutcome.Updated;
    }

    private sealed class NoopQuarantine : ILostAndFoundQuarantine
    {
        public bool TryQuarantine(string relativePath) => true;
    }
}
