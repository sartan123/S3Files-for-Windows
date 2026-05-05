# Security Policy

## Supported Versions

`s3files` is pre-1.0 software. Security fixes are issued only against the
**latest tagged release** on the `main` branch. Older tags are not patched.

| Version | Supported          |
| ------- | ------------------ |
| Latest release on `main` | :white_check_mark: |
| Older tags               | :x:                |

## Reporting a Vulnerability

**Please do not open a public GitHub issue for security vulnerabilities.**

Use GitHub's **Private Vulnerability Reporting** instead, which keeps the
report private until a fix is published:

1. Go to the [Security tab](https://github.com/sartan123/S3Files-for-Windows/security)
   of this repository.
2. Click **Report a vulnerability**.
3. Fill in a clear description, reproduction steps, affected version, and
   the impact you observed (e.g. credential exposure, arbitrary file write
   outside the virtualization root, privilege escalation).

If you cannot use Private Vulnerability Reporting, you may instead contact
the maintainer through the email address shown in their [GitHub profile](https://github.com/sartan123).

### What to include

- Affected version / commit SHA
- Steps to reproduce, ideally with a minimal repro
- Expected vs. actual behavior
- Impact assessment (confidentiality / integrity / availability)
- Any suggested mitigation, if known

### Response timeline

This is a personal open-source project, not a vendor product, so response
times are best-effort:

- **Initial acknowledgement:** within 7 days
- **Triage and severity assessment:** within 14 days
- **Fix or mitigation plan:** depends on severity; critical issues are
  prioritized

You will be credited in the release notes for the fix unless you request
otherwise.

## Scope

In scope:

- The `s3files` host process and its handling of S3 credentials, ProjFS
  callbacks, local file writes, and the `.s3files-lost+found` quarantine
  directory.
- Code under [`src/`](./src) and [`tests/`](./tests) in this repository.

Out of scope (please report these to the upstream project instead):

- Vulnerabilities in [AWS SDK for .NET](https://github.com/aws/aws-sdk-net)
- Vulnerabilities in [`Microsoft.Windows.ProjFS`](https://www.nuget.org/packages/Microsoft.Windows.ProjFS)
  or the underlying `PrjFlt.sys` kernel component
- Vulnerabilities in the .NET runtime or Native AOT toolchain
- Issues that require an attacker who already has write access to the
  user's machine or to the target S3 bucket

## Security-relevant configuration notes

- `s3files` uses the standard AWS SDK credential chain. Treat the host
  process as having the same trust level as any other tool that has
  access to those credentials.
- The virtualization root is a normal Windows directory; standard NTFS
  ACLs apply. Do not place the root in a directory that other,
  less-trusted users on the machine can read or write.
- The `.s3files-lost+found` directory may contain copies of local edits
  that lost a sync conflict. Treat its contents with the same sensitivity
  as the bucket itself.
