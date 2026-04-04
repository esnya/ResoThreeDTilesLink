# 3DTilesLink

Google Photorealistic 3D Tiles を指定地点周辺だけ取得し、Resonite Link 経由で非永続に可視化する .NET CLI ツールです。
単発 CLI (`ThreeDTilesLink.Cli`) と、Probe 監視で常駐実行するインタラクティブ版 (`ThreeDTilesLink.Interactive`) を含みます。

## Scope

- 対象領域は基準点中心の正方形断面・高度無限角柱
- tile 採否は `boundingVolume(region/box/sphere)` と角柱断面の交差で判定
- tile `transform` を階層合成し、`Geographic -> ECEF -> ENU -> EUN` で変換
- `content` が `.json` の場合は nested tileset を再帰追跡して最詳細まで探索
- GLB はそのまま読み出し、メッシュとして逐次送信
- Resonite 側は実送信用 `WebSocket` クライアントを自前実装し、`ResoniteLink` はメッセージ定義のみ利用
- 送信時はセッション親 Slot 配下に tile Slot を作成し、`MeshCollider` と `PBS_Metallic(Smoothness=0)` を付与
- セッション親 Slot に `License` + `DynamicVariableSpace(Google3DTiles)` + `DynamicField<string>(Google3DTiles/License)` を付与
- `CreditString` は表示中 GLB タイルの `asset.copyright` を集計し、権利者を `;` 区切り・出現頻度の高い順で反映（親タイル削除時は対応 attribution を減算）
- 非永続利用前提（永続保存/アセット化を前提にしない）

## UV / Texture Convention

- 3D Tiles の GLB (glTF 2.0) は UV 原点 `(0,0)` を画像左上として扱う
- Resonite 送信前に `V` を `1 - V` へ変換し、座標系差分を吸収する
- テクスチャ画像バイトは反転しない（画像変換は行わない）

## Prerequisites

- .NET SDK 10.0+
- 認証方式をどちらか用意
  - `api-key` モード: `GOOGLE_MAPS_API_KEY` を環境変数に設定
  - `gcloud` モード(ADC): `gcloud auth application-default login` 実施済み、または `GOOGLE_APPLICATION_CREDENTIALS` を設定
- Resonite で Resonite Link を有効化し、接続先ポートを確認

## .env

CLI 起動時に `.env` を自動ロードします（上位ディレクトリ探索あり）。既存の環境変数は上書きしません。

## Build

```bash
dotnet build ThreeDTilesLink.slnx
```

## Resonite 型名ポリシー

- Resonite コンポーネント型名は推測フォールバックしない
- 型名やジェネリック表記が必要な場合は、実行中の ResoniteLink へ直接問い合わせて実在値を確認してから固定する
- WSL 環境で確認する際は必要に応じて `cmd.exe` 経由でホスト側から実行する

## Test

```bash
dotnet test ThreeDTilesLink.slnx
```

## Run

```bash
dotnet run --project src/ThreeDTilesLink.Cli -- \
  --lat 35.65858 \
  --lon 139.745433 \
  --height-offset-m 20 \
  --half-width-m 400 \
  --link-host 127.0.0.1 \
  --link-port 12000 \
  --max-tiles 1024 \
  --max-depth 16 \
  --timeout-sec 120 \
  --log-level Information
```

認証は自動判定です。`GOOGLE_MAPS_API_KEY` があれば API キーを使用し、未設定なら ADC を使用します。

`--dry-run` を付けると Resonite 送信なしで取得・選別・変換経路のみ検証します。

`--height-offset-m` は省略可能です。未指定時は `0` (楕円体高 0m) を使用します。

`RESONITE_LINK_HOST_HEADER` を設定すると WebSocket 接続時の `Host` ヘッダを上書きできます。

`gcloud` 認証を使う場合:

```bash
gcloud auth application-default login
dotnet run --project src/ThreeDTilesLink.Cli -- \
  --lat 35.65858 \
  --lon 139.745433 \
  --height-offset-m 20 \
  --half-width-m 400 \
  --link-host 127.0.0.1 \
  --link-port 12000 \
  --dry-run
```

## Run (Interactive / Resident)

接続時に Probe スロットを作成し、`World/3DTilesLink/Probe/{Latitude|Longitude|Range}` の `DynamicValueVariable<T>` を監視します。  
値変更は `debounce/throttle` で平滑化し、再実行時は旧タスクをキャンセルして旧ランのスロットを削除します。

```bash
dotnet run --project src/ThreeDTilesLink.Interactive -- \
  --lat 35.65858 \
  --lon 139.745433 \
  --half-width-m 400 \
  --height-offset-m 20 \
  --link-host 127.0.0.1 \
  --link-port 12000 \
  --poll-ms 250 \
  --debounce-ms 800 \
  --throttle-ms 3000 \
  --probe-path-prefix World/3DTilesLink/Probe \
  --probe-slot-name "3DTilesLink Probe"
```
