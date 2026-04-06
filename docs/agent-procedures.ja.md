# Agent Procedures

この文書は AI / Coding Agent 向けの手順を置きます。設計説明ではなく、作業時の判断基準だけを残します。

## 変更前

1. まず関連コードとテストを読む
2. コードとテストを設計の一次資料として扱う
3. 文書変更が必要かは、コードから読み取れない情報があるかで判断する

## 振る舞いを変えるとき

1. 先にテストの影響範囲を確認する
2. 仕様変更なら、文書よりコードとテストの更新を優先する
3. 文書は必須要件、運用前提、判断理由だけを補足する

## Resonite 連携を触るとき

1. 型名やメンバー名を推測しない
2. 必要な値は live の Resonite Link から確認する
3. WSL から live 検証するときは、`cmd.exe /c dotnet.exe run ...` や `pwsh.exe` ラッパなど host 側実行を優先する
4. `stream` で live の send/remove 順序を確認する
5. ポートを手入力するより、`tools/Invoke-ResoniteLinkCommand.ps1` の Resonite Link 自動検知を優先する
6. 自動検知の根拠は Unity Editor の挙動推測ではなく、Resonite Unity SDK と `YellowDogMan.ResoniteLink` の実装である:
   `LinkSessionListener` は UDP `12512` を bind し、JSON の `ResoniteLinkSession` announcement を受け取り、告知された `linkPort` を使う
7. 単発確認の前に、まず `pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" discover` を実行する
8. session が 1 件だけなら、`repl` / `send-json` / `benchmark-send` / `cleanup-slot` では `-Port` を省略し、script 側の自動解決に任せる
9. session が複数ある場合は、固定ポートをメモへ残すのではなく `-SessionId` か `-SessionName` で選ぶ
10. raw JSON だけで足りない場合は、`tools/Invoke-ResoniteLinkCommand.ps1 repl ...` 経由で公式 ResoniteLink REPL を使って live 確認とメンバー確認を行う
11. アプリ本体の entry point がまだ `--resonite-port` を要求する場合も、実行直前に discover して、その値をその場限りの入力として扱う
12. 実機確認ができない場合は、その前提を差分説明に明記する

## ドキュメント更新ルール

1. `README.md` は短く保つ
2. `AGENTS.md` は不変で汎用的な最小セットだけにする
3. `docs/` は最新情報と手順だけにする
4. 設計レベルの説明は新設しない

## 検証

- 通常は `dotnet test ThreeDTilesLink.slnx`
- child node 向けの local IPC が禁止される制限付き agent sandbox では、`dotnet build ThreeDTilesLink.slnx --no-restore -m:1` を使う
- 同じ環境では solution レベルの `dotnet test` が VSTest の socket 通信を必要とするため、`dotnet test tests/ThreeDTilesLink.Tests/ThreeDTilesLink.Tests.csproj --no-build` を優先する
- 変更範囲が限定される場合は、まず関連テストを優先する
- 実運用依存の確認が未実施なら、その不足を明示して終える
