# 3DTilesLink

Google Photorealistic 3D Tiles を指定地点周辺だけ取得し、Resonite Link 経由で非永続に可視化する .NET CLI ツールです。

人間向けの入口はこの `README.md` に置き、最新の運用情報や AI 向け手順は `docs/` に分離します。

## 前提

- `.NET SDK 10.0+`
- Google Map Tiles API の認証
  - `GOOGLE_MAPS_API_KEY` を使う
  - または ADC を使う (`gcloud auth application-default login` / `GOOGLE_APPLICATION_CREDENTIALS`)
- Resonite で Resonite Link を有効化し、接続先ポートを確認する

CLI 起動時は `.env` を上位ディレクトリ探索付きで自動ロードし、既存の環境変数は上書きしません。

## ビルド

```bash
dotnet build ThreeDTilesLink.slnx
```

## テスト

```bash
dotnet test ThreeDTilesLink.slnx
```

## 使い方

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

- `--dry-run` を付けると Resonite 送信なしで取得・変換経路だけを検証できます。
- `GOOGLE_MAPS_API_KEY` があれば API キーを使い、未設定なら ADC を使います。
- `--height-offset-m` を省略した場合は `0` を使います。

ADC を使う例:

```bash
gcloud auth application-default login
dotnet run --project src/ThreeDTilesLink.Cli -- \
  --lat 35.65858 \
  --lon 139.745433 \
  --half-width-m 400 \
  --link-host 127.0.0.1 \
  --link-port 12000 \
  --dry-run
```

## ドキュメント

- `AGENTS.md`: Coding Agent 向けの最小ガイド
- `docs/current-state.md`: 現時点の運用情報と制約
- `docs/agent-procedures.md`: AI 向けの作業手順
