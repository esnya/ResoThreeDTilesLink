# 3D Tiles Performance Notes

This document records current knowledge about what tends to improve or regress 3D Tiles performance in this project.

These notes are based on live measurements at the time they were written. They are not universal rules and may change as upstream services, payloads, or the implementation change.

## What Usually Helps

- Separating network fetch cost from decode and placement cost makes the real bottleneck visible. Improvements are usually real only when they reduce the dominant stage rather than just shifting time between stages.
- Raising traversal or discovery throughput helps only while traversal is the dominant stage. Once decode or send becomes dominant, more traversal parallelism mostly increases queueing.
- Raising content decode throughput helps only while decode is the dominant stage. Once fetch latency dominates, more decode workers mostly sit idle.
- Process-local HTTP reuse can still help within a single run when the same tileset JSON is revisited.

## What Usually Does Not Help

- Persistent cross-run file caching for Google Photorealistic 3D Tiles is usually ineffective here because observed response URLs and `datasets/.../files/...` paths are session-scoped.
- Tuning by shrinking `range` or `tile-limit` improves runtime mainly by reducing requested work, not by making the same workload more efficient.
- Increasing one stage in isolation often stops helping once another stage becomes dominant.

## What Usually Makes It Worse

- Adding permanent caching complexity for session-scoped tile URLs tends to increase maintenance cost without producing stable speed gains.
- Overdriving discovery or decode parallelism after saturation tends to increase memory pressure and in-flight backlog rather than improving wall-clock time.
- Treating quality reduction as a performance win hides the actual bottleneck and makes later tuning harder to reason about.
