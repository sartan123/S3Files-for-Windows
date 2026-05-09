using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.ObjectStore;
using OSVFS.Sync;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xunit;

namespace OSVFS.Core.UnitTests;

public sealed class OnDemandPollingChangeSourceTests
{
    [Fact]
    public async Task Reconcile_emits_no_events_when_directory_was_just_registered_and_listing_is_empty()
    {
        var backend = new FakeBackend();
        var source = NewSource(backend);

        source.RegisterWatchedDirectory("docs");
        var events = await source.ReconcileAllAsync(CancellationToken.None);

        Assert.Empty(events);
    }

    [Fact]
    public async Task Reconcile_seeds_baseline_on_first_pass_then_detects_new_file_on_second_pass()
    {
        var backend = new FakeBackend();
        backend.Set("docs/a.txt", "etag-a", 1);
        var source = NewSource(backend);
        source.RegisterWatchedDirectory("docs");

        // First pass primes the per-directory snapshot. The current implementation emits
        // an upsert for every file it finds for the first time (the watcher is idempotent
        // and TryUpdateFile + WritePlaceholder are no-ops when the placeholder is already
        // current, mirroring the full-polling path).
        var first = await source.ReconcileAllAsync(CancellationToken.None);
        var seeded = Assert.Single(first);
        Assert.Equal("docs/a.txt", seeded.Key);

        backend.Set("docs/b.txt", "etag-b", 2);
        var second = await source.ReconcileAllAsync(CancellationToken.None);

        var newEvent = Assert.Single(second);
        Assert.Equal(ObjectChangeKind.Upserted, newEvent.Kind);
        Assert.Equal("docs/b.txt", newEvent.Key);
        Assert.Equal("docs\\b.txt", newEvent.RelativePath);
        Assert.Equal("etag-b", newEvent.ETag);
    }

    [Fact]
    public async Task Reconcile_detects_modified_file_and_emits_upsert_with_new_etag()
    {
        var backend = new FakeBackend();
        backend.Set("docs/a.txt", "etag-old", 1);
        var source = NewSource(backend);
        source.RegisterWatchedDirectory("docs");
        await source.ReconcileAllAsync(CancellationToken.None); // seed

        backend.Set("docs/a.txt", "etag-new", 5);
        var events = await source.ReconcileAllAsync(CancellationToken.None);

        var ev = Assert.Single(events);
        Assert.Equal(ObjectChangeKind.Upserted, ev.Kind);
        Assert.Equal("docs/a.txt", ev.Key);
        Assert.Equal("etag-new", ev.ETag);
        Assert.Equal(5, ev.Size);
    }

    [Fact]
    public async Task Reconcile_detects_deleted_file_and_emits_delete()
    {
        var backend = new FakeBackend();
        backend.Set("docs/gone.txt", "e", 1);
        var source = NewSource(backend);
        source.RegisterWatchedDirectory("docs");
        await source.ReconcileAllAsync(CancellationToken.None); // seed

        backend.Remove("docs/gone.txt");
        var events = await source.ReconcileAllAsync(CancellationToken.None);

        var ev = Assert.Single(events);
        Assert.Equal(ObjectChangeKind.Deleted, ev.Kind);
        Assert.Equal("docs/gone.txt", ev.Key);
    }

    [Fact]
    public async Task Reconcile_does_not_observe_changes_in_unwatched_directories()
    {
        var backend = new FakeBackend();
        var source = NewSource(backend);
        // Only register "docs"; "other/" stays unwatched.
        source.RegisterWatchedDirectory("docs");
        await source.ReconcileAllAsync(CancellationToken.None);

        backend.Set("other/never-noticed.txt", "e", 1);
        var events = await source.ReconcileAllAsync(CancellationToken.None);

        // Per the on-demand spec: unvisited directories are invisible to the
        // reconcile loop. They become visible only when ProjFS enumerates them,
        // which is the moment they enter the watch set.
        Assert.Empty(events);
    }

    [Fact]
    public void RegisterWatchedDirectory_auto_registers_ancestor_chain_up_to_root()
    {
        var source = NewSource(new FakeBackend());

        source.RegisterWatchedDirectory("a/b/c");

        // root, a, a/b, a/b/c → 4 entries
        Assert.Equal(4, source.WatchedDirectoryCount);
    }

    [Fact]
    public void RegisterWatchedDirectory_is_idempotent_and_root_is_always_present()
    {
        var source = NewSource(new FakeBackend());

        source.RegisterWatchedDirectory("a/b");
        source.RegisterWatchedDirectory("a/b");
        source.RegisterWatchedDirectory(string.Empty);

        // root, a, a/b → 3 entries even after duplicate registrations
        Assert.Equal(3, source.WatchedDirectoryCount);
    }

    [Fact]
    public async Task Reconcile_with_root_registered_picks_up_top_level_objects()
    {
        var backend = new FakeBackend();
        var source = NewSource(backend);
        source.RegisterWatchedDirectory(string.Empty);
        await source.ReconcileAllAsync(CancellationToken.None);

        backend.Set("top.txt", "etag-top", 9);
        var events = await source.ReconcileAllAsync(CancellationToken.None);

        var ev = Assert.Single(events);
        Assert.Equal("top.txt", ev.Key);
        Assert.Equal(9, ev.Size);
    }

    [Fact]
    public async Task Reconcile_uses_windows_relative_path_for_registration_input()
    {
        // Confirm the registrar accepts the backslash form ProjFS hands us, not just
        // the forward-slash object-key form.
        var backend = new FakeBackend();
        backend.Set("a/b/c.txt", "e", 1);
        var source = NewSource(backend);

        source.RegisterWatchedDirectory("a\\b");
        await source.ReconcileAllAsync(CancellationToken.None); // seed

        backend.Set("a/b/d.txt", "e2", 2);
        var events = await source.ReconcileAllAsync(CancellationToken.None);

        var ev = Assert.Single(events);
        Assert.Equal("a/b/d.txt", ev.Key);
    }

    [Fact]
    public async Task RecordLocalUpload_prevents_self_trigger_on_next_reconcile()
    {
        var backend = new FakeBackend();
        var source = NewSource(backend);
        source.RegisterWatchedDirectory("docs");
        await source.ReconcileAllAsync(CancellationToken.None);

        var when = DateTimeOffset.UtcNow;
        backend.Set("docs/local.txt", "etag-from-upload", 42, when);
        source.RecordLocalUpload("docs/local.txt", "etag-from-upload", 42, when);

        var events = await source.ReconcileAllAsync(CancellationToken.None);
        Assert.Empty(events);
    }

    [Fact]
    public async Task BeginLocalKeyChange_skips_key_in_concurrent_reconcile()
    {
        var backend = new FakeBackend();
        backend.Set("docs/inflight.txt", "etag-old", 1);
        var source = NewSource(backend);
        source.RegisterWatchedDirectory("docs");
        await source.ReconcileAllAsync(CancellationToken.None);

        backend.Set("docs/inflight.txt", "etag-new", 2);

        IReadOnlyList<ObjectChangeEvent> events;
        using (source.BeginLocalKeyChange("docs/inflight.txt"))
        {
            events = await source.ReconcileAllAsync(CancellationToken.None);
        }

        Assert.Empty(events);
    }

    [Fact]
    public async Task RecordLocalDelete_prevents_self_delete_event_on_next_reconcile()
    {
        var backend = new FakeBackend();
        backend.Set("docs/gone.txt", "e", 1);
        var source = NewSource(backend);
        source.RegisterWatchedDirectory("docs");
        await source.ReconcileAllAsync(CancellationToken.None);

        backend.Remove("docs/gone.txt");
        source.RecordLocalDelete("docs/gone.txt");

        var events = await source.ReconcileAllAsync(CancellationToken.None);
        Assert.Empty(events);
    }

    [Fact]
    public async Task RecordLocalDeletePrefix_clears_cached_entries_under_prefix()
    {
        var backend = new FakeBackend();
        backend.Set("dir/a.txt", "e1", 1);
        backend.Set("dir/sub/b.txt", "e2", 1);
        var source = NewSource(backend);
        source.RegisterWatchedDirectory("dir/sub");
        await source.ReconcileAllAsync(CancellationToken.None);

        backend.Remove("dir/a.txt");
        backend.Remove("dir/sub/b.txt");
        source.RecordLocalDeletePrefix("dir");

        var events = await source.ReconcileAllAsync(CancellationToken.None);
        Assert.Empty(events);
    }

    [Fact]
    public async Task WatchAsync_with_zero_interval_yields_nothing_and_completes()
    {
        var backend = new FakeBackend();
        backend.Set("seed.txt", "e", 1);
        var source = new OnDemandPollingChangeSource(
            backend, TimeSpan.Zero, NullLogger<OnDemandPollingChangeSource>.Instance);
        source.RegisterWatchedDirectory(string.Empty);

        var events = new List<ObjectChangeEvent>();
        await foreach (var ev in source.WatchAsync(CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Empty(events);
    }

    [Fact]
    public async Task Reconcile_only_lists_registered_directories_not_whole_bucket()
    {
        // Demonstrates the API-cost contract: a single tick issues exactly one
        // ListAsync per registered directory, regardless of bucket size or
        // unvisited subtrees.
        var backend = new FakeBackend();
        for (var i = 0; i < 50; i++)
        {
            backend.Set($"hot/dir/file-{i}.txt", $"e{i}", 1);
            backend.Set($"cold/dir/file-{i}.txt", $"e{i}", 1);
        }

        var source = NewSource(backend);
        source.RegisterWatchedDirectory("hot/dir");
        backend.ListCallCount = 0; // reset after registration; nothing has been listed yet

        await source.ReconcileAllAsync(CancellationToken.None);

        // Watch set after RegisterWatchedDirectory("hot/dir"): root, hot, hot/dir → 3 lists.
        Assert.Equal(3, backend.ListCallCount);
        Assert.DoesNotContain("cold/dir", backend.ListedDirectories);
    }

    private static OnDemandPollingChangeSource NewSource(IObjectStoreBackend backend) => new(
        backend, TimeSpan.Zero, NullLogger<OnDemandPollingChangeSource>.Instance);

    // ---- Test doubles -----------------------------------------------------------------

    /// <summary>
    /// Minimal in-memory backend that supports the delimited <see cref="ListAsync"/>
    /// the on-demand source actually consumes. Tracks call counts so tests can
    /// assert that the source only lists the watched subset of the bucket.
    /// </summary>
    private sealed class FakeBackend : IObjectStoreBackend
    {
        private readonly ConcurrentDictionary<string, ObjectInfo> objects =
            new(StringComparer.Ordinal);

        public int ListCallCount;
        public List<string> ListedDirectories { get; } = new();

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

        public IAsyncEnumerable<ObjectInfo> ListAsync(
            string relativeDirectory, CancellationToken ct)
        {
            Interlocked.Increment(ref ListCallCount);
            var dir = KeyPath.ToObjectKey(relativeDirectory).TrimEnd('/');
            lock (ListedDirectories) ListedDirectories.Add(dir);
            return EnumerateImmediateChildrenAsync(dir, ct);
        }

        private async IAsyncEnumerable<ObjectInfo> EnumerateImmediateChildrenAsync(
            string directory, [EnumeratorCancellation] CancellationToken ct)
        {
            var prefix = directory.Length == 0 ? string.Empty : directory + "/";
            var seenSubdirs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kv in objects.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var key = kv.Key;
                if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.Ordinal)) continue;

                var tail = prefix.Length == 0 ? key : key[prefix.Length..];
                var slash = tail.IndexOf('/');
                if (slash < 0)
                {
                    // Immediate file child.
                    yield return kv.Value;
                }
                else
                {
                    var subdir = tail[..slash];
                    if (seenSubdirs.Add(subdir))
                    {
                        var subdirKey = prefix + subdir;
                        yield return new ObjectInfo(
                            Key: subdirKey,
                            RelativePath: KeyPath.ToRelativePath(subdirKey),
                            Size: 0,
                            LastModified: default,
                            ETag: string.Empty,
                            IsDirectory: true);
                    }
                }
                await Task.Yield();
            }
        }

        public IAsyncEnumerable<ObjectInfo> ListAllAsync(CancellationToken ct) =>
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
