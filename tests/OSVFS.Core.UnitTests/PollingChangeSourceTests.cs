using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.ObjectStore;
using OSVFS.Sync;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xunit;

namespace OSVFS.Core.UnitTests;

public sealed class PollingChangeSourceTests
{
    [Fact]
    public async Task Poll_with_no_changes_emits_no_events()
    {
        var backend = new FakeBackend();
        backend.Set("a.txt", "etag-a", 1);
        backend.Set("b.txt", "etag-b", 2);

        var source = NewSource(backend);
        await source.PrimeSnapshotAsync(CancellationToken.None);
        var events = await source.PollOnceAsync(CancellationToken.None);

        Assert.Empty(events);
    }

    [Fact]
    public async Task Poll_detects_new_object_and_emits_upsert()
    {
        var backend = new FakeBackend();
        var source = NewSource(backend);
        await source.PrimeSnapshotAsync(CancellationToken.None);

        backend.Set("docs/new.txt", "etag-1", 5);
        var events = await source.PollOnceAsync(CancellationToken.None);

        var ev = Assert.Single(events);
        Assert.Equal(ObjectChangeKind.Upserted, ev.Kind);
        Assert.Equal("docs/new.txt", ev.Key);
        Assert.Equal("docs\\new.txt", ev.RelativePath);
        Assert.Equal(5, ev.Size);
        Assert.Equal("etag-1", ev.ETag);
    }

    [Fact]
    public async Task Poll_detects_modified_object_and_emits_upsert_with_new_etag()
    {
        var backend = new FakeBackend();
        backend.Set("file.txt", "etag-old", 1);
        var source = NewSource(backend);

        await source.PrimeSnapshotAsync(CancellationToken.None);
        backend.Set("file.txt", "etag-new", 2);
        var events = await source.PollOnceAsync(CancellationToken.None);

        var ev = Assert.Single(events);
        Assert.Equal(ObjectChangeKind.Upserted, ev.Kind);
        Assert.Equal("file.txt", ev.Key);
        Assert.Equal(2, ev.Size);
        Assert.Equal("etag-new", ev.ETag);
    }

    [Fact]
    public async Task Poll_detects_deleted_object_and_emits_delete()
    {
        var backend = new FakeBackend();
        backend.Set("gone.txt", "etag-1", 1);
        var source = NewSource(backend);

        await source.PrimeSnapshotAsync(CancellationToken.None);
        backend.Remove("gone.txt");
        var events = await source.PollOnceAsync(CancellationToken.None);

        var ev = Assert.Single(events);
        Assert.Equal(ObjectChangeKind.Deleted, ev.Kind);
        Assert.Equal("gone.txt", ev.Key);
        Assert.Equal("gone.txt", ev.RelativePath);
    }

    [Fact]
    public async Task RecordLocalUpload_prevents_self_trigger_on_next_poll()
    {
        var backend = new FakeBackend();
        var source = NewSource(backend);

        await source.PrimeSnapshotAsync(CancellationToken.None);

        var lastModified = DateTimeOffset.UtcNow;
        backend.Set("local.txt", "etag-from-upload", 42, lastModified);
        source.RecordLocalUpload("local.txt", "etag-from-upload", 42, lastModified);

        var events = await source.PollOnceAsync(CancellationToken.None);
        Assert.Empty(events);
    }

    [Fact]
    public async Task BeginLocalKeyChange_skips_key_in_concurrent_poll()
    {
        var backend = new FakeBackend();
        backend.Set("inflight.txt", "etag-old", 1);
        var source = NewSource(backend);

        await source.PrimeSnapshotAsync(CancellationToken.None);

        backend.Set("inflight.txt", "etag-new", 2);
        IReadOnlyList<ObjectChangeEvent> events;
        using (source.BeginLocalKeyChange("inflight.txt"))
        {
            events = await source.PollOnceAsync(CancellationToken.None);
        }

        Assert.Empty(events);
    }

    [Fact]
    public async Task RecordLocalDeletePrefix_clears_snapshot_under_prefix()
    {
        var backend = new FakeBackend();
        backend.Set("dir/a.txt", "e1", 1);
        backend.Set("dir/sub/b.txt", "e2", 1);
        backend.Set("other.txt", "e3", 1);

        var source = NewSource(backend);
        await source.PrimeSnapshotAsync(CancellationToken.None);

        backend.Remove("dir/a.txt");
        backend.Remove("dir/sub/b.txt");
        source.RecordLocalDeletePrefix("dir");

        var events = await source.PollOnceAsync(CancellationToken.None);
        Assert.Empty(events);
    }

    [Fact]
    public async Task RecordLocalRename_transposes_snapshot()
    {
        var backend = new FakeBackend();
        backend.Set("src.txt", "etag-1", 7);
        var source = NewSource(backend);
        await source.PrimeSnapshotAsync(CancellationToken.None);

        backend.Remove("src.txt");
        backend.Set("dst.txt", "etag-1", 7);
        source.RecordLocalRename("src.txt", "dst.txt");

        var events = await source.PollOnceAsync(CancellationToken.None);
        Assert.Empty(events);
    }

    [Fact]
    public async Task WatchAsync_with_zero_interval_primes_snapshot_and_completes()
    {
        var backend = new FakeBackend();
        backend.Set("seed.txt", "e", 1);
        var source = new PollingChangeSource(
            backend, TimeSpan.Zero, NullLogger<PollingChangeSource>.Instance);

        var events = new List<ObjectChangeEvent>();
        await foreach (var ev in source.WatchAsync(CancellationToken.None))
        {
            events.Add(ev);
        }

        // Priming the snapshot should never emit events; the zero-interval source
        // shuts down cleanly afterwards.
        Assert.Empty(events);
    }

    private static PollingChangeSource NewSource(IObjectStoreBackend backend) => new(
        backend, TimeSpan.Zero, NullLogger<PollingChangeSource>.Instance);

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
}
