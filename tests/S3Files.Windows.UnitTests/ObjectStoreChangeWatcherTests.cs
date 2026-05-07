using Microsoft.Extensions.Logging.Abstractions;
using S3Files.Windows.ObjectStore;
using S3Files.Windows.Sync;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xunit;

namespace S3Files.Windows.UnitTests;

public sealed class ObjectStoreChangeWatcherTests
{
    [Fact]
    public async Task Poll_with_no_changes_invokes_no_commands()
    {
        var backend = new FakeBackend();
        backend.Set("a.txt", "etag-a", 1);
        backend.Set("b.txt", "etag-b", 2);

        var sink = new RecordingSink();
        var quarantine = new RecordingQuarantine();
        var watcher = NewWatcher(backend, sink, quarantine);

        await watcher.PrimeSnapshotAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Poll_detects_new_object_and_writes_placeholder()
    {
        var backend = new FakeBackend();
        var sink = new RecordingSink();
        var watcher = NewWatcher(backend, sink, new RecordingQuarantine());

        await watcher.PrimeSnapshotAsync(CancellationToken.None);
        backend.Set("docs/new.txt", "etag-1", 5);
        await watcher.PollOnceAsync(CancellationToken.None);

        var call = Assert.Single(sink.Calls);
        Assert.Equal("WritePlaceholder", call.Op);
        Assert.Equal("docs\\new.txt", call.Path);
        Assert.Equal(5, call.Size);
    }

    [Fact]
    public async Task Poll_detects_modified_object_and_calls_update()
    {
        var backend = new FakeBackend();
        backend.Set("file.txt", "etag-old", 1);
        var sink = new RecordingSink();
        var watcher = NewWatcher(backend, sink, new RecordingQuarantine());

        await watcher.PrimeSnapshotAsync(CancellationToken.None);
        backend.Set("file.txt", "etag-new", 2);
        await watcher.PollOnceAsync(CancellationToken.None);

        var call = Assert.Single(sink.Calls);
        Assert.Equal("UpdateFile", call.Op);
        Assert.Equal("file.txt", call.Path);
        Assert.Equal(2, call.Size);
    }

    [Fact]
    public async Task Poll_detects_deleted_object_and_calls_delete()
    {
        var backend = new FakeBackend();
        backend.Set("gone.txt", "etag-1", 1);
        var sink = new RecordingSink();
        var watcher = NewWatcher(backend, sink, new RecordingQuarantine());

        await watcher.PrimeSnapshotAsync(CancellationToken.None);
        backend.Remove("gone.txt");
        await watcher.PollOnceAsync(CancellationToken.None);

        var call = Assert.Single(sink.Calls);
        Assert.Equal("DeleteFile", call.Op);
        Assert.Equal("gone.txt", call.Path);
        Assert.False(call.AllowDirty);
    }

    [Fact]
    public async Task Conflict_on_update_quarantines_and_force_replaces()
    {
        var backend = new FakeBackend();
        backend.Set("conflict.txt", "etag-old", 1);

        var sink = new RecordingSink();
        sink.UpdateFileResult = ProjFsUpdateOutcome.DirtyConflict;

        var quarantine = new RecordingQuarantine();
        var watcher = NewWatcher(backend, sink, quarantine);

        await watcher.PrimeSnapshotAsync(CancellationToken.None);
        backend.Set("conflict.txt", "etag-new", 99);
        await watcher.PollOnceAsync(CancellationToken.None);

        // Spec: the dirty local copy goes to lost+found, then the placeholder is force-deleted
        // and re-created with the remote version.
        Assert.Equal(new[] { "conflict.txt" }, quarantine.Quarantined);

        Assert.Collection(
            sink.Calls,
            c => Assert.Equal("UpdateFile", c.Op),
            c => { Assert.Equal("DeleteFile", c.Op); Assert.True(c.AllowDirty); },
            c => { Assert.Equal("WritePlaceholder", c.Op); Assert.Equal(99, c.Size); });
    }

    [Fact]
    public async Task Conflict_on_delete_quarantines_and_force_deletes_without_recreating()
    {
        var backend = new FakeBackend();
        backend.Set("doomed.txt", "etag-1", 1);

        var sink = new RecordingSink();
        sink.DeleteFileResult = ProjFsUpdateOutcome.DirtyConflict;

        var quarantine = new RecordingQuarantine();
        var watcher = NewWatcher(backend, sink, quarantine);

        await watcher.PrimeSnapshotAsync(CancellationToken.None);
        backend.Remove("doomed.txt");

        // Allow the second (force) delete to succeed even though the first is configured to conflict.
        sink.OverrideOnNthDelete[2] = ProjFsUpdateOutcome.Updated;
        await watcher.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "doomed.txt" }, quarantine.Quarantined);
        Assert.Collection(
            sink.Calls,
            c => { Assert.Equal("DeleteFile", c.Op); Assert.False(c.AllowDirty); },
            c => { Assert.Equal("DeleteFile", c.Op); Assert.True(c.AllowDirty); });
        // No placeholder re-creation: object is gone in the remote store.
    }

    [Fact]
    public async Task RecordLocalUpload_prevents_self_trigger_on_next_poll()
    {
        var backend = new FakeBackend();
        var sink = new RecordingSink();
        var watcher = NewWatcher(backend, sink, new RecordingQuarantine());

        await watcher.PrimeSnapshotAsync(CancellationToken.None);

        // Simulate: we just uploaded the file ourselves.
        var lastModified = DateTimeOffset.UtcNow;
        backend.Set("local.txt", "etag-from-upload", 42, lastModified);
        watcher.RecordLocalUpload("local.txt", "etag-from-upload", 42, lastModified);

        await watcher.PollOnceAsync(CancellationToken.None);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task BeginLocalKeyChange_skips_key_in_concurrent_poll()
    {
        var backend = new FakeBackend();
        backend.Set("inflight.txt", "etag-old", 1);

        var sink = new RecordingSink();
        var watcher = NewWatcher(backend, sink, new RecordingQuarantine());
        await watcher.PrimeSnapshotAsync(CancellationToken.None);

        // Remote ETag changed (because we are mid-upload), but our local-change token tells the
        // watcher to ignore this key for the current cycle.
        backend.Set("inflight.txt", "etag-new", 2);
        using (var _ = watcher.BeginLocalKeyChange("inflight.txt"))
        {
            await watcher.PollOnceAsync(CancellationToken.None);
        }

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task RecordLocalDeletePrefix_clears_snapshot_under_prefix()
    {
        var backend = new FakeBackend();
        backend.Set("dir/a.txt", "e1", 1);
        backend.Set("dir/sub/b.txt", "e2", 1);
        backend.Set("other.txt", "e3", 1);

        var sink = new RecordingSink();
        var watcher = NewWatcher(backend, sink, new RecordingQuarantine());
        await watcher.PrimeSnapshotAsync(CancellationToken.None);

        // Local prefix delete: pretend we removed "dir/" from the backend.
        backend.Remove("dir/a.txt");
        backend.Remove("dir/sub/b.txt");
        watcher.RecordLocalDeletePrefix("dir");

        await watcher.PollOnceAsync(CancellationToken.None);

        // No DeleteFile calls because we already accounted for our own prefix delete.
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task RecordLocalRename_transposes_snapshot()
    {
        var backend = new FakeBackend();
        backend.Set("src.txt", "etag-1", 7);

        var sink = new RecordingSink();
        var watcher = NewWatcher(backend, sink, new RecordingQuarantine());
        await watcher.PrimeSnapshotAsync(CancellationToken.None);

        backend.Remove("src.txt");
        backend.Set("dst.txt", "etag-1", 7);
        watcher.RecordLocalRename("src.txt", "dst.txt");

        await watcher.PollOnceAsync(CancellationToken.None);

        // Neither a delete (we removed src.txt locally too) nor a create (we know about dst).
        Assert.Empty(sink.Calls);
    }

    private static ObjectStoreChangeWatcher NewWatcher(
        IObjectStoreBackend backend, IProjFsCommandSink sink, ILostAndFoundQuarantine quarantine)
    {
        return new ObjectStoreChangeWatcher(
            backend,
            sink,
            quarantine,
            interval: TimeSpan.Zero, // disable background loop; tests drive PollOnceAsync directly
            logger: NullLogger<ObjectStoreChangeWatcher>.Instance);
    }

    // ---- Test doubles -----------------------------------------------------------------

    private sealed class FakeBackend : IObjectStoreBackend
    {
        private readonly ConcurrentDictionary<string, ObjectInfo> objects =
            new(StringComparer.Ordinal);

        public void Set(string key, string etag, long size, DateTimeOffset lastModified = default)
        {
            objects[key] = new ObjectInfo(
                Key: key,
                RelativePath: KeyPath.ToRelativePath(key),
                Size: size,
                LastModified: lastModified == default ? DateTimeOffset.UtcNow : lastModified,
                ETag: etag,
                IsDirectory: false);
        }

        public void Remove(string key) => objects.TryRemove(key, out _);

        public IAsyncEnumerable<ObjectInfo> ListAllAsync(CancellationToken ct) =>
            EnumerateAsync(ct);

        private async IAsyncEnumerable<ObjectInfo> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var kv in objects.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                yield return kv.Value;
                await Task.Yield();
            }
        }

        // Unused by the watcher.
        public IAsyncEnumerable<ObjectInfo> ListAsync(string r, CancellationToken c) =>
            throw new NotImplementedException();
        public IAsyncEnumerable<ObjectInfo> ListRecursiveAsync(string r, CancellationToken c) =>
            throw new NotImplementedException();
        public Task<BucketVersioningStatus> GetBucketVersioningStatusAsync(CancellationToken c) =>
            throw new NotImplementedException();
        public Task<ObjectInfo?> HeadAsync(string r, CancellationToken c) =>
            throw new NotImplementedException();
        public Task ReadRangeAsync(string r, long o, long l, Stream d, CancellationToken c) =>
            throw new NotImplementedException();
        public Task<UploadResult> UploadAsync(string r, Stream s, string? e, CancellationToken c) =>
            throw new NotImplementedException();
        public Task DeleteAsync(string r, CancellationToken c) =>
            throw new NotImplementedException();
        public Task DeletePrefixAsync(string r, CancellationToken c) =>
            throw new NotImplementedException();
        public Task RenameAsync(string a, string b, CancellationToken c) =>
            throw new NotImplementedException();
        public Task RenamePrefixAsync(string a, string b, CancellationToken c) =>
            throw new NotImplementedException();

        public void Dispose() { }
    }

    private sealed record SinkCall(string Op, string Path, long Size, bool AllowDirty);

    private sealed class RecordingSink : IProjFsCommandSink
    {
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
            Calls.Add(new SinkCall("WritePlaceholder", relativePath, size, false));
            return WritePlaceholderResult;
        }

        public ProjFsUpdateOutcome TryUpdateFile(
            string relativePath, long size, DateTimeOffset lastModified, byte[] contentId)
        {
            Calls.Add(new SinkCall("UpdateFile", relativePath, size, false));
            return UpdateFileResult;
        }

        public ProjFsUpdateOutcome TryDeleteFile(string relativePath, bool allowDirty)
        {
            deleteCount++;
            Calls.Add(new SinkCall("DeleteFile", relativePath, 0, allowDirty));
            return OverrideOnNthDelete.TryGetValue(deleteCount, out var ov) ? ov : DeleteFileResult;
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
}
