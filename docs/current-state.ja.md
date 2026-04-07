# Current State

この文書には、コードやテストだけでは伝わりにくい現時点の運用情報だけを置きます。

## 運用上の前提

- このプロジェクトは Google Photorealistic 3D Tiles を Resonite Link へ非永続に流し込む用途を前提にする
- 永続保存、アセット化、設計資料の保守は目的にしない
- live 検証で観測した Google 3D Tiles のレスポンスは、query string の `session` だけでなく `datasets/.../files/...` のパス自体も session ごとに変わるため、run をまたぐ file cache の再利用は実質的に起きない
- そのため、Google 3D Tiles 向けの永続 file cache は、レスポンスヘッダ上は cacheable でも、無駄な複雑さとして運用目標にしない
- タイル取得の HTTP キャッシュは、ヘッダ準拠の process-local reuse に限定し、user ごとの disk 永続化は前提にしない
- 正式リリースのバージョン起点は `v1.2.3` 形式の `git tag` に統一する
- タグなしコミットのビルドはプレリリース扱いにし、正式版とは区別する
- 認証は `GOOGLE_MAPS_API_KEY` の API キーを使う
- 機能ごとの必要 API は次の通り
- `stream` のタイル取得: Google Map Tiles API
- `interactive` の `Latitude` / `Longitude` / `Range` ベースのタイル取得: Google Map Tiles API
- `interactive` の自由検索 (`World/... .Search`): Google Geocoding API
- タイル取得は ADC fallback を使わない。常に `GOOGLE_MAPS_API_KEY` を使う
- `interactive` の自由検索は `GOOGLE_MAPS_API_KEY` が必要で、ADC では扱わない
- アプリは `.env` を上位ディレクトリ探索付きで自動ロードし、既存の環境変数は上書きしない
- tileset の表示ラベルは、slot 名の可読性を優先して compact な 16 進数にしている
- 同一階層に 16 個を超える sibling がある場合、表示ラベルは 16 進数で折り返し、parser は warning を出す。ただし内部の stable path は一意性を保つ

## 環境変数

- `GOOGLE_MAPS_API_KEY`: Google Map Tiles API と Google Geocoding API を API キーで使うときに設定する
- `THREEDTILESLINK_DUMP_MESH_JSON`: メッシュ送信内容の JSON ダンプが必要なときに使う

## Resonite 連携の扱い

- Resonite のコンポーネント型名やメンバー名は推測で埋めない
- 実在値が必要な場合は、実行中の Resonite Link に問い合わせて確認してから固定する
- live 確認とメンバー確認には公式 ResoniteLink REPL を使う
- このリポジトリからは `tools/Invoke-ResoniteLinkCommand.ps1 repl ...` 経由で起動する
- WSL から運用する場合、確認コマンドは Windows host 側の `pwsh.exe` で実行し、Linux 側 `pwsh` 前提にはしない
- live 環境によっては `SimpleAvatarProtection` が公開されていないことがある。その場合でも接続とメッシュ送信は継続できる前提で扱う
- Session root や親スロットに付ける常設の書き込み元は、まず session-side の `DynamicValueVariable<T>` として置く
- `DynamicVariableSpace.OnlyDirectBinding` が有効な場合、session-side の source DV 名には `SpaceName/` を明示して含める
- `World/` alias は `DynamicField` ではなく、session-side の値を `ValueCopy<T>` で Drive する別の `DynamicValueVariable<T>` として公開する
- Target 側からの上書きは `ValueCopy.WriteBack` で制御し、`World/` から session-side へ戻す必要がある Interactive 入力パラメーターにだけ有効化する
- Progress は親スロットに置いた session-side の `DynamicValueVariable<float>` から `ValueCopy<float>` 経由で `World/ThreeDTilesLink.Progress` へ `0.0..1.0` の float として公開する
- 人間向けの進捗文字列は親スロットに置いた session-side の `DynamicValueVariable<string>` から `ValueCopy<string>` 経由で `World/ThreeDTilesLink.ProgressText` へ公開する

## WSL からの単発確認

- WSL 側に Linux 版 `pwsh` がなくても、Windows 側 `pwsh.exe` を WSL から呼び、host 側で実行する
- 単発確認は `tools/Invoke-ResoniteLinkCommand.ps1` 経由で行う。primary は `send-json`
- interactive な確認が必要な場合は `repl` で公式 ResoniteLink REPL 実装を使う
- `dotnet` が `pwsh.exe` の `PATH` に見えない環境があるため、スクリプト側で `dotnet.exe` の既定パスも探す
- Linux `dotnet` と Windows `dotnet.exe` を同じ checkout で混在させる前提があるため、`obj` はホスト OS ごとに分離して扱う
- NuGet の restore メタデータにはホスト依存パスが入るため、`obj` を共有させる運用に戻さない
- CI や検証環境で正しいバージョンを計算するには `git tag` 履歴が必要で、浅い checkout のままにしない
- raw JSON 送信は `tools/ResoniteRawJson` を使う
- Resonite Unity SDK は `YellowDogMan.ResoniteLink` の `LinkSessionListener` を使って自動検知する。これは UDP `12512` を bind し、JSON の `ResoniteLinkSession` announcement を受け取り、告知された `linkPort` を使う仕組み
- このリポジトリの `tools/Invoke-ResoniteLinkCommand.ps1` も同じ仕組みに寄せているため、port を script に書き込むより `discover` または `-Port` 省略を優先する
- WSL からは `pwsh.exe -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" <command> ...` の形で host 側実行に寄せる
- 例で使うポート番号はその時点の live な Resonite Link に合わせる。固定値として扱わない
- 複数 session が見つかった場合は、`-SessionId` または `-SessionName` で対象を選ぶ
- Git worktree から検証する場合は、その worktree にも `.env` を置く。アプリは現在の working tree から親ディレクトリ探索で `.env` を読むため、main checkout 側の `.env` は自動では拾われない
- WSL から `send-json` の `-JsonFile` を使う場合は Windows パスを渡す。`/tmp/...` のような Linux パスは host 側 `dotnet.exe` から読めない
- worktree 配下で host 実行すると、MinVer が project directory を Git working directory と見なせず warning を出すことがある。バージョン計算自体を検証対象にしていない限り、この warning は非 blocking として扱う
- live のメッシュ送信や remove 挙動の確認は、`stream` や `interactive` などアプリ本体の entry point を優先する。`send-json` は主に接続確認と焦点を絞った message 確認に使う
- WSL から live 検証のために `dotnet run` を叩く場合は、Linux 側 `dotnet` より `cmd.exe /c dotnet.exe run ...` や host 側 PowerShell ラッパを優先する
- Resonite Link のポート番号は文書や script に固定値として保存しない。毎回その場の live 値として扱う

## Live 検証の観点

- `stream` の確認では、東京タワー付近の小さい範囲を優先する。例: `--latitude 35.65858 --longitude 139.745433 --range 60`
- `stream` では、粗いタイルから細かいタイルへ収束する過程で、可視範囲が維持されるかを見る
- 要求した `range` が大きい場合、細かい descendant より先に粗い coverage ancestor が送られることがある。この順序は bootstrap の意図した挙動であり、それだけで退行とはみなさない
- `stream` のログでは `Streamed tile ...` と `Removed tile ...` の順序を重点確認する。可視範囲を保つために必要な追加より remove が先行してはいけない
- 単発の `stream` でも refinement 中に `Removed tile ...` は出る。remove は `interactive` だけの現象だと決めつけない

例:

```bash
pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" discover

pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" repl

pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" send-json \
  -Json '{"$type":"requestSessionData"}'

pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" send-json \
  -JsonFile "$(wslpath -w /tmp/get-slot-root.json)"
```

- `send-json` は任意の ResoniteLink JSON をそのまま 1 件送り、`sourceMessageId` が一致するレスポンスを待って整形表示する
- `messageId` が無ければ送信前に自動付与する
- live で使う `$type` は README の古い例と差異がありうるため、必要なら実際のレスポンスか SDK 実装で確認する
- `repl` は公式 ResoniteLink REPL controller を起動し、接続確認、スロット移動、コンポーネント確認、メンバー確認の標準手段として使う
- メッシュ送信の確認は、アプリケーション本体の経路か対象を絞った JSON / message 検証で行う

## この文書に書いてよいもの

- 明示しておくべき必須要件
- 実運用で必要な前提
- コードだけでは判断しにくい最新の制約

## この文書に書かないもの

- クラス構成や処理フローの設計説明
- コードとテストを読めばわかる仕様の焼き直し
