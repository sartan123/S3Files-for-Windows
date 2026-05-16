# OSVFS — Object Storage Virtual File System for Windows

[日本語 README](./README.ja.md)

[![CI](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)

OSVFS mounts a cloud object-store bucket as an ordinary local folder on
Windows, with on-demand hydration and two-way synchronization, built on
[Windows Projected File System (ProjFS)][projfs]. It is a **driver-free
alternative to `rclone mount`** on Windows: ProjFS ships as an optional
feature in Windows 10 1809+ and Windows 11, so OSVFS does not need WinFsp
(or any other third-party kernel driver) to be installed.

OSVFS ships with **Amazon S3 and Azure Blob Storage** backends today;
**Google Cloud Storage** is planned behind the same `provider` flag —
see [Supported backends](#supported-backends) below.

[projfs]: https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system

## Overview

OSVFS exposes a cloud object-store bucket through Windows Explorer the same
way OneDrive Files On-Demand exposes cloud files: directory entries are
visible without a full download, file contents are hydrated on first open,
local writes / deletes / renames are propagated back to the bucket, and
external changes are picked up by a background poller.

ProjFS — the Windows kernel-mode component that also powers OneDrive Files
On-Demand and VFS for Git — is the kernel side here. `osvfs` itself runs as
a normal user-mode process: there is no custom driver to install or sign.

## Compared to `rclone mount`

`rclone` is the de-facto way to mount object storage on Windows, and its
broad backend coverage remains unmatched. OSVFS is a narrower tool: it
focuses on the Windows experience and trades backend breadth for a
zero-third-party-driver install path.

| | OSVFS | `rclone mount` |
| --- | --- | --- |
| Kernel component | Windows-built-in **ProjFS** (enable an optional feature; no driver install) | **WinFsp** — separate kernel driver, MSI install required |
| Install footprint | Single signed `osvfs.exe` (Native AOT) | `rclone.exe` + WinFsp MSI |
| AppLocker / WDAC fit | No third-party kernel driver to allow-list | Requires WinFsp kernel driver to be allowed by policy |
| Explorer integration | Native ProjFS placeholders — the same "online-only" model OneDrive uses | FUSE-style mount; files appear as fully-present |
| Backends today | S3 + Azure Blob (GCS planned behind the same `provider` flag) | 70+ backends |
| Runtime dependency | None (Native AOT) | None (single Go binary) |

If you need a backend OSVFS does not support, keep using rclone. If you
want object storage to feel like OneDrive on Windows without installing a
kernel driver, OSVFS is for you.

## Supported backends

OSVFS is built around a provider-neutral
[`IObjectStoreBackend`](src/OSVFS.Core/ObjectStore/IObjectStoreBackend.cs)
abstraction, and the backend is selected at startup with the `provider`
flag. Multi-cloud support is an explicit goal of the project, not just an
abstraction left open for later.

| Provider | `provider` value | Status |
| --- | --- | --- |
| Amazon S3 (and S3-compatible: MinIO, Cloudflare R2, Wasabi, Backblaze B2, Ceph, …) | `s3` | **Available** |
| Azure Blob Storage | `azureblob` | **Available** |
| Google Cloud Storage | `gcs` | Planned |

## Documentation

The detailed reference is split into focused pages under
[`docs/`](docs/):

- **[Configuration](docs/configuration.md)** — prerequisites, the small
  CLI surface, the full `osvfs.toml` key reference, and the multi-mount
  layout.
- **[Performance tuning](docs/tuning.md)** — bandwidth ceilings,
  multipart upload thresholds, request concurrency, the HTTP transport,
  and retry policy.
- **[Synchronization and change events](docs/sync-and-events.md)** —
  polling vs. push-mode change detection, on-demand sync, read-only
  mounts, and SQS / Event Grid setup.
- **[Observability](docs/observability.md)** — structured logging,
  OpenTelemetry traces / metrics, the local Prometheus listener, and
  user-metadata round-trip.
- **[Authentication and credentials](docs/credentials.md)** — AWS
  shared profile / SSO / `aws login` and the four Azure auth branches
  (connection string, SAS, Managed Identity, DefaultAzureCredential).
- **[Troubleshooting](docs/troubleshooting.md)** — the `osvfs doctor`
  self-check and the `osvfs lost-and-found` recovery CLI.

## Quick start

The shortest possible mount config is two keys:

```toml
# ./osvfs.toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
```

With AWS credentials available through the SDK chain and bucket
versioning turned on (see
[Configuration → Prerequisites](docs/configuration.md#prerequisites) for
the full list), start the mount:

```powershell
osvfs                            # start the configured mount
osvfs mount-all                  # start every [[mount]] in the config (multi-mount form)
osvfs mount --name personal      # start one mount by name
```

Open the configured root folder in Explorer and the bucket contents
appear. For Azure Blob (`provider = "azureblob"`) see
[Authentication and credentials → Azure Blob configuration](docs/credentials.md#azure-blob-configuration).

## Architecture

`osvfs` is a user-mode ProjFS provider. `PrjFlt.sys` (the Windows ProjFS
filter driver, shipped by Microsoft as part of the OS) is the kernel side,
and `osvfs` is the provider that hydrates entries from the configured
object store and propagates local changes back.

```
 ┌─────────────────────┐  StartDirectoryEnumeration / GetPlaceholderInfo
 │  Windows Shell      │  GetFileData
 │  (PrjFlt.sys)       │ ───────────────────────────────────┐
 └─────────┬───────────┘                                    │
           │ placeholders                                   ▼
           │ + hydrated bytes                    ┌─────────────────────┐
 ┌─────────▼───────────┐  WriteFileData /        │  ProjFsProvider     │
 │  C:\…\OSVFS         │  WritePlaceholderInfo   │  (IRequiredCallbacks)│
 │  (virtualization    │ ←──────────────────────│                     │
 │   root)             │                         └────┬──────┬─────────┘
 └─────────┬───────────┘                              │      │ AWS SDK
           │ local writes                             ▼      ▼
           │ (notification callbacks)           ┌──────────────┐
 ┌─────────▼───────────┐  PUT / DELETE / COPY   │  S3 bucket   │
 │ NotificationCallbacks│ ─────────────────────→│              │
 └─────────────────────┘                        └──────┬───────┘
                                                       │
 ┌─────────────────────┐  ListObjectsV2 (poll)         │
 │ ObjectStoreChange   │ ←─────────────────────────────┘
 │ Watcher             │       SQS ReceiveMessage
 │  + LostAndFound     │ ←─────────  EventBridge ←──── (optional)
 └─────────────────────┘
```

Roughly:

- [`ProjFsProvider`](src/OSVFS/ProjFs/ProjFsProvider.cs) implements
  `IRequiredCallbacks` from the managed ProjFS wrapper. Directory enumeration,
  placeholder metadata, and on-demand hydration all flow through here.
- [`NotificationCallbacks`](src/OSVFS/ProjFs/NotificationCallbacks.cs)
  receives ProjFS notifications for local writes / deletes / renames and
  forwards them to the object-store backend.
- [`S3Backend`](src/OSVFS.Core/ObjectStore/S3/S3Backend.cs) wraps AWSSDK.S3
  behind the provider-neutral [`IObjectStoreBackend`](src/OSVFS.Core/ObjectStore/IObjectStoreBackend.cs)
  with the small, ProjFS-shaped surface the provider needs (list, head,
  range read, upload, delete, rename-by-copy). Uploads at or above the
  configured `multipart-threshold` (default 16 MiB) are routed through
  `TransferUtility` so large files are split into `multipart-part-size`
  chunks (default 5 MiB) and uploaded in parallel. It lives in a cross-platform Core library so
  integration tests can run against LocalStack on Linux without pulling in
  the Windows-only ProjFS bindings. When `prefix` is set, the backend
  transparently rewrites virtualization-root-relative paths into the
  full bucket key (`<prefix>/<path>`) on every API call.
- [`ObjectStoreChangeWatcher`](src/OSVFS.Core/Sync/ObjectStoreChangeWatcher.cs)
  applies external bucket changes back into ProjFS. Changes are discovered
  through pluggable [`IChangeSource`](src/OSVFS.Core/Sync/IChangeSource.cs)
  implementations:
  [`OnDemandPollingChangeSource`](src/OSVFS.Core/Sync/OnDemandPollingChangeSource.cs)
  (the default) re-lists only the directories the user has visited via
  ProjFS using `Delimiter='/'`,
  [`PollingChangeSource`](src/OSVFS.Core/Sync/PollingChangeSource.cs)
  re-lists the entire bucket on a fixed cadence and diffs against an
  in-memory snapshot (selected by `sync-mode = "full"`), and
  [`SqsChangeSource`](src/OSVFS.Core/Sync/Sqs/SqsChangeSource.cs)
  long-polls an SQS queue carrying EventBridge S3 notifications. The object
  store is treated as the source of truth: if a remote change collides with
  an unsynced local edit, the local copy is moved to a `.osvfs-lost+found`
  quarantine directory.

## Building

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the exact
  version is pinned in [`global.json`](./global.json))
- Visual Studio 2022 or Build Tools with the **"Desktop development with
  C++"** workload — required for `link.exe` and the Windows SDK libraries
  used by Native AOT publishing
- Windows x64 (the host project pins `RuntimeIdentifier=win-x64` because
  ProjFS is Windows-only)

### Debug build

```powershell
dotnet build OSVFS.slnx -c Debug
dotnet run --project src\OSVFS    # mount config supplied via osvfs.toml
```

### Release build (Native AOT, single binary)

```powershell
dotnet publish src\OSVFS -c Release -r win-x64 -o publish\win-x64
```

The output is a self-contained `osvfs.exe`. End users do **not** need the
.NET runtime installed.

### Tests

```powershell
# Unit tests (Windows or Linux)
dotnet test tests\OSVFS.Core.UnitTests

# Integration tests against LocalStack + Azurite (requires Docker)
dotnet test tests\OSVFS.Core.IntegrationTests
```

The integration test project targets `net10.0` and only references the
cross-platform `OSVFS.Core` library, so it can run on Linux CI runners
against [LocalStack](https://github.com/localstack/localstack) and
[Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite)
via [Testcontainers](https://dotnet.testcontainers.org/).

> **Azurite ServiceVersion pin.** The Azure SDK ships its newest default
> `x-ms-version` with each release, but Azurite trails the live service by
> several months. To let Dependabot keep `Azure.Storage.Blobs` /
> `Azure.Storage.Queues` current without breaking the IT, the integration
> tests pin `BlobClientOptions.ServiceVersion` /
> `QueueClientOptions.ServiceVersion` to whatever the Azurite "latest" image
> still understands. Production code path leaves the options as null so the
> SDK's newest default is used. When Azurite ships support for a higher API
> version, bump the constants in
> [`tests/OSVFS.Core.IntegrationTests/AzuriteFixture.cs`](tests/OSVFS.Core.IntegrationTests/AzuriteFixture.cs).

## Why C# / .NET?

ProjFS is a Windows kernel feature; any client must talk to it through native
APIs. Rust, Go, and C++ are all reasonable choices, but C# wins here for two
specific reasons:

1. **Microsoft ships an official managed wrapper for ProjFS.** The
   [`Microsoft.Windows.ProjFS`][projfs-nuget] NuGet package is the same
   binding used by Microsoft's own [SimpleProvider sample][simple-provider]
   and by VFS for Git. We can implement `IRequiredCallbacks` in C# and let
   the wrapper handle the COM/P-Invoke boundary, instead of hand-rolling
   the bindings ourselves.
2. **Native AOT removes the runtime tax.** A long-running user-mode
   filesystem provider has tight latency requirements: every directory
   listing and every byte of `GetFileData` is on the user's hot path.
   Publishing with `PublishAot=true` produces a single, statically compiled
   `osvfs.exe` with no JIT, no ReadyToRun, and no managed runtime install
   on the end user's machine — the startup and per-call cost is comparable
   to a native binary while we keep C#'s ergonomics for the cloud SDKs and
   the ProjFS callbacks.

The cross-platform pieces (`OSVFS.Core`) target plain `net10.0` and stay
AOT-compatible (`IsAotCompatible=true`), which is what lets LocalStack-based
integration tests run on Linux CI.

[projfs-nuget]: https://www.nuget.org/packages/Microsoft.Windows.ProjFS
[simple-provider]: https://github.com/microsoft/ProjFS-Managed-API

## References

- [Windows Projected File System (ProjFS) overview][projfs]
- [Microsoft `ProjFS-Managed-API` SimpleProvider sample][simple-provider]
- [.NET Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [AWS SDK for .NET — S3](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/s3-apis-intro.html)
- [`rclone`](https://rclone.org/) — comparable cross-platform mount utility;
  OSVFS is positioned as the no-extra-driver Windows-only alternative
- [WinFsp](https://winfsp.dev/) — the kernel driver `rclone mount` depends
  on, which OSVFS replaces with the Windows-built-in ProjFS feature

## License
