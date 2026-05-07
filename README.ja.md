# OSVFS — Object Storage Virtual File System for Windows

[English README](./README.md)

[![CI](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)

OSVFS はクラウドオブジェクトストレージのバケットを Windows のローカル
フォルダーとしてマウントするツールです。[AWS S3 Files][s3files] の体験を
モデルに、オンデマンドの hydrate と双方向同期を [Windows Projected File
System (ProjFS)][projfs] の上で実現します。現在のビルドには Amazon S3 の
バックエンドが同梱されており、オブジェクトストア抽象化は provider-neutral
なので、追加プロバイダー (GCS / Azure Blob) も同じ `--provider` フラグの
下に組み込めます。

[s3files]: https://docs.aws.amazon.com/AmazonS3/latest/userguide/s3-files.html
[projfs]: https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system

## 概要

AWS の [S3 Files][s3files] は、AWS のコンピュートリソース (EC2 / Lambda /
EKS / ECS) から S3 バケットを実ファイルシステムとして扱えるようにするサー
ビスです。フルダウンロード無しでディレクトリを参照でき、ファイル本体は
オンデマンドでロードされ、ローカルでの書き込みは S3 へ同期され、バケットへの
直接変更はファイルシステム側にも反映されます。AWS は EFS / NFS の上にこれを
実装しているため、利用できるのは AWS マネージドのコンピュート上のみです。

`osvfs` は同じユーザー体験を Windows デスクトップで提供します。バケット内の
オブジェクトは Windows エクスプローラー上にプレースホルダーとして列挙され、
初回アクセス時にオンデマンドで内容がダウンロードされます。ローカルでの書き込み
・削除・リネームはオブジェクトストアへ反映され、外部から行われたバケットの
変更はバックグラウンドのポーリングで検出してローカルに取り込みます。カーネル
側は ProjFS が担い、`osvfs` 自体は通常のユーザーモードプロセスとして動作する
ため、独自のカーネルドライバーは必要ありません。

## 利用手順

### 必要環境

- Windows 10 1809 (ビルド 17763) 以降、または Windows 11
- Windows オプション機能 **`Client-ProjFS`** が有効化されていること
- AWS SDK の標準的な認証情報チェーン (環境変数 / 共有プロファイル / IAM ロール
  など) で解決できる AWS 認証情報
- 読み書き可能な S3 バケット
- 対象バケットで **バージョニングが有効化されていること**。ローカルでの編集・
  削除は S3 では上書き PUT や `DeleteObject` として伝播するため、誤操作からの
  復旧手段としてバージョニングを必須にしています。バージョニングが Enabled で
  ない場合 `osvfs` は起動を拒否します。認証情報は併せて
  `s3:GetBucketVersioning` を許可している必要があります。

バージョニングは AWS CLI で 1 回だけ有効化します:

```powershell
aws s3api put-bucket-versioning `
  --bucket my-bucket `
  --versioning-configuration Status=Enabled
```

ProjFS は管理者権限の PowerShell で 1 回だけ有効化します:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All
```

### 起動

```powershell
osvfs `
  --provider s3 `
  --bucket my-bucket `
  --root-folder C:\Users\you\OSVFS
```

エクスプローラーで `C:\Users\you\OSVFS` を開くと、バケットの内容が表示され
ます。

### コマンドラインオプション

| オプション | 説明 | 既定値 |
| --- | --- | --- |
| `--provider` | 仮想化ルートのバックエンドとなるオブジェクトストアプロバイダー。現状 `s3` のみが完全実装。`gcs` / `azureblob` は起動時に失敗する | `s3` |
| `--bucket` | 仮想ファイルシステム経由で公開する S3 バケット / Azure コンテナー (必須) | — |
| `--root-folder` | 仮想化ルートのパス (必須) | — |
| `--endpoint-url` | S3 エンドポイント URL の上書き (LocalStack / MinIO 用) | AWS 既定 |
| `--region` | AWS リージョン (例: `us-east-1`、`ap-northeast-1`)。未指定時は SDK 標準のリージョン解決チェーン (環境変数 / プロファイル / IMDS) にフォールバックする | — |
| `--prefix` | バケット内のキープレフィックス。指定すると、このプレフィックス配下のオブジェクトだけが仮想化ルートに投影される | — |
| `--sync-interval-seconds` | 外部オブジェクトストア変更を検出するポーリング間隔。`0` で無効化 | `30` |
| `--verbose` | デバッグレベルのログを有効化 | off |

バケット内の特定のサブツリーだけを投影したい場合 (例えば
`s3://my-bucket/team-a/`) は `--prefix team-a/` を指定します。仮想化ルート
からはこのプレフィックスが論理ルートに見えるようになり、列挙・hydrate・
書き込み・削除・リネームすべてがプレフィックス配下のオブジェクトにスコー
プされます。バケット内のそれ以外のオブジェクトは見えなくなります。

## アーキテクチャ

`osvfs` は AWS の [S3 Files][s3files] を **Windows 上で再現** することを
目的としています。AWS は EFS をバックエンドにしてバケットを NFS でマウント
可能なファイルシステムとして公開することでこの体験を実現していますが、本プロ
ジェクトでは同等の体験を ProjFS プロバイダーをユーザーモードで実装することで
構築します。カーネル側は `PrjFlt.sys` が担当し、`osvfs` は設定されたオブジェ
クトストアからエントリを hydrate し、ローカルの変更を伝播させるプロバイダー
として動作します。

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
           │ ローカル書き込み                          ▼      ▼
           │ (notification callbacks)           ┌──────────────┐
 ┌─────────▼───────────┐  PUT / DELETE / COPY   │  S3 bucket   │
 │ NotificationCallbacks│ ─────────────────────→│              │
 └─────────────────────┘                        └──────┬───────┘
                                                       │
 ┌─────────────────────┐  ListObjectsV2 (定期)         │
 │ S3ChangeWatcher     │ ←─────────────────────────────┘
 │  + LostAndFound     │
 └─────────────────────┘
```

主な構成要素は次の通りです。

- [`ProjFsProvider`](src/OSVFS.Windows/ProjFs/ProjFsProvider.cs) — マネージド
  ProjFS ラッパーの `IRequiredCallbacks` を実装し、ディレクトリ列挙・プレースホ
  ルダー情報の書き込み・オンデマンド hydrate を担当します。
- [`NotificationCallbacks`](src/OSVFS.Windows/ProjFs/NotificationCallbacks.cs)
  — ローカルでの書き込み / 削除 / リネームを ProjFS の通知から受け取り、
  オブジェクトストアバックエンドに転送します。
- [`S3Backend`](src/OSVFS.Core/ObjectStore/S3/S3Backend.cs) — AWSSDK.S3 を
  プロバイダーニュートラルな [`IObjectStoreBackend`](src/OSVFS.Core/ObjectStore/IObjectStoreBackend.cs)
  の背後に置き、ProjFS プロバイダーが必要とする最小限の API (list / head /
  range read / upload / delete / copy ベースの rename) にラップします。
  8 MiB を超えるアップロードは `TransferUtility` 経由で 5 MiB のパートに分割
  して並列アップロードされます。クロスプラットフォームな Core ライブラリに
  置かれているため、Linux 上の LocalStack に対するインテグレーションテスト
  から ProjFS バインディング無しで利用できます。`--prefix` を指定した場合、
  バックエンドは仮想化ルートからの相対パスを `<prefix>/<path>` の形でフル
  キーに自動展開します。
- [`ObjectStoreChangeWatcher`](src/OSVFS.Core/Sync/ObjectStoreChangeWatcher.cs)
  — バケットを定期的に再列挙し、メモリ内スナップショットとの差分から外部変更
  を検出して ProjFS にプッシュします。AWS S3 Files と同様、オブジェクトストア
  を source of truth として扱い、未同期のローカル編集と衝突した場合はローカル
  側のコピーを `.osvfs-lost+found` ディレクトリに退避します。

## ビルド方法

### 必要なもの

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (具体的な
  バージョンは [`global.json`](./global.json) で固定)
- Visual Studio 2022 または Build Tools の **「C++ によるデスクトップ開発」**
  ワークロード (Native AOT publish に必要な `link.exe` と Windows SDK ライブラリ
  のため)
- Windows x64 (ProjFS が Windows 専用のため、ホストプロジェクトは
  `RuntimeIdentifier=win-x64` を固定)

### Debug ビルド

```powershell
dotnet build OSVFS.slnx -c Debug
dotnet run --project src\OSVFS.Windows -- --bucket my-bucket --root-folder C:\Users\you\OSVFS
```

### Release ビルド (Native AOT、単一バイナリ)

```powershell
dotnet publish src\OSVFS.Windows -c Release -r win-x64 -o publish\win-x64
```

出力は self-contained な `osvfs.exe` です。利用者側の PC に .NET ランタイム
をインストールする必要はありません。

### テスト

```powershell
# ユニットテスト (Windows / Linux どちらでも実行可能)
dotnet test tests\OSVFS.Core.UnitTests

# LocalStack に対するインテグレーションテスト (Docker が必要)
dotnet test tests\OSVFS.Core.IntegrationTests
```

インテグレーションテストプロジェクトは `net10.0` をターゲットとし、クロスプラッ
トフォームな `OSVFS.Core` のみを参照しているため、Linux CI 上でも
[Testcontainers](https://dotnet.testcontainers.org/) と
[LocalStack](https://github.com/localstack/localstack) を使ってビルド・実行
できます。

## なぜ C# (.NET) で実装しているのか？

ProjFS は Windows カーネルの機能であり、クライアントからは必ずネイティブ API
経由で呼び出すことになります。Rust / Go / C++ も合理的な選択肢ですが、本プロジェ
クトでは次の 2 点から C# を採用しています。

1. **Microsoft が ProjFS の公式マネージドラッパーを提供している。**
   NuGet パッケージ [`Microsoft.Windows.ProjFS`][projfs-nuget] は、Microsoft
   自身の [SimpleProvider サンプル][simple-provider] や VFS for Git でも使われ
   ているバインディングです。`IRequiredCallbacks` を C# で実装するだけで、
   COM / P-Invoke 境界はラッパー側に任せられます。バインディングを自前で書く
   必要がありません。
2. **Native AOT でランタイムコストを排除できる。**
   常駐するユーザーモードのファイルシステムプロバイダーには厳しいレイテンシ
   要件があります。ディレクトリ列挙や `GetFileData` の 1 バイトはユーザーの
   ホットパス上にあります。`PublishAot=true` でビルドすると JIT も R2R も
   不要な単一の静的バイナリ `osvfs.exe` が生成され、エンドユーザー側に .NET
   ランタイムをインストールする必要もなくなります。起動コスト・呼び出しコスト
   はネイティブバイナリ並みに保ちつつ、各クラウド SDK や ProjFS コールバック
   の記述には C# のエルゴノミクスをそのまま活用できます。

クロスプラットフォーム部分 (`OSVFS.Core`) は素の `net10.0` をターゲットにし、
`IsAotCompatible=true` を維持しています。これによって Linux CI 上での
LocalStack ベースのインテグレーションテストが成立します。

[projfs-nuget]: https://www.nuget.org/packages/Microsoft.Windows.ProjFS
[simple-provider]: https://github.com/microsoft/ProjFS-Managed-API

## 参考リンク

- [AWS S3 Files 公式ドキュメント][s3files] — 本プロジェクトが Windows で
  再現しようとしている体験
- [Understanding how synchronization works (S3 Files)](https://docs.aws.amazon.com/AmazonS3/latest/userguide/s3-files-synchronization.html)
- [Windows Projected File System (ProjFS) ドキュメント][projfs]
- [Microsoft `ProjFS-Managed-API` SimpleProvider サンプル][simple-provider]
- [.NET Native AOT デプロイ](https://learn.microsoft.com/ja-jp/dotnet/core/deploying/native-aot/)
- [AWS SDK for .NET — S3](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/s3-apis-intro.html)

## ライセンス情報

[MIT License](./LICENSE) で公開しています。
