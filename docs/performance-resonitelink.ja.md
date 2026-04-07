# Resonite Link Performance Notes

この文書には、このプロジェクトで Resonite Link 側のパフォーマンスがどういうときに改善しやすく、どういうときに悪化しやすいかという現時点の知識を置く。

ここに書く内容は記述時点の live 計測に基づくもので、普遍的な規則ではない。Resonite Link、payload の形、ローカル環境の変化に応じて変わりうる。

## 改善しやすいこと

- 接続数を盲目的に増やすより、mesh placement あたりの往復回数を減らす方が安定して効きやすい
- 3D Tiles の fetch や traversal から独立に send throughput を見ると、transport 側のボトルネックが見えやすい
- 順序も性能の一部として扱うと、見かけ上の false win を避けやすい。throughput が上がっても required replacement や removal が遅れるなら有効な改善ではない
- 計測を opt-in に保つと、計測不要時の hot path 性能を守りやすい

## 効きにくいこと

- send worker 数を増やせば単調に速くなるわけではない。ある帯域を超えると追加 lane が効かなくなることがある
- 累積 send time だけを見るのは不十分。remove の実行時間が短くても、見た目の順序では remove が遅すぎることがある
- transport だけを最適化しても、実ボトルネックが fetch や traversal に戻った時点で効果は止まりやすい

## 悪化しやすいこと

- 有効帯を超えて send lane を増やすと、throughput が改善せずむしろ悪化しやすい
- remove や replacement を許可する前に send で全 lane を埋める writer 設計は、raw send throughput が良く見えても visible な順序崩れを起こしやすい
- timer や listener などの常時有効な計測を hot path に載せると、通常 run にノイズとオーバーヘッドを持ち込みやすい
