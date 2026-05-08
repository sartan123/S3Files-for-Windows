# OSVFS — Object Storage Virtual File System for Windows

[English README](./README.md)

[![CI](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)

OSVFS はクラウドオブジェクトストレージのバケットを Windows のローカル
フォルダーとしてマウントするツールです。オンデマンドの hydrate と双方向
同期を [Windows Projected File System (ProjFS)][projfs] の上で実現します。
位置づけとしては **`rclone mount` の「ドライバ不要」な Windows 代替**で
あり、ProjFS は Windows 10 1809 以降と Windows 11 にオプション機能として
標準搭載されているため、OSVFS の利用にあたって WinFsp などのサードパーティ
製カーネルドライバを別途インストールする必要はありません。

現在のビルドには Amazon S3 のバックエンドが同梱されています。オブジェクト
ストア抽象化は最初から provider-neutral に設計されており、**追加プロバイダー
(Google Cloud Storage / Azure Blob Storage) にも同じ `--provider` フラグの
下で対応予定**です。詳細は下の[対応バックエンド](#対応バックエンド)を参照
してください。

[projfs]: https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system

## 概要

OSVFS は OneDrive の Files On-Demand と同じ感覚で、クラウドオブジェクト
ストレージを Windows エクスプローラーから扱えるようにします。フルダウン
ロードなしでディレクトリを参照でき、ファイル本体は初回アクセス時にオン
デマンドで hydrate され、ローカルでの書き込み・削除・リネームはオブジェクト
ストアへ反映されます。バケット側の外部変更はバックグラウンドのポーリングで
取り込みます。

カーネル側は OneDrive Files On-Demand や VFS for Git でも使われている
ProjFS が担い、`osvfs` 自体は通常のユーザーモードプロセスとして動作するため、
独自のカーネルドライバーをインストール / 署名する必要は一切ありません。

## `rclone mount` との比較

`rclone` は Windows でオブジェクトストレージをマウントするためのデファクト
スタンダードであり、対応バックエンドの広さは他の追随を許しません。OSVFS は
それより狭いスコープのツールで、対応バックエンドの広さを犠牲にする代わりに
**サードパーティ製カーネルドライバの導入が一切要らない Windows 体験**に
特化しています。

| | OSVFS | `rclone mount` |
| --- | --- | --- |
| カーネル要素 | Windows 標準搭載の **ProjFS** (オプション機能を有効化するだけ。ドライバインストールなし) | **WinFsp** — 別途カーネルドライバの MSI インストールが必要 |
| 配布物 | 単一の署名済み `osvfs.exe` (Native AOT) | `rclone.exe` + WinFsp MSI |
| AppLocker / WDAC との相性 | サードパーティ製カーネルドライバを許可リストに入れる必要なし | WinFsp カーネルドライバをポリシー上で許可する必要あり |
| エクスプローラー統合 | ネイティブな ProjFS プレースホルダー (OneDrive と同じ "online-only" モデル) | FUSE ライクなマウント。ファイルは常に実体ありのように見える |
| 対応バックエンド (現在) | S3 (GCS / Azure Blob は同じ `--provider` 抽象化の下で開発予定) | 70 種類以上 |
| ランタイム依存 | なし (Native AOT) | なし (Go の単一バイナリ) |

OSVFS が未対応のバックエンドを使いたい場合は、引き続き rclone を選んで
ください。「Windows でオブジェクトストレージを OneDrive のように扱いたい、
ただしカーネルドライバの追加インストールは避けたい」というニーズに対しては
OSVFS が選択肢になります。

## 対応バックエンド

OSVFS は provider-neutral な
[`IObjectStoreBackend`](src/OSVFS.Core/ObjectStore/IObjectStoreBackend.cs)
抽象化の上に構築されており、起動時の `--provider` フラグでバックエンドを
切り替えます。マルチクラウド対応は本プロジェクトの明示的なゴールであり、
「後で拡張できるように抽象化だけ残してある」という位置づけではありません。

| プロバイダー | `--provider` の値 | 状態 |
| --- | --- | --- |
| Amazon S3 (および S3 互換: MinIO / Cloudflare R2 / Wasabi / Backblaze B2 / Ceph など) | `s3` | **対応済み** |
| Google Cloud Storage | `gcs` | 対応予定 |
| Azure Blob Storage | `azureblob` | 対応予定 |

## 利用手順

### 必要環境

- Windows 10 1809 (ビルド 17763) 以降、または Windows 11
- Windows オプション機能 **`Client-ProjFS`** が有効化されていること
- AWS SDK の標準的な認証情報チェーン (環境変数 / 共有プロファイル / IAM ロール
  など) で解決できる AWS 認証情報、または下記
  [AWS 認証情報の管理](#aws-認証情報の管理)で説明する OSVFS 内蔵の暗号化スト
  アに保存した認証情報
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
| `--aws-profile` | `osvfs credentials set --profile <name>` で保存済みの認証情報を使う (Windows Credential Manager に DPAPI で暗号化保存)。未指定時は AWS SDK の標準チェーンにフォールバックする | — |
| `--prefix` | バケット内のキープレフィックス。指定すると、このプレフィックス配下のオブジェクトだけが仮想化ルートに投影される | — |
| `--sync-interval-seconds` | 外部オブジェクトストア変更を検出するポーリング間隔。`0` で無効化 | `30` |
| `--verbose` | デバッグレベルのログを有効化 | off |

バケット内の特定のサブツリーだけを投影したい場合 (例えば
`s3://my-bucket/team-a/`) は `--prefix team-a/` を指定します。仮想化ルート
からはこのプレフィックスが論理ルートに見えるようになり、列挙・hydrate・
書き込み・削除・リネームすべてがプレフィックス配下のオブジェクトにスコー
プされます。バケット内のそれ以外のオブジェクトは見えなくなります。

### AWS 認証情報の管理

OSVFS は AWS SDK 標準の認証情報チェーン (環境変数 / 共有プロファイル
`~/.aws/credentials` / IAM ロール / IMDS) を利用できるほか、**Windows
Credential Manager 上に独自のユーザー単位の暗号化ストア**を持つことができま
す。シークレットアクセスキー (および任意の STS セッショントークン) は
**DPAPI** の `CurrentUser` スコープで暗号化されたうえで credential blob に
書き込まれるため、**保存したユーザー本人だけが同一マシン上で復号できる**
設計です。

```powershell
# 対話入力で保存 (シークレット入力はマスク表示)
osvfs credentials set --profile prod

# コマンドラインで全部渡す場合 (対話プロンプトなし)
osvfs credentials set --profile prod `
  --access-key AKIA... `
  --secret-key ... `
  --session-token ...   # 一時認証情報のときだけ

# 保存済みプロファイルのメタデータ表示 (シークレットは絶対に表示されません)
osvfs credentials get --profile prod

# OSVFS 管理下のプロファイル一覧
osvfs credentials list

# 削除
osvfs credentials remove --profile prod
```

その上で `--aws-profile <name>` を指定して `osvfs` を起動します:

```powershell
osvfs `
  --provider s3 `
  --bucket my-bucket `
  --root-folder C:\Users\you\OSVFS `
  --aws-profile prod
```

エントリは generic credential として `OSVFS:AWS:<profile>` という target
name で保存され、`LocalMachine` スコープで永続化されます (ログアウト後も
維持)。一方で blob は保存したユーザーの DPAPI 鍵で暗号化されているため、
**別ユーザーや別マシンにエントリをコピーしても復号できません**。OSVFS の
このストアはあくまで「ユーザー単位のローカルキャッシュ」として扱い、AWS
認証情報のバックアップ用途には使わないでください。

## アーキテクチャ

`osvfs` はユーザーモードで動作する ProjFS プロバイダーです。カーネル側は
Microsoft が OS 標準で提供している ProjFS フィルタードライバー
`PrjFlt.sys` が担当し、`osvfs` は設定されたオブジェクトストアからエントリ
を hydrate し、ローカルの変更を伝播させるプロバイダーとして動作します。

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

- [`ProjFsProvider`](src/OSVFS/ProjFs/ProjFsProvider.cs) — マネージド
  ProjFS ラッパーの `IRequiredCallbacks` を実装し、ディレクトリ列挙・プレースホ
  ルダー情報の書き込み・オンデマンド hydrate を担当します。
- [`NotificationCallbacks`](src/OSVFS/ProjFs/NotificationCallbacks.cs)
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
  を検出して ProjFS にプッシュします。オブジェクトストアを source of truth
  として扱い、未同期のローカル編集と衝突した場合はローカル側のコピーを
  `.osvfs-lost+found` ディレクトリに退避します。

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
dotnet run --project src\OSVFS -- --bucket my-bucket --root-folder C:\Users\you\OSVFS
```

### Release ビルド (Native AOT、単一バイナリ)

```powershell
dotnet publish src\OSVFS -c Release -r win-x64 -o publish\win-x64
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

- [Windows Projected File System (ProjFS) ドキュメント][projfs]
- [Microsoft `ProjFS-Managed-API` SimpleProvider サンプル][simple-provider]
- [.NET Native AOT デプロイ](https://learn.microsoft.com/ja-jp/dotnet/core/deploying/native-aot/)
- [AWS SDK for .NET — S3](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/s3-apis-intro.html)
- [`rclone`](https://rclone.org/) — クロスプラットフォームな対抗ツール。
  OSVFS は「ドライバ追加不要 / Windows 専用」の代替として位置付けています。
- [WinFsp](https://winfsp.dev/) — `rclone mount` が依存しているカーネル
  ドライバ。OSVFS では Windows 標準の ProjFS で置き換えています。

## ライセンス情報

[MIT License](./LICENSE) で公開しています。
