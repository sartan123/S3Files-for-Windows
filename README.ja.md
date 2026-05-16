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

現在のビルドには **Amazon S3 と Azure Blob Storage** のバックエンドが
同梱されています。**Google Cloud Storage** も同じ `provider` フラグの
下で対応予定です。詳細は下の[対応バックエンド](#対応バックエンド)を
参照してください。

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
| 対応バックエンド (現在) | S3 + Azure Blob (GCS は同じ `provider` 抽象化の下で対応予定) | 70 種類以上 |
| ランタイム依存 | なし (Native AOT) | なし (Go の単一バイナリ) |

OSVFS が未対応のバックエンドを使いたい場合は、引き続き rclone を選んで
ください。「Windows でオブジェクトストレージを OneDrive のように扱いたい、
ただしカーネルドライバの追加インストールは避けたい」というニーズに対しては
OSVFS が選択肢になります。

## 対応バックエンド

OSVFS は provider-neutral な
[`IObjectStoreBackend`](src/OSVFS.Core/ObjectStore/IObjectStoreBackend.cs)
抽象化の上に構築されており、起動時の `provider` フラグでバックエンドを
切り替えます。マルチクラウド対応は本プロジェクトの明示的なゴールであり、
「後で拡張できるように抽象化だけ残してある」という位置づけではありません。

| プロバイダー | `provider` の値 | 状態 |
| --- | --- | --- |
| Amazon S3 (および S3 互換: MinIO / Cloudflare R2 / Wasabi / Backblaze B2 / Ceph など) | `s3` | **対応済み** |
| Azure Blob Storage | `azureblob` | **対応済み** |
| Google Cloud Storage | `gcs` | 対応予定 |

## ドキュメント

詳細リファレンスは [`docs/`](docs/) 配下のトピック別ページに分割しています。

- **[設定](docs/configuration.ja.md)** — 必要環境、CLI 表面、`osvfs.toml`
  全キーリファレンス、マルチマウントの書式。
- **[パフォーマンスチューニング](docs/tuning.ja.md)** — 帯域制御、
  multipart のしきい値、リクエスト並列度、HTTP トランスポート、
  リトライポリシー。
- **[同期と変更イベント](docs/sync-and-events.ja.md)** — ポーリング /
  push モードの変更検出、オンデマンド同期、読み取り専用マウント、
  SQS / Event Grid の構成。
- **[可観測性](docs/observability.ja.md)** — 構造化ログ、OpenTelemetry
  トレース / メトリクス、ローカル Prometheus リスナー、ユーザー定義
  メタデータの往復保持。
- **[認証とクレデンシャル](docs/credentials.ja.md)** — AWS shared profile
  / SSO / `aws login` と Azure の 4 認証ブランチ (connection string /
  SAS / Managed Identity / DefaultAzureCredential)。
- **[トラブルシューティング](docs/troubleshooting.ja.md)** —
  `osvfs doctor` セルフチェックと `osvfs lost-and-found` 復元 CLI。

## クイックスタート

マウント設定は 2 キーだけでも動きます。

```toml
# ./osvfs.toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
```

AWS SDK のチェーンから認証情報が解決でき、バケットのバージョニングが
有効化されている前提で
([設定 → 必要環境](docs/configuration.ja.md#必要環境) に詳細)、
次のように起動します。

```powershell
osvfs                            # 設定された単一マウントを起動
osvfs mount-all                  # 設定ファイル内の [[mount]] 全部を起動
osvfs mount --name personal      # 名前指定でひとつだけ起動
```

仮想化ルートをエクスプローラーで開くと、バケットの中身が見えます。
Azure Blob (`provider = "azureblob"`) を使う場合は
[認証とクレデンシャル → Azure Blob の設定](docs/credentials.ja.md#azure-blob-の設定)
を参照してください。

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
 │ ObjectStoreChange   │ ←─────────────────────────────┘
 │ Watcher             │       SQS ReceiveMessage
 │  + LostAndFound     │ ←─────────  EventBridge ←──── (任意)
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
  `multipart-threshold` (既定 16 MiB) 以上のアップロードは `TransferUtility`
  経由で `multipart-part-size` (既定 5 MiB) のパートに分割して並列アップロード
  されます。クロスプラットフォームな Core ライブラリに
  置かれているため、Linux 上の LocalStack に対するインテグレーションテスト
  から ProjFS バインディング無しで利用できます。`prefix` を指定した場合、
  バックエンドは仮想化ルートからの相対パスを `<prefix>/<path>` の形でフル
  キーに自動展開します。
- [`ObjectStoreChangeWatcher`](src/OSVFS.Core/Sync/ObjectStoreChangeWatcher.cs)
  — 外部のバケット変更を ProjFS に反映します。変更検出は差し替え可能な
  [`IChangeSource`](src/OSVFS.Core/Sync/IChangeSource.cs) 実装が担います:
  [`OnDemandPollingChangeSource`](src/OSVFS.Core/Sync/OnDemandPollingChangeSource.cs)
  (既定) は ProjFS 経由で訪問されたディレクトリだけを `Delimiter='/'`
  付きで再列挙し、
  [`PollingChangeSource`](src/OSVFS.Core/Sync/PollingChangeSource.cs)
  (`sync-mode = "full"` で選択) はバケット全体を一定間隔で再列挙して
  メモリ内スナップショットと差分を取り、
  [`SqsChangeSource`](src/OSVFS.Core/Sync/Sqs/SqsChangeSource.cs) は
  EventBridge S3 通知が流れる SQS キューを long-poll します。オブジェクト
  ストアを source of truth として扱い、未同期のローカル編集と衝突した場合
  はローカル側のコピーを `.osvfs-lost+found` ディレクトリに退避します。

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
dotnet run --project src\OSVFS    # マウント設定は osvfs.toml から取得
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

# LocalStack + Azurite に対するインテグレーションテスト (Docker が必要)
dotnet test tests\OSVFS.Core.IntegrationTests
```

インテグレーションテストプロジェクトは `net10.0` をターゲットとし、クロスプラッ
トフォームな `OSVFS.Core` のみを参照しているため、Linux CI 上でも
[Testcontainers](https://dotnet.testcontainers.org/) と
[LocalStack](https://github.com/localstack/localstack) /
[Azurite](https://learn.microsoft.com/ja-jp/azure/storage/common/storage-use-azurite)
を使ってビルド・実行できます。

> **Azurite ServiceVersion の pin 運用について。** Azure SDK は新しい
> リリースのたびにデフォルトの `x-ms-version` を更新しますが、Azurite の
> 対応はそれより数か月遅れます。Dependabot が `Azure.Storage.Blobs` /
> `Azure.Storage.Queues` を継続的に更新できるよう、インテグレーション
> テストでは `BlobClientOptions.ServiceVersion` /
> `QueueClientOptions.ServiceVersion` を Azurite "latest" イメージが
> 解釈できる範囲に pin しています。本番側のコードパスでは options を
> null にして SDK のデフォルト最新版を使います。Azurite が新しい API
> バージョンに対応した際は、
> [`tests/OSVFS.Core.IntegrationTests/AzuriteFixture.cs`](tests/OSVFS.Core.IntegrationTests/AzuriteFixture.cs)
> の定数を更新してください。

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
