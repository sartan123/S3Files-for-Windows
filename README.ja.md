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
| `--bandwidth-up` | アップロード帯域の上限。サフィックス無しはバイト/秒、`K`/`M`/`G` は KiB/s, MiB/s, GiB/s (例: `5M` = 5 MiB/s)。未指定もしくは `0` で無制限 | — |
| `--bandwidth-down` | ダウンロード帯域の上限。書式は `--bandwidth-up` と同じ | — |
| `--multipart-threshold` | このサイズ以上のアップロードを multipart に切り替える閾値。サフィックス書式は `--bandwidth-up` と同じ | `8M` |
| `--multipart-part-size` | multipart アップロードの 1 パートサイズ。`5M`〜`5G` の範囲で指定 | `5M` |
| `--log-format` | コンソールログの出力形式: `text` (1 行ずつの人間可読形式) または `json` (1 行 1 JSON オブジェクト、UTC タイムスタンプ。Datadog / Loki などのログ集約基盤向け) | `text` |
| `--verbose` | デバッグレベルのログを有効化 | off |

バケット内の特定のサブツリーだけを投影したい場合 (例えば
`s3://my-bucket/team-a/`) は `--prefix team-a/` を指定します。仮想化ルート
からはこのプレフィックスが論理ルートに見えるようになり、列挙・hydrate・
書き込み・削除・リネームすべてがプレフィックス配下のオブジェクトにスコー
プされます。バケット内のそれ以外のオブジェクトは見えなくなります。

### 帯域制御

`osvfs` は長時間バックグラウンドで動くプロセスのため、大きな hydrate や
アップロードが回線を食い潰すと同居アプリケーションのレスポンスを劣化させ
ます。`--bandwidth-up` / `--bandwidth-down` (または
[`osvfs.toml`](#設定ファイル)) で双方向独立に上限を指定できます。

```powershell
osvfs `
  --bucket my-bucket `
  --root-folder C:\Users\you\OSVFS `
  --bandwidth-up 5M `       # アップロードを 5 MiB/s に制限
  --bandwidth-down 10M      # ダウンロードを 10 MiB/s に制限
```

書式は rclone の `--bwlimit` に倣っています。サフィックス無しはバイト/秒、
`K` / `M` / `G` はそれぞれ KiB/s / MiB/s / GiB/s を意味します
(`5M` = 5 MiB/s)。フラグを指定しないか `0` を指定すると、その方向は無制限
です。アップロードペイロードのストリームとダウンロードレスポンスストリー
ムの両方をトークンバケットで律速するため、`TransferUtility` の multipart
ワーカーもオンデマンド hydrate も同じ上限の下で動きます。

### multipart アップロードのチューニング

`osvfs` は `--multipart-threshold` 以上のアップロードを S3 multipart
パスに振り分け、ペイロードを `--multipart-part-size` のチャンクに分割
して `TransferUtility` が並列にアップロードします。既定値 (閾値 8 MiB
/ パートサイズ 5 MiB) は一般的なオフィス回線向けですが、次の場面では
明示的なチューニングが効きます。

| シナリオ | 推奨設定 | 理由 |
| --- | --- | --- |
| 太い回線 / 大きなファイル中心 | `--multipart-threshold 64M --multipart-part-size 64M` | 大きなパートにすればリクエスト単位のオーバーヘッドが薄まり、複数 GiB のファイルでもパート数を抑えられる |
| 小さなファイルが大量 | `--multipart-threshold 16M` (パートは 5M のまま) | 小さなファイルで multipart のセッション交渉を省き、単一 PUT のほうが速い領域を広げる |
| 不安定な回線 | 既定値 | パートが小さいほどリトライ時の再送量も小さい |

S3 はパートサイズに 3 つの上限を設けています。いずれを破っても
`osvfs` は起動を拒否し、サーバ側も Complete 時にアップロードを失敗
させます。

- `--multipart-part-size` は **5 MiB (`5M`) 以上**であること。最終
  パート以外でこれより小さいパートは S3 が拒否します。
- `--multipart-part-size` は **5 GiB (`5G`) 以下**であること。これを
  超えるパートサイズは S3 がサポートしません。
- 1 つの multipart アップロードは **最大 10 000 パート**です。した
  がって 1 オブジェクトの最大サイズは `パートサイズ × 10 000` (16 MiB
  パートなら最大 160 GiB、64 MiB パートなら最大 640 GiB) です。
  扱う最大ファイルサイズが収まるよう、十分大きなパートサイズを選んで
  ください。

### 大規模バケット運用時の同期間隔

`osvfs` は外部オブジェクトストアの変更を `--sync-interval-seconds`
(既定 `30` 秒) ごとのバケット再列挙で検出します。各ポーリングは
ListObjectsV2 の全ページ (`NextContinuationToken` を自動引き継ぎ) を
走査するため、バケット全体 — `--prefix` を指定した場合はそのサブツリー —
が tick ごとにスキャンされます。

S3 の ListObjectsV2 は 1 ページあたり 1000 キーが上限のため、ポーリング
1 回の所要時間は監視対象プレフィックス配下のオブジェクト数にほぼ比例して
増えます。目安として、近接リージョンの本番 S3 に対して 1 ページの
ListObjectsV2 は十数〜数百ミリ秒程度で返るので、10 万オブジェクトの
バケットなら ~100 ラウンドトリップ・数秒分の listing が tick ごとに
発生します。listing 時間が `--sync-interval-seconds` に近づいたら、
ポーリングが重ならないように間隔を伸ばすか、`--prefix` で対象範囲を
絞ってください。インテグレーションテスト
`S3BackendListPaginationTests` は LocalStack 上で計測した
`ms / 1000 keys` をログ出力するので、環境ごとの数値を出すたたき台に
使えます。

### 構造化ログ

既定では `osvfs` は人間可読な 1 行形式のテキストログをコンソールに出力
します。`--log-format json` (または [`osvfs.toml`](#設定ファイル) で
`log-format = "json"`) を指定すると、Datadog / Loki などのログ集約基盤
が正規表現なしでパースできる構造化ストリームに切り替わります。

```powershell
osvfs --bucket my-bucket --root-folder C:\Users\you\OSVFS --log-format json
```

各ログエントリは 1 つの JSON オブジェクトを 1 行として書き出します
(プラットフォームの改行コードで終端)。フィールド名は
`Microsoft.Extensions.Logging.Console` の JSON フォーマッタが出力する
キーに準拠します:

| フィールド | 説明 |
| --- | --- |
| `Timestamp` | UTC タイムスタンプ。`yyyy-MM-ddTHH:mm:ss.fffZ` 形式 |
| `EventId` | ログエントリの `EventId.Id` (未指定時は `0`) |
| `LogLevel` | `Trace` / `Debug` / `Information` / `Warning` / `Error` / `Critical` |
| `Category` | ロガーのカテゴリ。通常はソース型のフルネーム (`OSVFS`、`OSVFS.ProjFs.ProjFsProvider` など) |
| `Message` | プレースホルダー置換後の最終的なメッセージ文字列 |
| `State` | 元のメッセージテンプレートと、`{Bucket}` などの名前付きプレースホルダーをそれぞれ独立したプロパティとして保持するオブジェクト。下流で構造化フィルタリングに使える |
| `Exception` | 例外が添付されている場合のみ存在。フォーマット済みの例外文字列 |

サンプル (可読性のため整形しているが、実際は 1 行で出力される):

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

### 設定ファイル

ルートコマンド `osvfs` に渡せるすべてのオプションは TOML 設定ファイルから
も指定できます。次の順で検索されます。

1. `./osvfs.toml` — カレントディレクトリ (プロジェクトローカル)
2. `%APPDATA%\OSVFS\config.toml` — ユーザー単位 (マシン共通)

プロジェクトローカルの値はユーザーローカルの値をキーごとに上書きし、コマ
ンドライン引数は両方を上書きします。安定した既定値はファイルに置きつつ、
個別オプションだけを CLI で都度上書きする使い方ができます。

`credentials` サブコマンドは設定ファイルの影響を受けません。常に CLI 引数
と対話プロンプトのみが入力源です。

```toml
# ./osvfs.toml もしくは %APPDATA%\OSVFS\config.toml
provider             = "s3"
bucket               = "my-bucket"
root-folder          = "C:/Users/you/OSVFS"
endpoint-url         = "http://localhost:4566"   # 任意
region               = "ap-northeast-1"          # 任意
prefix               = "team-a/"                 # 任意
aws-profile          = "prod"                    # 任意
bandwidth-up         = "5M"                      # 任意。"0" / 省略で無制限
bandwidth-down       = "10M"                     # 任意。"0" / 省略で無制限
multipart-threshold  = "8M"                      # 任意
multipart-part-size  = "16M"                     # 任意。5M〜5G
log-format           = "text"                    # 任意。"text" または "json"
verbose              = false
sync-interval-seconds = 30
```

編集用のサンプル [`osvfs.toml.example`](./osvfs.toml.example) をリポジトリ
ルートに同梱しています。`dotnet publish` 時には `osvfs.exe` と同じ階層にも
コピーされるので、`osvfs.toml` (または `%APPDATA%\OSVFS\config.toml`) に
リネームして必要なキーをコメントアウト解除するだけで使えます。

キーはケバブケース (`root-folder`) とスネークケース (`root_folder`) のどち
らも受け付けます。CLI フラグ名と一致するケバブケースが推奨です。設定ファ
イルを置けば、通常のマウントは次のように短く済みます。

```powershell
osvfs                       # オプションはすべて osvfs.toml から取得
osvfs --bucket other-bucket # 設定ファイルを使いつつ --bucket だけ上書き
```

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
  `--multipart-threshold` (既定 8 MiB) 以上のアップロードは `TransferUtility`
  経由で `--multipart-part-size` (既定 5 MiB) のパートに分割して並列アップロード
  されます。クロスプラットフォームな Core ライブラリに
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
