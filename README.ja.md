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
(Google Cloud Storage / Azure Blob Storage) にも同じ `provider` フラグの
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
| 対応バックエンド (現在) | S3 (GCS / Azure Blob は同じ `provider` 抽象化の下で開発予定) | 70 種類以上 |
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
- 対象バケットで **バージョニングが有効化されていること**。バージョニングが
  Enabled でない場合 `osvfs` は起動を拒否します — 詳しい理由と
  `allow-unversioned` によるバイパス方法は
  [バージョニングが必要な理由](#バージョニングが必要な理由) を参照してくだ
  さい。認証情報は併せて `s3:GetBucketVersioning` を許可している必要があり
  ます。

バージョニングは AWS CLI で 1 回だけ有効化します:

```powershell
aws s3api put-bucket-versioning `
  --bucket my-bucket `
  --versioning-configuration Status=Enabled
```

#### バージョニングが必要な理由

仮想化ルート内でのローカルファイルの編集・削除は、S3 上では上書き
`PutObject` および `DeleteObject` 呼び出しとして伝播されます。バケット
バージョニングが無効な場合、これらの操作は **破壊的かつ取り消し不可能** で
す。削除されたオブジェクトは消滅し、上書きされた内容は復元できません。
バージョニングを有効化すると、各操作が新しいバージョンと削除マーカーの組と
して保存されるため、エクスプローラー上の誤操作や暴走スクリプトからも復旧
できます。

`osvfs` は起動時に対象バケットがバージョニング無効 (もしくは Suspended) の
状態であると判定すると、コピー&ペースト可能な
`aws s3api put-bucket-versioning` コマンド・バケット名・このセクションへの
リンクを含むエラーメッセージを出して起動を拒否します。

バケットをジョブ毎に再作成する CI 用途や使い捨てバケットなど、復旧性の議論
が当てはまらないシナリオでは `osvfs.toml` で `allow-unversioned = true`
を設定すると安全チェックをバイパスできます。

ProjFS は管理者権限の PowerShell で 1 回だけ有効化します:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All
```

### 起動

OSVFS はマウントごとの設定を **TOML 設定ファイル** から読み込みます (キー
の詳細と複数マウント形式は [設定ファイル](#設定ファイル) 参照)。最小構成
は次の 2 行です:

```toml
# ./osvfs.toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
```

このファイルをカレントディレクトリ (もしくは `%APPDATA%\OSVFS\config.toml`)
に置けば:

```powershell
osvfs                            # 設定済みマウントを起動
osvfs mount-all                  # [[mount]] 配列の全マウントをこのプロセスで起動
osvfs mount --name personal      # 名前指定で 1 つだけ起動
```

仮想化ルートのフォルダーをエクスプローラーで開くと、バケットの内容が表示
されます。

### コマンドラインの構成

OSVFS は意図的に**設定ファイル駆動**です。各マウントの設定 (`bucket` /
`root-folder` / `region` / `aws-profile` / `bandwidth-up` /
`retry-max-attempts` …) はすべて `osvfs.toml` のみで管理します。CLI が
受け付けるのは次の 3 つだけです。

| 構成要素 | 用途 |
| --- | --- |
| サブコマンド (`mount` / `mount-all` / `credentials` / `doctor` / `lost-and-found`) | どのマウントを起動するか、暗号化された認証情報ストアの管理、環境セルフチェック、または隔離された退避ファイルの確認・復元 |
| `--name <mount>` | `osvfs mount` で `[[mount]]` 配列の中から 1 つ選ぶ |
| `--verbose` / `--log-format` | デバッグ用のプロセスレベル一時上書き。TOML の `verbose` / `log-format` も引き続き有効で、両方ある場合は CLI が勝ちます |

バケット内の特定のサブツリーだけを投影したい場合 (例えば
`s3://my-bucket/team-a/`) はマウントエントリで `prefix = "team-a/"` を
指定します。仮想化ルートからはこのプレフィックスが論理ルートに見えるよ
うになり、列挙・hydrate・書き込み・削除・リネームすべてがプレフィックス
配下のオブジェクトにスコープされます。バケット内のそれ以外のオブジェク
トは見えなくなります。

### 帯域制御

`osvfs` は長時間バックグラウンドで動くプロセスのため、大きな hydrate や
アップロードが回線を食い潰すと同居アプリケーションのレスポンスを劣化させ
ます。[`osvfs.toml`](#設定ファイル) の `bandwidth-up` / `bandwidth-down`
で双方向独立に上限を指定できます。

```toml
bucket         = "my-bucket"
root-folder    = "C:/Users/you/OSVFS"
bandwidth-up   = "5M"       # アップロードを 5 MiB/s に制限
bandwidth-down = "10M"      # ダウンロードを 10 MiB/s に制限
```

書式は rclone の `--bwlimit` に倣っています。サフィックス無しはバイト/秒、
`K` / `M` / `G` はそれぞれ KiB/s / MiB/s / GiB/s を意味します
(`5M` = 5 MiB/s)。キーを指定しないか `0` を指定すると、その方向は無制限
です。アップロードペイロードのストリームとダウンロードレスポンスストリー
ムの両方をトークンバケットで律速するため、`TransferUtility` の multipart
ワーカーもオンデマンド hydrate も同じ上限の下で動きます。

### multipart アップロードのチューニング

`osvfs` は `multipart-threshold` 以上のアップロードを S3 multipart
パスに振り分け、ペイロードを `multipart-part-size` のチャンクに分割
して `TransferUtility` が並列にアップロードします。既定値 (閾値 16 MiB
/ パートサイズ 5 MiB) は AWS SDK v4 の `MinSizeBeforePartUpload` 既定値
に揃えています。次の場面では明示的なチューニングが効きます。

| シナリオ | 推奨設定 | 理由 |
| --- | --- | --- |
| 太い回線 / 大きなファイル中心 | `multipart-threshold = "64M"`、`multipart-part-size = "64M"` | 大きなパートにすればリクエスト単位のオーバーヘッドが薄まり、複数 GiB のファイルでもパート数を抑えられる |
| 小さなファイルが大量 | `multipart-threshold = "16M"` (パートは 5M のまま) | 小さなファイルで multipart のセッション交渉を省き、単一 PUT のほうが速い領域を広げる |
| 不安定な回線 | 既定値 | パートが小さいほどリトライ時の再送量も小さい |

S3 はパートサイズに 3 つの上限を設けています。いずれを破っても
`osvfs` は起動を拒否し、サーバ側も Complete 時にアップロードを失敗
させます。

- `multipart-part-size` は **5 MiB (`5M`) 以上**であること。最終
  パート以外でこれより小さいパートは S3 が拒否します。
- `multipart-part-size` は **5 GiB (`5G`) 以下**であること。これを
  超えるパートサイズは S3 がサポートしません。
- 1 つの multipart アップロードは **最大 10 000 パート**です。した
  がって 1 オブジェクトの最大サイズは `パートサイズ × 10 000` (16 MiB
  パートなら最大 160 GiB、64 MiB パートなら最大 640 GiB) です。
  扱う最大ファイルサイズが収まるよう、十分大きなパートサイズを選んで
  ください。

### リクエスト並列度のチューニング

`osvfs` は方向ごとの S3 同時呼び出し数を上限で抑え、ハイドレーションや
バックグラウンドアップロードのバーストが SDK の HTTP プールやバケット
を圧迫しないようにしています。`osvfs.toml` の独立した 3 つのキーで上限
を制御します。

| キー | 既定値 | 制限対象 |
| --- | --- | --- |
| `max-concurrent-uploads` | `4` | 同時に進行する `UploadAsync` 呼び出し数。1 回の保存で 1 パーミット消費。SDK が内部でマルチパートに分割しても 1 回としてカウントします |
| `max-concurrent-downloads` | `8` | 同時に進行する `ReadRangeAsync` 呼び出し数 (ProjFS のハイドレーション 1 件につき 1 つ) |
| `max-multipart-parts` | `10` | **1 回の `UploadAsync` 内で**並列アップロードするマルチパートのパート数。`TransferUtilityConfig.ConcurrentServiceRequests` に伝播します |

外側のゲート (`max-concurrent-uploads`) と内側のゲート (`max-multipart-parts`)
は直交した制限です。瞬間的に S3 へ同時に飛ぶ部品 PUT 数は最大で
`max-concurrent-uploads × max-multipart-parts` になります。HTTP 接続プ
ールはこの値に余裕を持たせるため
`max(max-concurrent-uploads, max-concurrent-downloads) × 2` に設定し、
接続枯渇がボトルネックにならないようにしています。

| シナリオ | 推奨設定 | 理由 |
| --- | --- | --- |
| 太い回線、複数 GiB のファイル | `max-concurrent-uploads = 2`, `max-multipart-parts = 16` | 同時に走るアップロードは少なくし、各アップロードの内部並列度を上げて単一ファイルのスループットを最大化 |
| 小さなファイル多数 (ビルド成果物・写真など) | `max-concurrent-uploads = 8`, `max-multipart-parts = 4` | 多数の小さな PUT を並列実行。小ファイルではアップロード内の並列化はほぼ無駄 |
| 不安定な上流 / 5xx 多発 | `max-concurrent-uploads = 2`, `max-concurrent-downloads = 4` | バーストを抑え、SDK の Adaptive リトライのトークンバケットに余裕を残す |
| TPS クォータが厳しいバケット | 全項目を半分に | 1 秒あたりのリクエスト数を抑え `RequestLimitExceeded` を回避 |

```toml
bucket                    = "my-bucket"
root-folder               = "C:/Users/you/OSVFS"
max-concurrent-uploads    = 4
max-concurrent-downloads  = 8
max-multipart-parts       = 10
```

3 つのキーはいずれも 1 以上である必要があります。0 や負の値が指定された
場合 OSVFS は起動時に拒否します。

### リトライポリシー

オブジェクトストアの一時的な失敗は AWS SDK のリトライパイプラインに委
ねており、OSVFS は `RetryMode.Adaptive` (標準の指数バックオフに加えて、
サービスが過負荷を返した際に後続リクエストを抑止するクライアントサイド
スロットリング用トークンバケット) と `MaxErrorRetry = retry-max-attempts − 1`
を設定します。リトライ対象のエラー分類は SDK 組み込みのものを利用しま
す。

| エラー | リトライ対象? | 補足 |
| --- | --- | --- |
| HTTP 5xx (`500`, `502`, `503`, `504` …) | はい | サーバ / ロードバランサ側エラー。SDK が一時的とみなす |
| HTTP 408 `Request Timeout` | はい | サーバ側タイムアウト。SDK がバックオフ付きでリトライ |
| `Throttling` / `ThrottlingException` / `RequestThrottled*` / `TooManyRequestsException` / `ProvisionedThroughputExceededException` / `RequestLimitExceeded` / `SlowDown` | はい | AWS のスロットリング系エラー。Adaptive モードは続くリクエストもトークンバケットで抑止 |
| `RequestTimeout` / ネットワークエラー / 接続切断 | はい | ローカルのソケット / コネクションエラー |
| HTTP 4xx (408 を除く `400`, `401`, `403`, `404`, `409`, `412` …) | いいえ | 呼び出し側起因のエラー (不正リクエスト / 権限不足 / 未存在)。即座に伝播 |
| `OperationCanceledException` / `TaskCanceledException` | いいえ | キャンセルはリトライせずに伝播 |

スケジュールは SDK 側が所有しています: `MaxErrorRetry` 回までジッタ付き
の指数バックオフでリトライします。`retry-max-attempts = 1` を指定すると
SDK のリトライは 0 回になり、初回呼び出しのみが実行されます。SDK の
`TransferUtility` はマルチパートアップロードの個別パートを内部でリトラ
イするため、`retry-max-attempts = 3` 設定でも数 GiB のアップロード全体
を再送せずに済みます (失敗したパートだけ最大 3 回まで再送)。

```toml
bucket             = "my-bucket"
root-folder        = "C:/Users/you/OSVFS"
retry-max-attempts = 5         # 試行回数 5 回 (初回 1 + リトライ 4)
```

### 変更検出モード

OSVFS は、他クライアント (AWS マネジメントコンソール、別マシンの
`aws s3 cp`、チームメンバーの作業など) がバケットに加えた変更を検出する
方式を 2 つから選べます。バケット規模、要求するレイテンシ、サーバ側の
設定権限に応じて選択してください。

| モード | レイテンシ | バケット側のセットアップ | 想定用途 |
| --- | --- | --- | --- |
| `polling` (既定) | `sync-interval-seconds` 以下 (既定 30 秒) | 不要。AWS 認証情報がバケットを list できれば動く | 小規模バケットや変更頻度の低いバケット。EventBridge / SQS を追加する権限がない環境 |
| `events` | 数秒 (long-poll の覚醒 + SQS 往復) | バケット → EventBridge → SQS のパイプライン (下記手順) | 再列挙コストが高い大規模バケット、リモート編集を準リアルタイムで反映したい場合 |

`events` には、`Object Created` / `Object Deleted` 通知を EventBridge
経由で受け取る SQS キューが必要です。レガシーの S3 直接 SQS 通知形式
(`Records[]`) は **パースしません** — EventBridge 経由で構成してください。

#### SQS キュー / EventBridge ルール / バケット通知のセットアップ

下の 4 ステップで最小構成のパイプラインを作成します。アカウント ID、
リージョン、バケット名は環境に合わせて置き換えてください。各ステップは
AWS CLI コマンドを示していますが、SQS / EventBridge / S3 のマネジメント
コンソールからも同等の操作が可能です。

1. **SQS キューを作成。**

   ```bash
   aws sqs create-queue --queue-name osvfs-changes \
     --attributes ReceiveMessageWaitTimeSeconds=20
   ```

   キュー側でも long-polling を有効にしておくと空受信が減ります。

2. **EventBridge からの SendMessage を許可する。** プレースホルダーを
   置き換えて `queue-policy.json` として保存します。

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

   キューにアタッチ:

   ```bash
   aws sqs set-queue-attributes \
     --queue-url QUEUE_URL \
     --attributes Policy=file://queue-policy.json
   ```

3. **バケットの EventBridge 通知を有効化** (S3 → 対象バケット → プロパティ
   → "Amazon EventBridge"、もしくは)

   ```bash
   aws s3api put-bucket-notification-configuration \
     --bucket YOUR_BUCKET \
     --notification-configuration '{"EventBridgeConfiguration":{}}'
   ```

4. **キューをターゲットにする EventBridge ルールを作成。** イベント
   パターンを `event-pattern.json` として保存:

   ```json
   {
     "source": ["aws.s3"],
     "detail-type": ["Object Created", "Object Deleted"],
     "detail": { "bucket": { "name": ["YOUR_BUCKET"] } }
   }
   ```

   ルールとターゲットを作成:

   ```bash
   aws events put-rule \
     --name osvfs-bucket-changes \
     --event-pattern file://event-pattern.json

   aws events put-targets \
     --rule osvfs-bucket-changes \
     --targets 'Id=osvfs-sqs,Arn=arn:aws:sqs:REGION:ACCOUNT_ID:osvfs-changes'
   ```

OSVFS が動作する IAM 主体には、対象キューに対して
`sqs:ReceiveMessage`、`sqs:DeleteMessage`、(`event-queue` にキュー名のみを
渡す場合は) `sqs:GetQueueUrl` が必要です。

設定ファイルでマウントをイベント連携に切り替えます:

```toml
bucket        = "my-bucket"
root-folder   = "C:/Users/you/OSVFS"
change-source = "events"
event-queue   = "https://sqs.ap-northeast-1.amazonaws.com/123456789012/osvfs-changes"
```

> 仮想化ルート 1 つにつき 1 キュー。同じキューを複数の `osvfs` インスタンス
> で共有すると、メッセージが消費者間で分配されてしまい、それぞれが半分しか
> 受け取らなくなります。

### オンデマンド同期

`polling` モードには `sync-mode` で切り替え可能な 2 種類の再列挙戦略
があります。

| モード | tick ごとに再列挙する範囲 | API コスト | 適した用途 |
| --- | --- | --- | --- |
| `on-demand` (既定) | ProjFS 経由でユーザーが実際に訪問したディレクトリと、その祖先チェーンのみ | **訪問済みディレクトリ数** に比例 (バケットサイズに非依存) | 既定値。ProjFS の本来のオンデマンド設計と AWS S3 Files の[同期設計](https://docs.aws.amazon.com/AmazonS3/latest/userguide/s3-files-synchronization.html)に揃えた挙動 |
| `full` | バケット全体 (`prefix` 指定時はそのサブツリー) | **総オブジェクト数** に比例 (1000 キーごとに `ListObjectsV2` 1 ページ × tick) | バケット全域での "remote = source of truth" 保証が必要な場合や、十分小さい / 静かなバケットでコストが無視できる場合。Phase&nbsp;1 当初の挙動を保持 |

### 大規模バケット運用時の同期間隔

`osvfs` は外部オブジェクトストアの変更を `sync-interval-seconds`
(既定 `30` 秒) ごとのバケット再列挙で検出します。`sync-mode = "full"` では
各ポーリングが `ListObjectsV2` の全ページ (設定された prefix 以下) を
走査します。`sync-mode = "on-demand"` (既定) では訪問済みディレクトリごとに
`Delimiter='/'` 付きの 1 リクエスト (内部でページング) が走ります。

`full` モードでは S3 の ListObjectsV2 が 1 ページあたり 1000 キーで
打ち切られるため、ポーリング 1 回の所要時間は監視対象プレフィックス配下の
オブジェクト数にほぼ比例して増えます。目安として、近接リージョンの本番
S3 に対して 1 ページの ListObjectsV2 は十数〜数百ミリ秒程度で返るので、
10 万オブジェクトのバケットなら ~100 ラウンドトリップ・数秒分の listing
が tick ごとに発生します。listing 時間が `sync-interval-seconds` に
近づいたら、ポーリングが重ならないように間隔を伸ばすか、`prefix` で
対象範囲を絞ってください。

`on-demand` モードのコストは訪問済みディレクトリ数にスケールするため、
100 ディレクトリ × 各 1 万件のバケットでも、ユーザーが開いたディレクトリ
1 つあたり ~1 件の `ListObjectsV2` 呼び出しに収まります (バケット全体の
1000 キーごとに 1 ページ、ではありません)。

### 読み取り専用マウント

仮想化ルートを「バケットからは読み込むが書き戻しはしない」一方向モード
に切り替えたい場合は、[`osvfs.toml`](#設定ファイル) で `read-only = true`
を指定します:

```toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
read-only   = true
```

`read-only` を有効化したマウントの挙動は以下の通りです:

- 削除 / リネーム / ハードリンク作成 / プレースホルダーから実体ファイル
  への変換 (placeholder-to-full) に対する ProjFS の事前通知はすべて
  `false` を返します。エクスプローラーや他のプロセスからの書き込み試行は
  S3 呼び出しが発生する前にファイルシステム層で失敗します。新規ファイル
  作成 / 上書き / ハンドルクローズ時の修正通知自体は受信しますが、その後の
  アップロード経路は短絡されます。
- オブジェクトストア変更ウォッチャーは無効化されます。`ListObjectsV2`
  のポーリングも SQS の receive も走らず、`.osvfs-lost+found` 隔離
  ディレクトリも作成されません。したがって読み取り専用マウントは
  各ディレクトリを初めて列挙した時点の **凍結スナップショット** に
  なり、マウント中に他クライアントが行ったリモート編集は反映されません。
- ディレクトリの列挙、プレースホルダーの作成、初回オープン時のオン
  デマンド hydrate (ファイル本体のダウンロード) は読み書きモードと
  まったく同じ挙動です。読み取り経路には一切影響ありません。

### 構造化ログ

既定では `osvfs` は人間可読な 1 行形式のテキストログをコンソールに出力
します。[`osvfs.toml`](#設定ファイル) で `log-format = "json"` を指定する
(あるいは一時的に `--log-format json` で起動する) と、Datadog / Loki などの
ログ集約基盤が正規表現なしでパースできる構造化ストリームに切り替わります。

```powershell
osvfs --log-format json    # ファイル設定があっても CLI が一時的に上書きする
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

### ユーザー定義オブジェクトメタデータの往復保持

S3 のオブジェクトはユーザー定義ヘッダー (`x-amz-meta-*`) を任意個持てます。
タグや作者名、アプリ固有のマーカーなどがその例です。OSVFS はこれらの
ヘッダーを hydrate と再アップロードを跨いで保持し、ローカル編集によって
失われないようにします。

プレースホルダー作成時、OSVFS は `HeadObject` でバケット側のユーザー
メタデータを取得し、各エントリを `:osvfs-user-meta` という Windows NTFS の
**代替データストリーム (ADS)** にミラーします。フォーマットは UTF-8 で
1 行あたり `key=value` のプレーンテキストです。キーは S3 の wire と同じく
小文字に正規化されます。

ローカル編集をアップロードする際、OSVFS は同じ ADS を読み戻し、
`PutObject` (またはマルチパート) リクエストに `x-amz-meta-*` として
そのまま添付します。これによりローカル編集サイクル前後でヘッダーが
ビット単位で保たれます。新規作成のローカルファイルにはストリームが
存在しないため、従来どおりユーザーメタデータなしでアップロードされます。

ミラーされたメタデータは PowerShell から確認できます。

```powershell
# プレースホルダーに付与されたストリームを一覧
Get-Item C:\Users\you\OSVFS\meta\file.txt -Stream *

# メタデータを表示 (UTF-8、1 行 1 key=value)
Get-Content C:\Users\you\OSVFS\meta\file.txt -Stream osvfs-user-meta
```

AWS は `x-amz-meta-*` の合計サイズを **1 オブジェクトあたり 2 KiB** に
制限しています。OSVFS は同じ上限でアップロード前に検証を行い、ネットワーク
往復を待たず即座にエラーを返します。S3 が不透明な 400 を返すのを待つ
必要はありません。

### 設定ファイル

マウント設定は TOML 設定ファイルでのみ管理します。最大 3 つのソースを
**優先度の低い順**でマージし、後のソースが先のソースを**キー単位で上書き**
します。

1. **`osvfs.exe` と同階層の `osvfs.toml`** (最低優先度。配布物に同梱する
   ベースライン)。`AppContext.BaseDirectory` で解決するため、カレント
   ディレクトリに依存せず常に exe 隣を見ます。
2. **`%APPDATA%\OSVFS\config.toml`** (ユーザー単位 / マシン共通)。認証情報
   やログ設定など、ユーザー個別の値を置く場所として推奨します。
3. **`--config <path>`** (最高優先度)。標準の保存場所を編集せず、複数の
   設定ファイルを切り替えたい場合に便利。1, 2 と異なり、指定したパスが
   存在しないと**起動時エラー**になります (黙ってフォールバックしない)。

プロセスレベルの CLI フラグ (`--verbose` / `--log-format`) は最終マージ
結果より優先されます。

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
multipart-threshold  = "16M"                     # 任意。既定 16 MiB (AWS SDK v4 既定値)
multipart-part-size  = "16M"                     # 任意。5M〜5G
retry-max-attempts   = 3                         # 任意。1 でリトライ無効
max-concurrent-uploads   = 4                     # 任意。同時 UploadAsync 呼び出し数
max-concurrent-downloads = 8                     # 任意。同時 ReadRangeAsync 呼び出し数
max-multipart-parts      = 10                    # 任意。1 アップロードあたりの並列パート数
log-format           = "text"                    # 任意。"text" または "json"
allow-unversioned    = false                     # DANGER: バージョニング安全チェックをスキップ
verbose              = false
sync-interval-seconds = 30
change-source        = "polling"                 # "polling" | "events"
sync-mode            = "on-demand"               # "on-demand" | "full" — polling 時のみ有効
event-queue          = ""                        # SQS URL/名。events で必須
```

編集用のサンプル [`osvfs.toml.example`](./osvfs.toml.example) をリポジトリ
ルートに同梱しています。`dotnet publish` 時には `osvfs.exe` と同じ階層にも
コピーされるので、`osvfs.toml` (または `%APPDATA%\OSVFS\config.toml`) に
リネームして必要なキーをコメントアウト解除するだけで使えます。

キーはケバブケース (`root-folder`) とスネークケース (`root_folder`) のど
ちらも受け付けます。ケバブケースが推奨です。設定ファイルを置けば、起動は
次のように短く済みます。

```powershell
osvfs                       # オプションはすべて osvfs.toml から取得
```

#### 1 ファイルで複数マウント

`[[mount]]` のテーブル配列構文で 1 ファイルに複数のマウント定義を持たせ
られます。各マウントごとに bucket / root-folder / region / etc を別々
に書けるため、個人用バケットと業務用バケットを 1 つの設定ファイルで管
理できます。プロセスレベルのキー (`verbose` / `log-format`) はファイル
ルートに置き、すべてのマウントに適用されます。

```toml
# ./osvfs.toml — 複数マウント
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

`[[mount]]` エントリには従来の単一マウント形式と同じキー (`verbose` /
`log-format` を除く) が指定可能です。`name` はファイル内で一意である必
要があり、明示しないエントリには `mount[0]` / `mount[1]` … が自動付与さ
れます。1 つのファイル内で `[[mount]]` 配列とルート直下のマウントキー
を混在させると優先順位があいまいになるため、混在は明示的なエラーで拒否
します。

ファイルが 2 つ以上のマウントを宣言している場合、引数なしの `osvfs` は
どのマウントを起動すべきか判断できないので、次のいずれかを使います。

```powershell
osvfs mount-all                 # 全 [[mount]] をこのプロセスで起動
osvfs mount --name personal     # 名前指定で 1 つだけ起動
osvfs mount --name work         # 同じ設定ファイル内の別マウントを起動
```

各マウントは独自の `ProjFsProvider` で動作します。ログは
`OSVFS.Mount.<name>` カテゴリに分類されるので text / JSON フォーマット
のいずれでもマウント名で識別できます。ホストプロセスで Enter を押すと
逐次逆順 (最後に起動したマウントから先に) シャットダウンします。

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

その上でマウント設定にプロファイル名を記述します:

```toml
provider    = "s3"
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
aws-profile = "prod"
```

エントリは generic credential として `OSVFS:AWS:<profile>` という target
name で保存され、`LocalMachine` スコープで永続化されます (ログアウト後も
維持)。一方で blob は保存したユーザーの DPAPI 鍵で暗号化されているため、
**別ユーザーや別マシンにエントリをコピーしても復号できません**。OSVFS の
このストアはあくまで「ユーザー単位のローカルキャッシュ」として扱い、AWS
認証情報のバックアップ用途には使わないでください。

#### AWS IAM Identity Center (SSO) でサインイン

AWS IAM Identity Center (旧 AWS SSO) を利用する環境では、AWS CLI 標準の
`aws configure sso` をそのまま使ってください。`~/.aws/config` に
`sso_session` プロファイルが書き込まれ、ベアラトークンは
`~/.aws/sso/cache/` にキャッシュされ、AWS SDK がリクエスト毎にロール
クレデンシャルを自動でリフレッシュします。OSVFS は SDK 共有プロファイル
チェーン経由でこのプロファイルを拾うため、OSVFS 専用の SSO コマンドは
ありません。

1. **SDK 推奨ウィザードを実行** (`sso-session` ブロックとそれを参照する
   プロファイルが書き込まれます。詳細は
   [SDK ドキュメント](https://docs.aws.amazon.com/sdkref/latest/guide/feature-sso-credentials.html#sso-token-config) 参照):
   ```powershell
   aws configure sso --profile prod
   ```
   ウィザードが start URL / region / アカウント / ロールを対話で聞いた
   うえで、以下のような設定を生成します:
   ```ini
   [sso-session my-org]
   sso_start_url = https://my-org.awsapps.com/start
   sso_region    = us-east-1
   sso_registration_scopes = sso:account:access

   [profile prod]
   sso_session   = my-org
   sso_account_id = 123456789012
   sso_role_name  = ReadOnly
   region         = us-east-1
   ```
2. **ベアラトークンが期限切れになったら再認可** (既定で約 8 時間):
   ```powershell
   aws sso login --sso-session my-org
   ```
3. **`osvfs.toml` から参照**。OSVFS DPAPI ストアに無いプロファイル名は
   SDK 共有プロファイルチェーンへフォールバックして SSO エントリを
   拾います:
   ```toml
   provider    = "s3"
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/OSVFS"
   aws-profile = "prod"
   ```

`osvfs doctor --profile prod` で解決経路 (例: `shared profile 'prod' (sso)`)
を確認できます。SDK チェーンが SSO 経由で配っていることがログから
判別できます。

##### マウント中の自動再認証

SDK が提供する `SSOAWSCredentials` (および `credential_process` /
`AssumeRole` 等の同系統ラッパー) は、リクエスト毎に短期クレデンシャルを
期限切れ前にローテーションします。OSVFS は通常パスでは何もする必要が
ありません。安全網として、SDK のプリエンプトウィンドウをすり抜けて
発生する稀な `ExpiredToken` レスポンス (スリープ復帰や時刻の大きな
ずれなど) は OSVFS が捕捉し、SDK のリフレッシングラッパーに対して
`ClearCredentials()` を呼び出し、リクエストを 1 度だけ再送します。

- **再試行成功**: 新しい有効期限が Information ログに 1 行残るだけで、
  マウントは継続します。
- **再試行失敗** (上流のベアラトークン / リフレッシュトークン自体が
  失効している場合): Windows のバルーン通知 "OSVFS: AWS credentials
  expired" が表示され、`aws sso login` (または `aws login` /
  `osvfs credentials set`) を再実行して再認証するよう促します。失敗
  リクエストの例外はそのまま呼び出し元に伝播するため、エディタ / シェル
  でも検知できます。

#### `aws login` (AWS CLI 2.32+) でサインイン

IAM Identity Center を使わない環境では、AWS CLI v2.32.0 で追加された
`aws login` (OAuth 2.0 + PKCE フロー) でコンソールサインインを最大 12 時間の
自動更新付き一時クレデンシャルに変換できます。OAuth クライアントとエンド
ポイントは AWS CLI 専用に予約されているため、OSVFS は **AWS 公式推奨の
`credential_process` パターンを介して `~/.aws/config` のプロファイルを SDK
共有プロファイルチェーン経由で参照します**。

1. **AWS CLI v2.32.0 以降をインストール** し、IAM プリンシパルに
   [`SignInLocalDevelopmentAccess`](https://docs.aws.amazon.com/signin/latest/userguide/security-iam-awsmanpol.html)
   マネージドポリシーをアタッチ。
2. **CLI でサインイン**。`login_session` プロファイルが書き込まれ、リフレッシュ
   トークンは `%USERPROFILE%\.aws\login\cache` にキャッシュされます:
   ```powershell
   aws login --profile signin
   ```
3. **`~/.aws/config` に `credential_process` プロファイルを追加**。任意の
   AWS SDK が消費できる形にします (将来的に SDK が `login_session` を
   ネイティブサポートする可能性はありますが、現時点では credential_process
   が確実です):
   ```ini
   [profile signin]
   login_session = arn:aws:iam::123456789012:user/you
   region = us-east-1

   [profile osvfs-login]
   credential_process = aws configure export-credentials --profile signin --format process
   region = us-east-1
   ```
4. **`osvfs.toml` から参照**。指定した名前が OSVFS DPAPI ストアにない場合、
   OSVFS は SDK 共有プロファイルチェーンにフォールバックして
   `credential_process` エントリを拾います:
   ```toml
   provider    = "s3"
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/OSVFS"
   aws-profile = "osvfs-login"
   ```

`osvfs doctor --profile osvfs-login` は解決経路 (例:
`shared profile 'osvfs-login' (credential_process)`) を表示するため、SDK
デフォルトチェーンではなく共有ファイルから取得できているかを確認できます。

## トラブルシューティング

マウントが起動しない (`StartVirtualizing failed`、"bucket not found"、
"AccessDenied"、認証情報の有効期限切れ など) ときは **まず
`osvfs doctor` を実行してください**。doctor は読み取り専用の環境
セルフチェックを順番に実行し、色付きのサマリーを出力します。

```powershell
# osvfs.toml の最初の [[mount]] からバケット / リージョン / プロファイルを引用
osvfs doctor

# CLI で完全に上書き (まだ設定ファイルがないとき向け)
osvfs doctor --bucket my-bucket --region ap-northeast-1 --profile prod

# LocalStack / MinIO 風
osvfs doctor --bucket my-bucket --endpoint-url http://localhost:4566
```

doctor が確認する項目は以下の順です。

1. **Windows ProjFS 機能 (`Client-ProjFS`)** — PrjFlt minifilter が
   登録されているかと、ユーザーモード DLL `ProjectedFSLib.dll` の
   存在を確認。`Get-WindowsOptionalFeature -FeatureName Client-ProjFS`
   と同等の判定です。
2. **`StartVirtualizing` スモークテスト** — 一時ディレクトリを作成し
   仮想化ルート化、`StartVirtualizing` を試行してから片付けます。
   レジストリ確認では拾えない「機能はインストール済みだが PrjFlt
   サービスが停止」「EDR / アンチウイルスがブロック」といった失敗を
   検出できます。
3. **AWS 認証情報の解決** — OSVFS プロファイル (`--profile`) または
   SDK チェーンから資格情報を解決し、ソース・アクセスキーの末尾 4
   桁・一時資格情報 (セッショントークン保持) かどうかを表示します。
4. **バケット到達性 (`HeadBucket`)** — `GetBucketLocation` を呼び
   出します。403 はリスト権限不足、404 はリージョン違いの可能性が
   高いです。
5. **バケットバージョニング** — OSVFS の衝突解決に必須です。
   未設定 / Suspended の場合は、有効化のための
   `aws s3api put-bucket-versioning` コマンドをそのまま提示します。

各行は先頭に `[OK]` / `[!!]` / `[XX]` / `[--]` (skipped) のマーカーが
付きます。プロセスは全項目が PASS なら **0**、いずれかが要対処なら
**2** で終了するため、起動スクリプトの先頭にも組み込めます。

```powershell
osvfs doctor --bucket $env:OSVFS_BUCKET; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
osvfs mount-all
```

`NO_COLOR=1` の指定や stdout のリダイレクトでは ANSI 制御コードを
省略するため、ログ収集や CI のスクレイピングでも素のテキストとして
読み込めます。

### 隔離された退避ファイルの確認・復元 (`lost-and-found`)

リモート側の変更が同期されていないローカル編集と衝突したとき、ウォッ
チャーは「リモート側が真」というポリシーに従ってローカルファイルを上書
きしますが、上書き直前にダーティなローカルコピーをマウント直下の
`.osvfs-lost+found` ディレクトリへ退避します。`osvfs lost-and-found`
サブコマンドを使うと、シェルだけでこれらの退避ファイルを確認し復元
できます。

```powershell
# 退避ファイル一覧 (新しい順)。元のパスとサイズも表示されます
osvfs lost-and-found list

# osvfs.toml に複数マウントがある場合は --name で 1 つを選択
osvfs lost-and-found list --name docs

# 退避ファイルと現在のリモートオブジェクトを diff
# テキスト: 外部 `git diff --no-index --color`
# バイナリ (先頭 8 KiB に NUL バイトあり): SHA-256 とサイズの比較
osvfs lost-and-found diff 20260510T123456789Z_docs%2Fnotes.md

# 退避ファイルを任意の場所にコピー
# (--target を省略するとカレントディレクトリへ元ファイル名で保存)
osvfs lost-and-found restore 20260510T123456789Z_docs%2Fnotes.md `
  --target C:\Users\you\Desktop\notes-recovered.md
```

`list` の 1 列目 (`FILENAME`) が `diff` / `restore` で渡す識別子です。
そのままコピー＆ペーストしてください。ファイル名は `<UTC タイムスタン
プ>_<URL エスケープ済みの元パス>` 形式なので、`list` は併せて復号後の
`ORIGINAL-PATH` も表示します。`restore` は既存ファイルを上書きしないた
め、強制上書きしたい場合は `--force` を追加してください。`diff` はテ
キスト比較に `git` を使用するため、`PATH` に `git` が無い場合はバイナ
リ用のサマリ表示にフォールバックします。

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
