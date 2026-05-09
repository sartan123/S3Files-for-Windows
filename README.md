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

The current build ships an Amazon S3 backend. The object-store abstraction
is provider-neutral by design, and **additional providers (Google Cloud
Storage and Azure Blob Storage) are planned** behind the same `--provider`
flag — see [Supported backends](#supported-backends) below.

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
| Backends today | S3 (GCS / Azure Blob planned behind the same `--provider` flag) | 70+ backends |
| Runtime dependency | None (Native AOT) | None (single Go binary) |

If you need a backend OSVFS does not support, keep using rclone. If you
want object storage to feel like OneDrive on Windows without installing a
kernel driver, OSVFS is for you.

## Supported backends

OSVFS is built around a provider-neutral
[`IObjectStoreBackend`](src/OSVFS.Core/ObjectStore/IObjectStoreBackend.cs)
abstraction, and the backend is selected at startup with the `--provider`
flag. Multi-cloud support is an explicit goal of the project, not just an
abstraction left open for later.

| Provider | `--provider` value | Status |
| --- | --- | --- |
| Amazon S3 (and S3-compatible: MinIO, Cloudflare R2, Wasabi, Backblaze B2, Ceph, …) | `s3` | **Available** |
| Google Cloud Storage | `gcs` | Planned |
| Azure Blob Storage | `azureblob` | Planned |

## How to use

### Prerequisites

- Windows 10 1809 (build 17763) or later, or Windows 11
- The Windows optional feature **`Client-ProjFS`** must be enabled
- AWS credentials reachable via the standard AWS SDK chain (environment
  variables, shared profile, IAM role, etc.) — or saved into the OSVFS
  built-in encrypted store described in
  [Managing AWS credentials](#managing-aws-credentials)
- An S3 bucket you have read/write access to
- **Bucket versioning must be Enabled** on the target bucket. `osvfs`
  refuses to start otherwise — see [Why versioning matters](#why-versioning-matters)
  for the rationale and the `--allow-unversioned` escape hatch. The
  credentials must also allow `s3:GetBucketVersioning`.

Enable versioning once with the AWS CLI:

```powershell
aws s3api put-bucket-versioning `
  --bucket my-bucket `
  --versioning-configuration Status=Enabled
```

#### Why versioning matters

Local file edits and deletes inside the virtualization root propagate to S3
as overwrite `PutObject` and `DeleteObject` calls. Without bucket versioning
those operations are **destructive and irreversible**: a deleted object is
gone, an overwrite leaves no prior copy. Versioning turns each of those
calls into a new version + delete-marker pair, so a misclick in Explorer or
a runaway script remains recoverable.

If `osvfs` detects that the configured bucket has versioning disabled (or
suspended) it refuses to start with a copy-pasteable `aws s3api
put-bucket-versioning` command, the bucket name, and a link back to this
section.

For CI runs or disposable buckets where the bucket is recreated per-job
and the recoverability story does not apply, pass `--allow-unversioned` to
bypass the safety check.

Enable ProjFS once, in an elevated PowerShell session:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All
```

### Run

```powershell
osvfs `
  --provider s3 `
  --bucket my-bucket `
  --root-folder C:\Users\you\OSVFS
```

Open `C:\Users\you\OSVFS` in Explorer and the bucket contents appear.

### Command-line options

| Option | Description | Default |
| --- | --- | --- |
| `--provider` | Object-store provider backing the virtualization root. Currently `s3` is fully implemented; `gcs` / `azureblob` fail at startup. | `s3` |
| `--bucket` | Bucket (S3/GCS) or container (Azure) to expose through the filesystem (required) | — |
| `--root-folder` | Path to the virtualization root (required) | — |
| `--endpoint-url` | Override the default S3 endpoint URL (e.g. for LocalStack / MinIO) | AWS default |
| `--region` | AWS region (e.g. `us-east-1`, `ap-northeast-1`). When omitted, the SDK falls back to the standard region resolution chain (env vars, profile, IMDS). | — |
| `--aws-profile` | Use credentials previously saved by `osvfs credentials set --profile <name>` (encrypted with DPAPI in Windows Credential Manager). When omitted, the AWS SDK's default chain is used. | — |
| `--prefix` | Optional key prefix within the bucket. When set, only objects under this prefix are projected into the virtualization root. | — |
| `--sync-interval-seconds` | Polling interval for detecting external object-store changes; `0` disables | `30` |
| `--bandwidth-up` | Upload bandwidth ceiling. Plain bytes/s by default; suffixes `K`/`M`/`G` mean KiB/s, MiB/s, GiB/s (e.g. `5M` = 5 MiB/s). Omit or set to `0` to disable. | — (unlimited) |
| `--bandwidth-down` | Download bandwidth ceiling. Same format as `--bandwidth-up`. | — (unlimited) |
| `--multipart-threshold` | Stream size at or above which uploads are routed through the multipart path. Same K/M/G suffixes as `--bandwidth-up`. | `8M` |
| `--multipart-part-size` | Per-part size used by multipart uploads. Must be between `5M` and `5G`. | `5M` |
| `--log-format` | Console log output format: `text` (single-line, human-readable) or `json` (one UTF-8 JSON object per line, UTC timestamps, for log shippers like Datadog / Loki). | `text` |
| `--allow-unversioned` | **DANGER:** Skip the bucket-versioning safety check and run against a bucket without versioning. Local edits and deletes become unrecoverable. Intended for CI / disposable buckets only — see [Why versioning matters](#why-versioning-matters). | off |
| `--verbose` | Enable debug-level logging | off |

To project only a sub-tree of a bucket — for example `s3://my-bucket/team-a/` —
pass `--prefix team-a/`. The virtualization root then mirrors that prefix as
its own logical root: listings, hydration, writes, deletes, and renames all
stay scoped to objects under the prefix, and the rest of the bucket is
invisible.

### Bandwidth limits

`osvfs` runs as a long-lived background process, so a single large hydration
or upload can saturate the link and starve other applications. Pass
`--bandwidth-up` / `--bandwidth-down` (or set them in
[`osvfs.toml`](#configuration-file)) to cap each direction independently:

```powershell
osvfs `
  --bucket my-bucket `
  --root-folder C:\Users\you\OSVFS `
  --bandwidth-up 5M `       # cap uploads at 5 MiB/s
  --bandwidth-down 10M      # cap downloads at 10 MiB/s
```

Values follow the rclone `--bwlimit` convention: a plain number is bytes per
second, and the `K` / `M` / `G` suffixes mean KiB/s, MiB/s, and GiB/s
respectively (`5M` = 5 MiB/s). Omitting the flag — or setting it to `0` —
leaves that direction unlimited. The limit is enforced through a token
bucket on the upload payload stream and the download response stream, so
`TransferUtility`'s multipart workers and the on-demand hydration path are
both paced by the same ceiling.

### Tuning multipart uploads

`osvfs` routes any upload at or above `--multipart-threshold` through the
S3 multipart path, splitting the payload into `--multipart-part-size`
chunks that `TransferUtility` uploads in parallel. The defaults (8 MiB
threshold, 5 MiB parts) target a typical office connection, but two
common scenarios benefit from explicit tuning:

| Scenario | Suggested settings | Why |
| --- | --- | --- |
| Fat links / large files | `--multipart-threshold 64M --multipart-part-size 64M` | Larger parts amortize per-request overhead and cut the part count on multi-GiB files. |
| Many tiny edits | `--multipart-threshold 16M` (keep 5M parts) | Skips multipart for small files where a single PUT is faster than negotiating an upload session. |
| Constrained networks | Keep defaults | Smaller parts mean a network blip retries less data. |

S3 enforces three hard limits on the part size — you must stay inside
all of them or `osvfs` refuses to start, and the service rejects the
upload at completion time:

- `--multipart-part-size` must be **≥ 5 MiB** (`5M`). Smaller parts are
  rejected by S3 except for the last part of an upload.
- `--multipart-part-size` must be **≤ 5 GiB** (`5G`). Larger parts
  exceed the per-part ceiling.
- A single multipart upload is capped at **10 000 parts**, so the
  largest object you can upload is `part-size × 10 000` (16 MiB parts
  → 160 GiB max; 64 MiB parts → 640 GiB max). Pick a part size large
  enough to fit your largest expected file.

### Sync interval for large buckets

`osvfs` detects external object-store changes by re-listing the bucket every
`--sync-interval-seconds` (default `30`). Each poll walks every page of
ListObjectsV2 (`NextContinuationToken` is threaded automatically), so the
entire bucket — or the configured `--prefix` sub-tree — is scanned on every
tick.

Because S3 caps a single ListObjectsV2 page at 1000 keys, the wall time of a
poll grows roughly linearly with the number of objects under the watched
prefix. As a rough planning guide, a single ListObjectsV2 page typically
returns in tens to low-hundreds of milliseconds against AWS S3 from a nearby
region, so a 100k-object bucket needs ~100 round-trips and a few seconds of
listing per tick. If the listing time approaches `--sync-interval-seconds`,
raise the interval (or scope the projection with `--prefix`) so polls do not
overlap and starve other I/O. The integration test
`S3BackendListPaginationTests` logs the observed `ms / 1000 keys` against
LocalStack and is the easiest place to dial in a per-environment number.

### Structured logging

By default `osvfs` writes single-line, human-readable log entries to the
console. Pass `--log-format json` (or set `log-format = "json"` in
[`osvfs.toml`](#configuration-file)) to switch to a structured stream that
log shippers such as Datadog or Loki can parse without regex:

```powershell
osvfs --bucket my-bucket --root-folder C:\Users\you\OSVFS --log-format json
```

Each log entry is written as a single line (terminated with the platform
line separator) carrying one JSON object. Field names follow the keys
produced by `Microsoft.Extensions.Logging.Console`'s JSON formatter:

| Field | Description |
| --- | --- |
| `Timestamp` | UTC timestamp in `yyyy-MM-ddTHH:mm:ss.fffZ` format. |
| `EventId` | The `EventId.Id` of the log entry (`0` when not set). |
| `LogLevel` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, or `Critical`. |
| `Category` | Logger category — typically the source type's full name (`OSVFS`, `OSVFS.ProjFs.ProjFsProvider`, ...). |
| `Message` | Final formatted message after structured-template substitution. |
| `State` | Object containing the original message template and each named placeholder (e.g. `{Bucket}`) as a separate property — preserved as structured data for downstream filtering. |
| `Exception` | Present only when an exception was attached; carries the formatted exception text. |

Sample line (pretty-printed here for readability — on the wire it is one
line):

```json
{
  "Timestamp": "2026-05-09T11:22:33.456Z",
  "EventId": 0,
  "LogLevel": "Information",
  "Category": "OSVFS",
  "Message": "Virtualizing s3://my-bucket at C:\\Users\\you\\OSVFS",
  "State": {
    "Message": "Virtualizing s3://my-bucket at C:\\Users\\you\\OSVFS",
    "Bucket": "my-bucket",
    "Root": "C:\\Users\\you\\OSVFS",
    "{OriginalFormat}": "Virtualizing s3://{Bucket} at {Root}"
  }
}
```

### Configuration file

Every option that can be passed to the root `osvfs` command can also be set
in a TOML configuration file. Two locations are searched, in this order:

1. `./osvfs.toml` — relative to the current working directory (project-local)
2. `%APPDATA%\OSVFS\config.toml` — per-user, machine-global

Values from the project file override the user file per key. CLI flags
override both, so you can keep stable defaults in the file and tweak
individual options at the prompt.

`credentials` sub-commands are not affected by the config file; they always
take their inputs from CLI arguments and interactive prompts.

```toml
# ./osvfs.toml or %APPDATA%\OSVFS\config.toml
provider             = "s3"
bucket               = "my-bucket"
root-folder          = "C:/Users/you/OSVFS"
endpoint-url         = "http://localhost:4566"   # optional
region               = "ap-northeast-1"          # optional
prefix               = "team-a/"                 # optional
aws-profile          = "prod"                    # optional
bandwidth-up         = "5M"                      # optional, "0" / omit = unlimited
bandwidth-down       = "10M"                     # optional, "0" / omit = unlimited
multipart-threshold  = "8M"                      # optional
multipart-part-size  = "16M"                     # optional, 5M..5G
log-format           = "text"                    # optional, "text" or "json"
allow-unversioned    = false                     # DANGER: skip the bucket-versioning safety check
verbose              = false
sync-interval-seconds = 30
```

A ready-to-edit sample is shipped as
[`osvfs.toml.example`](./osvfs.toml.example) at the repo root and is also
copied next to `osvfs.exe` on `dotnet publish`, so you can rename it to
`osvfs.toml` (or `%APPDATA%\OSVFS\config.toml`) and uncomment the keys you
need.

Both kebab-case (`root-folder`) and snake_case (`root_folder`) keys are
accepted; kebab matches the CLI flag names and is preferred. With a config
file in place, a typical mount becomes:

```powershell
osvfs                       # all options sourced from osvfs.toml
osvfs --bucket other-bucket # config file used, --bucket overrides it
```

### Managing AWS credentials

OSVFS can resolve AWS credentials through the standard AWS SDK chain
(environment variables, the shared `~/.aws/credentials` profile, IAM role,
IMDS), **or** through its own per-user encrypted store backed by Windows
Credential Manager. The secret access key — and any STS session token — is
encrypted with **DPAPI** at `CurrentUser` scope before it is written into the
credential blob, so the entry can only be decrypted by the user that saved
it on the same machine.

```powershell
# Save a profile (the secret prompt is masked)
osvfs credentials set --profile prod

# Or pass everything on the command line (skip the prompts)
osvfs credentials set --profile prod `
  --access-key AKIA... `
  --secret-key ... `
  --session-token ...   # optional, for temporary credentials

# Inspect a profile (the secret is never echoed)
osvfs credentials get --profile prod

# List every profile owned by OSVFS
osvfs credentials list

# Delete a profile
osvfs credentials remove --profile prod
```

Then run `osvfs` with `--aws-profile <name>` to use it for a mount:

```powershell
osvfs `
  --provider s3 `
  --bucket my-bucket `
  --root-folder C:\Users\you\OSVFS `
  --aws-profile prod
```

Each entry is stored as a Windows generic credential under the target name
`OSVFS:AWS:<profile>`. The credential persists at `LocalMachine` scope
(it survives logout) but the DPAPI envelope is bound to the saving user, so
copying the entry to another user — or to another machine — will fail to
decrypt. Treat the OSVFS store as a per-user convenience cache, not as a
backup of your AWS credentials.

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
 │ S3ChangeWatcher     │ ←─────────────────────────────┘
 │  + LostAndFound     │
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
  configured `--multipart-threshold` (default 8 MiB) are routed through
  `TransferUtility` so large files are split into `--multipart-part-size`
  chunks (default 5 MiB) and uploaded in parallel. It lives in a cross-platform Core library so
  integration tests can run against LocalStack on Linux without pulling in
  the Windows-only ProjFS bindings. When `--prefix` is set, the backend
  transparently rewrites virtualization-root-relative paths into the
  full bucket key (`<prefix>/<path>`) on every API call.
- [`ObjectStoreChangeWatcher`](src/OSVFS.Core/Sync/ObjectStoreChangeWatcher.cs)
  periodically re-lists the bucket, diffs against an in-memory snapshot, and
  pushes external changes back into ProjFS. The object store is treated as
  the source of truth: if a remote change collides with an unsynced local
  edit, the local copy is moved to a `.osvfs-lost+found` quarantine
  directory.

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
dotnet run --project src\OSVFS -- --bucket my-bucket --root-folder C:\Users\you\OSVFS
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

# Integration tests against LocalStack (requires Docker)
dotnet test tests\OSVFS.Core.IntegrationTests
```

The integration test project targets `net10.0` and only references the
cross-platform `OSVFS.Core` library, so it can run on Linux CI runners
against [LocalStack](https://github.com/localstack/localstack) via
[Testcontainers](https://dotnet.testcontainers.org/).

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

Released under the [MIT License](./LICENSE).
