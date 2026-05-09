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
Storage and Azure Blob Storage) are planned** behind the same `provider`
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
| Backends today | S3 (GCS / Azure Blob planned behind the same `provider` flag) | 70+ backends |
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
  for the rationale and the `allow-unversioned` escape hatch. The
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
and the recoverability story does not apply, set `allow-unversioned = true`
in `osvfs.toml` to bypass the safety check.

Enable ProjFS once, in an elevated PowerShell session:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All
```

### Run

OSVFS reads every per-mount setting from a TOML configuration file (see
[Configuration file](#configuration-file) for the full key reference and the
multi-mount layout). The shortest possible config is:

```toml
# ./osvfs.toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
```

With that file in the current directory (or at
`%APPDATA%\OSVFS\config.toml`):

```powershell
osvfs                            # start the configured mount
osvfs mount-all                  # start every [[mount]] in the config (multi-mount form)
osvfs mount --name personal      # start one mount by name
```

Open the configured root folder in Explorer and the bucket contents appear.

### Command-line surface

OSVFS is intentionally configuration-driven: every per-mount setting
(`bucket`, `root-folder`, `region`, `aws-profile`, `bandwidth-up`,
`retry-max-attempts`, …) lives only in `osvfs.toml`. The command line
exposes just three things:

| Surface | Purpose |
| --- | --- |
| Sub-commands (`mount`, `mount-all`, `credentials`) | Pick which mount(s) to start, or manage the encrypted credential store. |
| `--name <mount>` | Selects an entry from the `[[mount]]` array on `osvfs mount`. |
| `--verbose`, `--log-format` | Process-level overrides for one-off debugging. The TOML keys (`verbose`, `log-format`) are still honoured; the CLI flags simply win when both are present. |

To project only a sub-tree of a bucket — for example
`s3://my-bucket/team-a/` — set `prefix = "team-a/"` in the mount entry.
The virtualization root then mirrors that prefix as its own logical root:
listings, hydration, writes, deletes, and renames all stay scoped to objects
under the prefix, and the rest of the bucket is invisible.

### Bandwidth limits

`osvfs` runs as a long-lived background process, so a single large hydration
or upload can saturate the link and starve other applications. Set
`bandwidth-up` / `bandwidth-down` in
[`osvfs.toml`](#configuration-file) to cap each direction independently:

```toml
bucket         = "my-bucket"
root-folder    = "C:/Users/you/OSVFS"
bandwidth-up   = "5M"       # cap uploads at 5 MiB/s
bandwidth-down = "10M"      # cap downloads at 10 MiB/s
```

Values follow the rclone `--bwlimit` convention: a plain number is bytes per
second, and the `K` / `M` / `G` suffixes mean KiB/s, MiB/s, and GiB/s
respectively (`5M` = 5 MiB/s). Omitting the key — or setting it to `0` —
leaves that direction unlimited. The limit is enforced through a token
bucket on the upload payload stream and the download response stream, so
`TransferUtility`'s multipart workers and the on-demand hydration path are
both paced by the same ceiling.

### Tuning multipart uploads

`osvfs` routes any upload at or above `multipart-threshold` through the
S3 multipart path, splitting the payload into `multipart-part-size`
chunks that `TransferUtility` uploads in parallel. The defaults (8 MiB
threshold, 5 MiB parts) target a typical office connection, but two
common scenarios benefit from explicit tuning:

| Scenario | Suggested settings | Why |
| --- | --- | --- |
| Fat links / large files | `multipart-threshold = "64M"`, `multipart-part-size = "64M"` | Larger parts amortize per-request overhead and cut the part count on multi-GiB files. |
| Many tiny edits | `multipart-threshold = "16M"` (keep 5M parts) | Skips multipart for small files where a single PUT is faster than negotiating an upload session. |
| Constrained networks | Keep defaults | Smaller parts mean a network blip retries less data. |

S3 enforces three hard limits on the part size — you must stay inside
all of them or `osvfs` refuses to start, and the service rejects the
upload at completion time:

- `multipart-part-size` must be **≥ 5 MiB** (`5M`). Smaller parts are
  rejected by S3 except for the last part of an upload.
- `multipart-part-size` must be **≤ 5 GiB** (`5G`). Larger parts
  exceed the per-part ceiling.
- A single multipart upload is capped at **10 000 parts**, so the
  largest object you can upload is `part-size × 10 000` (16 MiB parts
  → 160 GiB max; 64 MiB parts → 640 GiB max). Pick a part size large
  enough to fit your largest expected file.

### Retry policy

Transient object-store failures are retried by the AWS SDK pipeline. OSVFS
configures the client with `RetryMode.Adaptive` (the SDK's adaptive
client-side throttling, which combines the standard exponential backoff with
a token bucket that suppresses request bursts when the service signals
overload) and `MaxErrorRetry = retry-max-attempts − 1`. The SDK's built-in
retry classifier decides which failures are eligible:

| Failure | Retried? | Notes |
| --- | --- | --- |
| HTTP 5xx (`500`, `502`, `503`, `504`, …) | Yes | Server-side / load-balancer errors. Treated as transient by the SDK. |
| HTTP 408 `Request Timeout` | Yes | Server-side timeout; the SDK retries with backoff. |
| `Throttling` / `ThrottlingException` / `RequestThrottled*` / `TooManyRequestsException` / `ProvisionedThroughputExceededException` / `RequestLimitExceeded` / `SlowDown` | Yes | AWS throttling family. Adaptive mode also slows the next request via the token bucket. |
| `RequestTimeout` / network errors / connection resets | Yes | Local socket / connection errors. |
| HTTP 4xx other than 408 (`400`, `401`, `403`, `404`, `409`, `412`, …) | No | Caller-side errors (bad request, missing object, permissions). Surfaced immediately. |
| `OperationCanceledException` / `TaskCanceledException` | No | Cancellation propagates without retry. |

The schedule is owned by the SDK: it uses exponential backoff with jitter
inside `MaxErrorRetry` retries. When `retry-max-attempts` is `1` the SDK
performs zero retries (the first attempt is the only one). The SDK's
`TransferUtility` retries individual multipart parts on its own — under
`retry-max-attempts = 3` a single failing part can be re-uploaded up to
three times without restarting the whole multi-GiB upload.

```toml
bucket             = "my-bucket"
root-folder        = "C:/Users/you/OSVFS"
retry-max-attempts = 5         # 5 total attempts (1 initial + 4 retries)
```

### Change detection modes

OSVFS supports two strategies for detecting changes that other clients (the
AWS console, another `aws s3 cp`, a teammate's machine) make to the bucket.
Pick the one that matches your bucket size, latency budget, and how much
server-side configuration you can do.

| Mode | Latency | Bucket-side setup | When to use |
| --- | --- | --- | --- |
| `polling` (default) | Up to `sync-interval-seconds` (default 30 s) | None — works on any bucket the AWS credentials can list. | Small or quiet buckets; environments where you don't have permission to add EventBridge / SQS. |
| `events` | Seconds (long-poll wakeup + SQS round-trip) | Bucket → EventBridge → SQS pipeline (steps below). | Large buckets where re-listing is expensive, or when you need near-real-time visibility on remote edits. |

`events` needs an SQS queue that receives `Object Created` and `Object Deleted`
notifications produced by EventBridge. The legacy direct S3-to-SQS
notification format (`Records[]`) is **not** parsed; configure EventBridge
instead.

#### Setting up the SQS queue, EventBridge rule, and bucket notifications

The four steps below create the minimal pipeline needed. Substitute your
account ID, region, and bucket name. Each step shows the AWS CLI command;
the same actions are available in the console under SQS / EventBridge / S3.

1. **Create the SQS queue.**

   ```bash
   aws sqs create-queue --queue-name osvfs-changes \
     --attributes ReceiveMessageWaitTimeSeconds=20
   ```

   Long-polling on the queue side reduces empty receives.

2. **Allow EventBridge to publish to the queue.** Save this policy (replacing
   the placeholders) as `queue-policy.json`:

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [{
       "Effect": "Allow",
       "Principal": { "Service": "events.amazonaws.com" },
       "Action": "sqs:SendMessage",
       "Resource": "arn:aws:sqs:REGION:ACCOUNT_ID:osvfs-changes"
     }]
   }
   ```

   Then attach it to the queue:

   ```bash
   aws sqs set-queue-attributes \
     --queue-url QUEUE_URL \
     --attributes Policy=file://queue-policy.json
   ```

3. **Enable EventBridge notifications on the bucket** (S3 → bucket → Properties
   → "Amazon EventBridge", or:)

   ```bash
   aws s3api put-bucket-notification-configuration \
     --bucket YOUR_BUCKET \
     --notification-configuration '{"EventBridgeConfiguration":{}}'
   ```

4. **Create the EventBridge rule that targets the queue.** Save the pattern as
   `event-pattern.json`:

   ```json
   {
     "source": ["aws.s3"],
     "detail-type": ["Object Created", "Object Deleted"],
     "detail": { "bucket": { "name": ["YOUR_BUCKET"] } }
   }
   ```

   Then create the rule and target:

   ```bash
   aws events put-rule \
     --name osvfs-bucket-changes \
     --event-pattern file://event-pattern.json

   aws events put-targets \
     --rule osvfs-bucket-changes \
     --targets 'Id=osvfs-sqs,Arn=arn:aws:sqs:REGION:ACCOUNT_ID:osvfs-changes'
   ```

The IAM identity that OSVFS runs as needs `sqs:ReceiveMessage`,
`sqs:DeleteMessage`, and (when `event-queue` is a bare name)
`sqs:GetQueueUrl` on the queue.

Then point the mount at the queue in your config:

```toml
bucket        = "my-bucket"
root-folder   = "C:/Users/you/OSVFS"
change-source = "events"
event-queue   = "https://sqs.ap-northeast-1.amazonaws.com/123456789012/osvfs-changes"
```

> One queue per virtualization root. Two `osvfs` instances sharing a queue
> would each see only half of the messages.

### On-demand sync

`polling` mode supports two reconciliation strategies via `sync-mode`:

| Mode | What gets re-listed each tick | API cost | When to use |
| --- | --- | --- | --- |
| `on-demand` (default) | Only the directories the user has actually visited through ProjFS, plus their ancestor chain | Scales with the **visited-directory count**, independent of bucket size | The default — matches ProjFS's on-demand model and the AWS S3 Files [synchronization design](https://docs.aws.amazon.com/AmazonS3/latest/userguide/s3-files-synchronization.html). |
| `full` | The entire bucket (or `prefix` subtree) | Scales with **total object count** (one `ListObjectsV2` page per 1000 keys, every tick) | When you need the bucket-wide source-of-truth guarantee, or for small/quiet buckets where the cost is negligible. Preserves the original Phase&nbsp;1 behavior. |

### Sync interval for large buckets

`osvfs` detects external object-store changes by re-listing the bucket every
`sync-interval-seconds` (default `30`). Under `sync-mode = "full"` each poll
walks every page of `ListObjectsV2` for the configured prefix; under
`sync-mode = "on-demand"` each poll re-lists every visited directory once with
`Delimiter='/'` (one paged request per directory).

Under `full` mode the wall time of a poll grows roughly linearly with the
number of objects under the watched prefix because S3 caps a single
`ListObjectsV2` page at 1000 keys. As a rough planning guide, a single
`ListObjectsV2` page typically returns in tens to low-hundreds of
milliseconds against AWS S3 from a nearby region, so a 100k-object bucket
needs ~100 round-trips and a few seconds of listing per tick. If the listing
time approaches `sync-interval-seconds`, raise the interval (or scope the
projection with `prefix`) so polls do not overlap and starve other I/O.

Under `on-demand` mode the cost instead scales with the number of visited
directories, so a bucket with 100 directories each containing 10k files
costs roughly one paged `ListObjectsV2` per directory the user has opened,
not one per 1000 keys in the bucket.

### Structured logging

By default `osvfs` writes single-line, human-readable log entries to the
console. Set `log-format = "json"` in
[`osvfs.toml`](#configuration-file) (or pass `--log-format json` for an
ad-hoc override) to switch to a structured stream that log shippers such
as Datadog or Loki can parse without regex:

```powershell
osvfs --log-format json    # one-off override; the file value applies otherwise
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

### User-defined object metadata round-trip

S3 lets every object carry an arbitrary number of user-defined headers (the
`x-amz-meta-*` family) — for example a `tag`, an `author`, or any
application-specific marker the upload tool wrote. OSVFS preserves these
headers across hydration and re-upload so a local edit doesn't strip them.

When a placeholder is created, OSVFS reads the bucket-side user metadata via
`HeadObject` and mirrors every entry into a Windows NTFS **alternate data
stream** named `:osvfs-user-meta` attached to the placeholder. The stream is
plain UTF-8 with one `key=value` pair per line. Names are normalized to
lowercase (matching the case S3 uses on the wire).

When a local edit is uploaded, OSVFS reads the same ADS back out and
reattaches every entry as `x-amz-meta-*` on the `PutObject` (or multipart)
request, so the headers survive the local edit cycle bit-for-bit. Newly
created local files have no stream attached and upload with no user
metadata, exactly as before.

You can inspect the mirrored metadata from PowerShell:

```powershell
# List streams attached to a hydrated placeholder
Get-Item C:\Users\you\OSVFS\meta\file.txt -Stream *

# Dump the metadata (UTF-8, one key=value per line)
Get-Content C:\Users\you\OSVFS\meta\file.txt -Stream osvfs-user-meta
```

AWS limits the combined `x-amz-meta-*` name+value byte count to **2 KiB per
object**. OSVFS pre-validates uploads against the same limit and surfaces a
clear error before the network round-trip, instead of forwarding an
oversized request that S3 would reject with an opaque 400.

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
retry-max-attempts   = 3                         # optional, 1 disables retries
log-format           = "text"                    # optional, "text" or "json"
allow-unversioned    = false                     # DANGER: skip the bucket-versioning safety check
verbose              = false
sync-interval-seconds = 30
change-source        = "polling"                 # "polling" | "events"
sync-mode            = "on-demand"               # "on-demand" | "full" — only used by polling
event-queue          = ""                        # SQS URL/name, required for events
```

A ready-to-edit sample is shipped as
[`osvfs.toml.example`](./osvfs.toml.example) at the repo root and is also
copied next to `osvfs.exe` on `dotnet publish`, so you can rename it to
`osvfs.toml` (or `%APPDATA%\OSVFS\config.toml`) and uncomment the keys you
need.

Both kebab-case (`root-folder`) and snake_case (`root_folder`) keys are
accepted; kebab is preferred. With a config file in place, a typical mount
is just:

```powershell
osvfs                       # all options sourced from osvfs.toml
```

#### Multiple mounts in a single config

A configuration file can declare more than one mount under the `[[mount]]`
table-array syntax, each with its own bucket / root-folder / region / etc.
The process-level keys (`verbose`, `log-format`) stay at the top level and
apply to every mount:

```toml
# ./osvfs.toml — multiple mounts
verbose   = false
log-format = "json"

[[mount]]
name        = "personal"
bucket      = "my-personal"
root-folder = "C:/Users/you/OSVFS-personal"

[[mount]]
name        = "work"
bucket      = "my-work"
root-folder = "C:/Users/you/OSVFS-work"
prefix      = "team-a/"
aws-profile = "prod-readonly"
```

Each `[[mount]]` entry takes the same per-mount keys as the legacy single-
mount form (everything except `verbose` and `log-format`). The `name` is
required to be unique within the file; entries without an explicit `name`
are tagged `mount[0]`, `mount[1]`, etc. Mixing top-level mount keys with
`[[mount]]` entries in the same file is rejected so the precedence stays
unambiguous — pick one form per file.

When a file declares 2+ mounts, the bare root command refuses to guess
which one the operator wants. Pick one of:

```powershell
osvfs mount-all                 # start every [[mount]] in this process
osvfs mount --name personal     # start a single named mount
osvfs mount --name work         # start a different mount from the same config
```

Each mount runs its own `ProjFsProvider`; logs from a given mount land in
the `OSVFS.Mount.<name>` category so text / JSON formatters surface which
mount each line came from. Pressing Enter on the host process disposes
every mount in reverse start order.

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

Then reference the profile in your mount config:

```toml
provider    = "s3"
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
aws-profile = "prod"
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
  configured `multipart-threshold` (default 8 MiB) are routed through
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
