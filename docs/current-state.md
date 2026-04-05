# Current State

この文書には、コードやテストだけでは伝わりにくい現時点の運用情報だけを置きます。

## 運用上の前提

- このプロジェクトは Google Photorealistic 3D Tiles を Resonite Link へ非永続に流し込む用途を前提にする
- 永続保存、アセット化、設計資料の保守は目的にしない
- 認証は `GOOGLE_MAPS_API_KEY` があれば API キーを優先し、なければ ADC を使う
- 機能ごとの必要 API は次の通り
- CLI のタイル取得: Google Map Tiles API
- Interactive の `Latitude` / `Longitude` / `Range` ベースのタイル取得: Google Map Tiles API
- Interactive の自由検索 (`World/... .Search`): Google Geocoding API
- Interactive の自由検索は `GOOGLE_MAPS_API_KEY` が必要で、ADC では扱わない
- CLI は `.env` を上位ディレクトリ探索付きで自動ロードし、既存の環境変数は上書きしない

## 環境変数

- `GOOGLE_MAPS_API_KEY`: Google Map Tiles API と Google Geocoding API を API キーで使うときに設定する
- `GOOGLE_APPLICATION_CREDENTIALS`: ADC の明示パス指定に使う
- `THREEDTILESLINK_DUMP_MESH_JSON`: メッシュ送信内容の JSON ダンプが必要なときに使う

## Resonite 連携の扱い

- Resonite のコンポーネント型名やメンバー名は推測で埋めない
- 実在値が必要な場合は、実行中の Resonite Link に問い合わせて確認してから固定する
- 確認には `tools/ResoniteInspect` と `tools/ResoniteProbe` を使う
- WSL からの確認で制約がある場合は、必要に応じてホスト側コマンド実行も使う
- live 環境によっては `SimpleAvatarProtection` が公開されていないことがある。その場合でも接続とメッシュ送信は継続できる前提で扱う
- DV を追加するときは、対象の親スロットに専用 `DynamicVariableSpace` を置く構成を基本にする
- 外から観測させる値は、上記とは別に `World/` プレフィックス付き `DynamicField` でも公開する二本立てを基本にする
- Progress は親スロットに付けた `DynamicField` から `World/ThreeDTilesLink.Progress` へ `0.0..1.0` の float で公開する
- 人間向けの進捗文字列は親スロットに付けた `DynamicField` から `World/ThreeDTilesLink.ProgressText` へ公開する

## WSL からの単発確認

- WSL 側に Linux 版 `pwsh` がなくても、Windows 側 `pwsh.exe` を WSL から呼べる
- 単発確認は `tools/Invoke-ResoniteLinkCommand.ps1` 経由で行う。primary は `send-json`
- `dotnet` が `pwsh.exe` の `PATH` に見えない環境があるため、スクリプト側で `dotnet.exe` の既定パスも探す
- Linux `dotnet` と Windows `dotnet.exe` を同じ checkout で混在させる前提があるため、`obj` はホスト OS ごとに分離して扱う
- NuGet の restore メタデータにはホスト依存パスが入るため、`obj` を共有させる運用に戻さない
- raw JSON 送信は `tools/ResoniteRawJson` を使う
- WSL から `pwsh.exe -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" send-json localhost <port> -JsonFile <windows-path>` の形で呼ぶ
- 例で使うポート番号はその時点の live な Resonite Link に合わせる。固定値として扱わない

例:

```bash
pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" send-json localhost 49379 \
  -Json '{"$type":"requestSessionData"}'

pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" send-json localhost 49379 \
  -JsonFile "$(wslpath -w /tmp/get-slot-root.json)"

pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" inspect localhost 49379
pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" probe localhost 49379
```

- `send-json` は任意の ResoniteLink JSON をそのまま 1 件送り、`sourceMessageId` が一致するレスポンスを待って整形表示する
- `messageId` が無ければ送信前に自動付与する
- live で使う `$type` は README の古い例と差異がありうるため、必要なら実際のレスポンスか SDK 実装で確認する
- `inspect` は接続確認と定義確認向け
- `probe` は三角形メッシュを 2 件送って描画経路を確認するときに使う
- `probe` は `src/ThreeDTilesLink.Core` のビルドが通ることを前提にする。接続確認だけなら先に `inspect` を使う

## この文書に書いてよいもの

- 明示しておくべき必須要件
- 実運用で必要な前提
- コードだけでは判断しにくい最新の制約

## この文書に書かないもの

- クラス構成や処理フローの設計説明
- コードとテストを読めばわかる仕様の焼き直し
