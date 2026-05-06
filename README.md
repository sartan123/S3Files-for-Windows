# S3Files-for-Windows

[日本語 README](./README.ja.md)

[![CI](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)

A Windows port of the [AWS S3 Files][s3files] experience: mount an Amazon S3
bucket as an ordinary local folder, with on-demand hydration and two-way
synchronization. Built on top of [Windows Projected File System
(ProjFS)][projfs].

[s3files]: https://docs.aws.amazon.com/AmazonS3/latest/userguide/s3-files.html
[projfs]: https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system

## Overview

AWS [S3 Files][s3files] lets you connect AWS compute resources (EC2, Lambda,
EKS, ECS) to an S3 bucket as a real file system: directory entries are visible
without a full download, file contents are loaded on demand, local writes are
synchronized back to the bucket, and changes made directly to the bucket are
reflected in the file system view. AWS implements this on top of EFS / NFS,
so it is only available inside AWS-managed compute.

`s3files` brings the same end-user experience to a Windows desktop. Objects
in the bucket appear as placeholders in Windows Explorer; their contents are
downloaded the first time you open them. Local writes, deletes, and renames
are propagated back to S3, and external changes to the bucket are picked up
by a background poller. ProjFS is the kernel-mode component, and `s3files`
itself runs as a normal user-mode process — no custom driver required.

## How to use

### Prerequisites

- Windows 10 1809 (build 17763) or later, or Windows 11
- The Windows optional feature **`Client-ProjFS`** must be enabled
- AWS credentials reachable via the standard AWS SDK chain (environment
  variables, shared profile, IAM role, etc.)
- An S3 bucket you have read/write access to
- **Bucket versioning must be Enabled** on the target bucket. `s3files`
  refuses to start otherwise: local file edits and deletes propagate to S3
  as overwrites and `DeleteObject` calls, and versioning is what makes those
  recoverable. The credentials must also allow `s3:GetBucketVersioning`.

Enable versioning once with the AWS CLI:

```powershell
aws s3api put-bucket-versioning `
  --bucket my-bucket `
  --versioning-configuration Status=Enabled
```

Enable ProjFS once, in an elevated PowerShell session:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All
```

### Run

```powershell
s3files `
  --bucket my-bucket `
  --root-folder C:\Users\you\S3Files
```

Open `C:\Users\you\S3Files` in Explorer and the bucket contents appear.

### Command-line options

| Option | Description | Default |
| --- | --- | --- |
| `--bucket` | S3 bucket to expose through the filesystem (required) | — |
| `--root-folder` | Path to the virtualization root (required) | — |
| `--endpoint-url` | Override the default S3 endpoint URL (e.g. for LocalStack / MinIO) | AWS default |
| `--region` | AWS region (e.g. `us-east-1`, `ap-northeast-1`). When omitted, the SDK falls back to the standard region resolution chain (env vars, profile, IMDS). | — |
| `--prefix` | Optional key prefix within the bucket. When set, only objects under this prefix are projected into the virtualization root. | — |
| `--sync-interval-seconds` | Polling interval for detecting external S3 changes; `0` disables | `30` |
| `--verbose` | Enable debug-level logging | off |

To project only a sub-tree of a bucket — for example `s3://my-bucket/team-a/` —
pass `--prefix team-a/`. The virtualization root then mirrors that prefix as
its own logical root: listings, hydration, writes, deletes, and renames all
stay scoped to objects under the prefix, and the rest of the bucket is
invisible.

## Architecture

`s3files` is modeled directly on AWS [S3 Files][s3files]. AWS provides the
"bucket as a file system" experience by exposing an EFS-backed file system
over NFS to AWS-managed compute. This project provides the equivalent
experience on a Windows desktop by implementing a ProjFS provider in user
space — `PrjFlt.sys` is the kernel side, and `s3files` is the provider that
hydrates entries from S3 and propagates local changes back.

```
 ┌─────────────────────┐  StartDirectoryEnumeration / GetPlaceholderInfo
 │  Windows Shell      │  GetFileData
 │  (PrjFlt.sys)       │ ───────────────────────────────────┐
 └─────────┬───────────┘                                    │
           │ placeholders                                   ▼
           │ + hydrated bytes                    ┌─────────────────────┐
 ┌─────────▼───────────┐  WriteFileData /        │  ProjFsProvider     │
 │  C:\…\S3Files       │  WritePlaceholderInfo   │  (IRequiredCallbacks)│
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

- [`ProjFsProvider`](src/S3Files.Windows/ProjFs/ProjFsProvider.cs) implements
  `IRequiredCallbacks` from the managed ProjFS wrapper. Directory enumeration,
  placeholder metadata, and on-demand hydration all flow through here.
- [`NotificationCallbacks`](src/S3Files.Windows/ProjFs/NotificationCallbacks.cs)
  receives ProjFS notifications for local writes / deletes / renames and
  forwards them to the S3 backend.
- [`S3Backend`](src/S3Files.Windows.Core/S3/S3Backend.cs) wraps AWSSDK.S3 with
  the small, ProjFS-shaped surface the provider needs (list, head, range
  read, upload, delete, rename-by-copy). Uploads above 8 MiB are routed
  through `TransferUtility` so large files are split into 5 MiB parts and
  uploaded in parallel. It lives in a cross-platform Core library so
  integration tests can run against LocalStack on Linux without pulling in
  the Windows-only ProjFS bindings. When `--prefix` is set, the backend
  transparently rewrites virtualization-root-relative paths into the
  full bucket key (`<prefix>/<path>`) on every API call.
- [`S3ChangeWatcher`](src/S3Files.Windows.Core/Sync/S3ChangeWatcher.cs)
  periodically re-lists the bucket, diffs against an in-memory snapshot, and
  pushes external changes back into ProjFS. As in AWS S3 Files, the S3 bucket
  is treated as the source of truth: if a remote change collides with an
  unsynced local edit, the local copy is moved to a `.s3files-lost+found`
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
dotnet build S3Files.Windows.slnx -c Debug
dotnet run --project src\S3Files.Windows -- --bucket my-bucket --root-folder C:\Users\you\S3Files
```

### Release build (Native AOT, single binary)

```powershell
dotnet publish src\S3Files.Windows -c Release -r win-x64 -o publish\win-x64
```

The output is a self-contained `s3files.exe`. End users do **not** need the
.NET runtime installed.

### Tests

```powershell
# Unit tests (Windows or Linux)
dotnet test tests\S3Files.Windows.UnitTests

# Integration tests against LocalStack (requires Docker)
dotnet test tests\S3Files.Windows.IntegrationTests
```

The integration test project targets `net10.0` and only references the
cross-platform `S3Files.Windows.Core` library, so it can run on Linux CI
runners against [LocalStack](https://github.com/localstack/localstack) via
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
   `s3files.exe` with no JIT, no ReadyToRun, and no managed runtime install
   on the end user's machine — the startup and per-call cost is comparable
   to a native binary while we keep C#'s ergonomics for the AWS SDK and the
   ProjFS callbacks.

The cross-platform pieces (`S3Files.Windows.Core`) target plain `net10.0`
and stay AOT-compatible (`IsAotCompatible=true`), which is what lets
LocalStack-based integration tests run on Linux CI.

[projfs-nuget]: https://www.nuget.org/packages/Microsoft.Windows.ProjFS
[simple-provider]: https://github.com/microsoft/ProjFS-Managed-API

## References

- [AWS S3 Files — official documentation][s3files] — the experience this
  project is reproducing on Windows
- [Understanding how synchronization works (S3 Files)](https://docs.aws.amazon.com/AmazonS3/latest/userguide/s3-files-synchronization.html)
- [Windows Projected File System (ProjFS) overview][projfs]
- [Microsoft `ProjFS-Managed-API` SimpleProvider sample][simple-provider]
- [.NET Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [AWS SDK for .NET — S3](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/s3-apis-intro.html)

## License

Released under the [MIT License](./LICENSE).
