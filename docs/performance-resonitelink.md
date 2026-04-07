# Resonite Link Performance Notes

This document records current knowledge about what tends to improve or regress Resonite Link performance in this project.

These notes are based on live measurements at the time they were written. They are not universal rules and may change as Resonite Link, payload shape, or the local environment change.

## What Usually Helps

- Reducing the number of round trips per mesh placement helps more reliably than blindly increasing connection count.
- Measuring send throughput independently from 3D Tiles fetch and traversal makes transport-side bottlenecks visible.
- Treating ordering as part of performance helps avoid false wins. A change is useful only if it improves throughput without delaying required replacement or removal too much.
- Keeping instrumentation opt-in helps preserve hot-path performance when measurement is not needed.

## What Usually Does Not Help

- Increasing send-worker count is not monotonic. After a certain band, additional lanes may stop helping.
- Looking only at cumulative send time is insufficient. A system can report low remove execution time while still applying removals too late in user-visible order.
- Optimizing transport in isolation stops helping once the actual bottleneck moves back to fetch or traversal.

## What Usually Makes It Worse

- Pushing parallel send lanes past the useful band can reduce throughput rather than improving it.
- A writer design that fills all lanes with sends before allowing removal or replacement tends to create visible ordering regressions even if raw send throughput looks good.
- Always-on measurement, timers, or listeners on the hot path can add noise and overhead in normal runs without improving behavior.
