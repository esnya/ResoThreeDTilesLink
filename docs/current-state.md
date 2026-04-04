# Current State

この文書には、コードやテストだけでは伝わりにくい現時点の運用情報だけを置きます。

## 運用上の前提

- このプロジェクトは Google Photorealistic 3D Tiles を Resonite Link へ非永続に流し込む用途を前提にする
- 永続保存、アセット化、設計資料の保守は目的にしない
- 認証は `GOOGLE_MAPS_API_KEY` があれば API キーを優先し、なければ ADC を使う
- CLI は `.env` を上位ディレクトリ探索付きで自動ロードし、既存の環境変数は上書きしない

## 環境変数

- `GOOGLE_MAPS_API_KEY`: Google Map Tiles API を API キーで使うときに設定する
- `GOOGLE_APPLICATION_CREDENTIALS`: ADC の明示パス指定に使う
- `RESONITE_LINK_HOST_HEADER`: WebSocket 接続時の `Host` ヘッダを上書きしたいときに使う
- `THREEDTILESLINK_DUMP_MESH_JSON`: メッシュ送信内容の JSON ダンプが必要なときに使う

## Resonite 連携の扱い

- Resonite のコンポーネント型名やメンバー名は推測で埋めない
- 実在値が必要な場合は、実行中の Resonite Link に問い合わせて確認してから固定する
- 確認には `tools/ResoniteInspect` と `tools/ResoniteProbe` を使う
- WSL からの確認で制約がある場合は、必要に応じてホスト側コマンド実行も使う

## この文書に書いてよいもの

- 明示しておくべき必須要件
- 実運用で必要な前提
- コードだけでは判断しにくい最新の制約

## この文書に書かないもの

- クラス構成や処理フローの設計説明
- コードとテストを読めばわかる仕様の焼き直し
