using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.Sync;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Xunit;

namespace OSVFS.Core.UnitTests;

public sealed class ObjectStoreChangeWatcherTests
{
    [Fact]
    public void Apply_upsert_routes_through_TryUpdateFile()
    {
        var sink = new RecordingSink();
        var watcher = NewWatcher(new FakeChangeSource(), sink, new RecordingQuarantine());

        watcher.ApplyForTesting(Upsert("file.txt", "etag-1", 100));

        var call = Assert.Single(sink.Calls);
        Assert.Equal("UpdateFile", call.Op);
        Assert.Equal("file.txt", call.Path);
        Assert.Equal(100, call.Size);
    }

    [Fact]
    public void Apply_upsert_when_TryUpdateFile_returns_NotFound_falls_back_to_WritePlaceholder()
    {
        var sink = new RecordingSink { UpdateFileResult = ProjFsUpdateOutcome.NotFound };
        var watcher = NewWatcher(new FakeChangeSource(), sink, new RecordingQuarantine());

        watcher.ApplyForTesting(Upsert("docs/new.txt", "etag", 5));

        Assert.Collection(
            sink.Calls,
            c => Assert.Equal("UpdateFile", c.Op),
            c => { Assert.Equal("WritePlaceholder", c.Op); Assert.Equal(5, c.Size); });
    }

    [Fact]
    public void Apply_delete_routes_through_TryDeleteFile_with_disallow_dirty()
    {
        var sink = new RecordingSink();
        var watcher = NewWatcher(new FakeChangeSource(), sink, new RecordingQuarantine());

        watcher.ApplyForTesting(Delete("gone.txt"));

        var call = Assert.Single(sink.Calls);
        Assert.Equal("DeleteFile", call.Op);
        Assert.Equal("gone.txt", call.Path);
        Assert.False(call.AllowDirty);
    }

    [Fact]
    public void Conflict_on_upsert_quarantines_then_force_replaces()
    {
        var sink = new RecordingSink { UpdateFileResult = ProjFsUpdateOutcome.DirtyConflict };
        var quarantine = new RecordingQuarantine();
        var watcher = NewWatcher(new FakeChangeSource(), sink, quarantine);

        watcher.ApplyForTesting(Upsert("conflict.txt", "etag-new", 99));

        Assert.Equal(new[] { "conflict.txt" }, quarantine.Quarantined);
        Assert.Collection(
            sink.Calls,
            c => Assert.Equal("UpdateFile", c.Op),
            c => { Assert.Equal("DeleteFile", c.Op); Assert.True(c.AllowDirty); },
            c => { Assert.Equal("WritePlaceholder", c.Op); Assert.Equal(99, c.Size); });
    }

    [Fact]
    public void Conflict_on_delete_quarantines_and_force_deletes_without_recreating()
    {
        var sink = new RecordingSink { DeleteFileResult = ProjFsUpdateOutcome.DirtyConflict };
        sink.OverrideOnNthDelete[2] = ProjFsUpdateOutcome.Updated;
        var quarantine = new RecordingQuarantine();
        var watcher = NewWatcher(new FakeChangeSource(), sink, quarantine);

        watcher.ApplyForTesting(Delete("doomed.txt"));

        Assert.Equal(new[] { "doomed.txt" }, quarantine.Quarantined);
        Assert.Collection(
            sink.Calls,
            c => { Assert.Equal("DeleteFile", c.Op); Assert.False(c.AllowDirty); },
            c => { Assert.Equal("DeleteFile", c.Op); Assert.True(c.AllowDirty); });
    }

    [Fact]
    public void RecordLocalUpload_suppresses_matching_event_emitted_immediately_after()
    {
        // Self-suppression covers the SQS-style case where the events stream loops our own
        // write back at us. The watcher's recent-mutation map drops events whose ETag matches
        // a recent local upload.
        var sink = new RecordingSink();
        var watcher = NewWatcher(new FakeChangeSource(), sink, new RecordingQuarantine());

        watcher.RecordLocalUpload("local.txt", "etag-x", 12, DateTimeOffset.UtcNow);
        watcher.ApplyForTesting(Upsert("local.txt", "etag-x", 12));

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void RecordLocalUpload_does_not_suppress_event_with_different_etag()
    {
        var sink = new RecordingSink();
        var watcher = NewWatcher(new FakeChangeSource(), sink, new RecordingQuarantine());

        watcher.RecordLocalUpload("local.txt", "etag-old", 12, DateTimeOffset.UtcNow);
        watcher.ApplyForTesting(Upsert("local.txt", "etag-new", 99));

        var call = Assert.Single(sink.Calls);
        Assert.Equal("UpdateFile", call.Op);
        Assert.Equal(99, call.Size);
    }

    [Fact]
    public void RecordLocalDelete_suppresses_matching_delete_event()
    {
        var sink = new RecordingSink();
        var watcher = NewWatcher(new FakeChangeSource(), sink, new RecordingQuarantine());

        watcher.RecordLocalDelete("doomed.txt");
        watcher.ApplyForTesting(Delete("doomed.txt"));

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task StartAsync_pumps_events_from_the_source_until_disposed()
    {
        var source = new FakeChangeSource();
        var sink = new RecordingSink();
        var watcher = NewWatcher(source, sink, new RecordingQuarantine());

        await watcher.StartAsync(CancellationToken.None);
        await source.WriteAsync(Upsert("a.txt", "e1", 1));
        await source.WriteAsync(Delete("b.txt"));

        // Give the background pump time to drain.
        await FakeChangeSource.WaitForApplyCountAsync(sink, 2, TimeSpan.FromSeconds(5));
        await watcher.DisposeAsync();

        Assert.Collection(
            sink.Calls,
            c => Assert.Equal("UpdateFile", c.Op),
            c => Assert.Equal("DeleteFile", c.Op));
    }

    [Fact]
    public void RecordLocalUpload_forwards_to_the_underlying_recorder_when_present()
    {
        var source = new FakeChangeSource();
        var sink = new RecordingSink();
        var watcher = NewWatcher(source, sink, new RecordingQuarantine());

        watcher.RecordLocalUpload("k.txt", "e", 1, DateTimeOffset.UtcNow);

        Assert.Equal(new[] { "k.txt" }, source.RecordedUploads);
    }

    [Fact]
    public void RegisterWatchedDirectory_forwards_to_a_directory_aware_source()
    {
        var source = new RegistrarChangeSource();
        var watcher = NewWatcher(source, new RecordingSink(), new RecordingQuarantine());

        watcher.RegisterWatchedDirectory("docs");

        Assert.True(watcher.SupportsDirectoryWatchRegistration);
        Assert.Equal(new[] { "docs" }, source.Registered);
        Assert.Equal(1, watcher.WatchedDirectoryCount);
    }

    [Fact]
    public void RegisterWatchedDirectory_is_a_noop_when_the_source_does_not_implement_the_facet()
    {
        var source = new FakeChangeSource(); // ILocalMutationRecorder only — no registrar
        var watcher = NewWatcher(source, new RecordingSink(), new RecordingQuarantine());

        watcher.RegisterWatchedDirectory("docs");

        Assert.False(watcher.SupportsDirectoryWatchRegistration);
        Assert.Equal(0, watcher.WatchedDirectoryCount);
    }

    private static ObjectStoreChangeWatcher NewWatcher(
        IChangeSource source, IProjFsCommandSink sink, ILostAndFoundQuarantine quarantine)
        => new(
            source,
            sink,
            quarantine,
            NullLogger<ObjectStoreChangeWatcher>.Instance);

    private static ObjectChangeEvent Upsert(string key, string etag, long size) => new(
        Kind: ObjectChangeKind.Upserted,
        Key: key,
        RelativePath: key.Replace('/', '\\'),
        Size: size,
        LastModified: DateTimeOffset.UtcNow,
        ETag: etag);

    private static ObjectChangeEvent Delete(string key) => new(
        Kind: ObjectChangeKind.Deleted,
        Key: key,
        RelativePath: key.Replace('/', '\\'),
        Size: 0,
        LastModified: default,
        ETag: string.Empty);

    // ---- Test doubles -----------------------------------------------------------------

    /// <summary>
    /// Channel-backed change source. Tests push events with WriteAsync; the watcher
    /// drains the channel via WatchAsync. Also tracks ILocalMutationRecorder
    /// forwarding so we can assert RecordLocal* round-trips through the watcher.
    /// </summary>
    private sealed class FakeChangeSource : IChangeSource, ILocalMutationRecorder
    {
        private readonly Channel<ObjectChangeEvent> channel =
            Channel.CreateUnbounded<ObjectChangeEvent>();

        public List<string> RecordedUploads { get; } = new();
        public List<string> RecordedDeletes { get; } = new();

        public ValueTask WriteAsync(ObjectChangeEvent ev) => channel.Writer.WriteAsync(ev);

        public IAsyncEnumerable<ObjectChangeEvent> WatchAsync(CancellationToken ct) =>
            ReadAllAsync(ct);

        private async IAsyncEnumerable<ObjectChangeEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(ct))
            {
                yield return ev;
            }
        }

        public ValueTask DisposeAsync()
        {
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void RecordLocalUpload(string objectKey, string etag, long size, DateTimeOffset lastModified)
            => RecordedUploads.Add(objectKey);
        public void RecordLocalDelete(string objectKey) => RecordedDeletes.Add(objectKey);
        public void RecordLocalDeletePrefix(string objectKeyPrefix) { }
        public void RecordLocalRename(string oldKey, string newKey) { }
        public void RecordLocalRenamePrefix(string oldPrefix, string newPrefix) { }
        public IDisposable BeginLocalKeyChange(string objectKey) => Disposable.Empty;
        public IDisposable BeginLocalPrefixChange(string objectKeyPrefix) => Disposable.Empty;

        public static async Task WaitForApplyCountAsync(RecordingSink sink, int target, TimeSpan timeout)
        {
            var start = DateTime.UtcNow;
            while (sink.Calls.Count < target && DateTime.UtcNow - start < timeout)
            {
                await Task.Delay(10);
            }
        }
    }

    private sealed record SinkCall(string Op, string Path, long Size, bool AllowDirty);

    private sealed class RecordingSink : IProjFsCommandSink
    {
        private readonly object gate = new();
        public List<SinkCall> Calls { get; } = new();

        public bool WritePlaceholderResult { get; set; } = true;
        public ProjFsUpdateOutcome UpdateFileResult { get; set; } = ProjFsUpdateOutcome.Updated;
        public ProjFsUpdateOutcome DeleteFileResult { get; set; } = ProjFsUpdateOutcome.Updated;

        /// <summary>1-based: nth invocation of TryDeleteFile returns this outcome instead.</summary>
        public Dictionary<int, ProjFsUpdateOutcome> OverrideOnNthDelete { get; } = new();
        private int deleteCount;

        public bool TryWritePlaceholder(
            string relativePath, long size, DateTimeOffset lastModified, byte[] contentId, bool isDirectory)
        {
            lock (gate) { Calls.Add(new SinkCall("WritePlaceholder", relativePath, size, false)); }
            return WritePlaceholderResult;
        }

        public ProjFsUpdateOutcome TryUpdateFile(
            string relativePath, long size, DateTimeOffset lastModified, byte[] contentId)
        {
            lock (gate) { Calls.Add(new SinkCall("UpdateFile", relativePath, size, false)); }
            return UpdateFileResult;
        }

        public ProjFsUpdateOutcome TryDeleteFile(string relativePath, bool allowDirty)
        {
            int n;
            lock (gate)
            {
                deleteCount++;
                n = deleteCount;
                Calls.Add(new SinkCall("DeleteFile", relativePath, 0, allowDirty));
            }
            return OverrideOnNthDelete.TryGetValue(n, out var ov) ? ov : DeleteFileResult;
        }
    }

    private sealed class RecordingQuarantine : ILostAndFoundQuarantine
    {
        public List<string> Quarantined { get; } = new();
        public bool Result { get; set; } = true;

        public bool TryQuarantine(string relativePath)
        {
            Quarantined.Add(relativePath);
            return Result;
        }
    }

    private sealed class Disposable : IDisposable
    {
        public static readonly IDisposable Empty = new Disposable();
        public void Dispose() { }
    }

    /// <summary>
    /// Source-shaped fake that also implements <see cref="IDirectoryWatchRegistrar"/>
    /// so the watcher's forwarding path can be observed.
    /// </summary>
    private sealed class RegistrarChangeSource : IChangeSource, IDirectoryWatchRegistrar
    {
        public List<string> Registered { get; } = new();

        public int WatchedDirectoryCount => Registered.Count;

        public void RegisterWatchedDirectory(string relativeDirectory) =>
            Registered.Add(relativeDirectory);

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
}
