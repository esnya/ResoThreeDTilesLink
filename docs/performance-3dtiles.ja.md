# 3D Tiles Performance Notes

この文書には、このプロジェクトで 3D Tiles 側のパフォーマンスがどういうときに改善しやすく、どういうときに悪化しやすいかという現時点の知識を置く。

ここに書く内容は記述時点の live 計測に基づくもので、普遍的な規則ではない。上流サービス、payload、実装の変化に応じて変わりうる。

## 改善しやすいこと

- network fetch と decode、placement を分けて見ると、本当のボトルネックが見えやすい。支配的な stage を下げた改善だけが実効的になりやすい
- traversal / discovery が支配的な間だけ、それらの throughput を上げる効果が出やすい。decode や send が支配的になった後は、並列化を増やしても queue が増えやすい
- decode が支配的な間だけ、content decode throughput を上げる効果が出やすい。fetch latency が支配的な状態では、decode worker を増やしても遊びやすい
- 同じ run 内で同じ tileset JSON を引き直す場合は、process-local の HTTP reuse が効くことがある

## 効きにくいこと

- このプロジェクトの Google Photorealistic 3D Tiles では、レスポンス URL や `datasets/.../files/...` パスが session ごとに変わる観測があるため、run をまたぐ永続 file cache は通常効きにくい
- `range` を下げる調整は、同じ仕事を速くするというより、要求する仕事自体を減らしていることが多い
- ある stage だけを単独で強化しても、別の stage が支配的になった時点で効果は頭打ちになりやすい

## 悪化しやすいこと

- session-scoped な tile URL に対して永続 cache を前提にした複雑さを足すと、安定した速度向上がないまま保守コストだけ増えやすい
- 飽和後も discovery や decode の並列度を上げ続けると、wall-clock time 改善より memory pressure や in-flight backlog 増加が起きやすい
- 品質低下を性能改善とみなすと、本当のボトルネックが見えにくくなり、その後の調整判断も悪化しやすい
