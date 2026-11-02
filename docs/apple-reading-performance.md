# Apple reading performance audit

Measured on 2026-11-02 with optimized Swift 6.4.2 builds on the local Apple Silicon
Mac. Fixtures were synthetic; no live host, installed profile, or account was used.

## Changes

The saved manual order was searched twice inside each sort comparison. A shuffled
300-session fixture averaged **4.78 ms per sort** with that comparator and
**0.70 ms** after building an ID-to-rank map once, across 100 iterations of each.
Both versions produced identical IDs. Pins still take precedence, recent/machine
ordering still falls back to manual order, missing IDs still sort by ID, and the
first saved occurrence wins if preferences contain a duplicate.

Markdown parsing previously ran in the view initializer, including when a parent
updated without changing its text. Parsing mixed heading/list/table/code fixtures
cost approximately **1.6 ms for 16 KiB** and **4.3 ms for 64 KiB** per invocation.
Parsing now lives behind an equatable child so unchanged parent input can skip
that work. Equality compares exact UTF-8 bytes: Unicode canonical equivalence must
not reuse a different URL target. The document's byte, line, nesting and table
limits, native rendering, and link-scheme allowlist are unchanged. No global cache
retains text across sessions or accounts.

These are CPU microbenchmarks, not measured frame-rate or Instruments results.
The sorting fixture used 300 `SessionEntry` values, IDs `session-0000` through
`session-0299`, saved ascending, and input permutation `(index * 137) % 300`, with
empty pins and manual ordering. Each implementation was warmed and its output
checked before timing 100 calls with `ContinuousClock` in a release test build.

## Other paths checked

- At the host's 20-delegate list limit, three descendant traversals per row cost
  about 0.46 ms in the same optimized harness. No graph rewrite was introduced.
- Decoding 50 tool rows four times cost about 0.17 ms. No additional retained
  tool-state cache was introduced.
- Search, delegate grouping/events/labels, and refresh scheduling were inspected.
  Their security bounds, attribution checks, single in-flight loader, and refresh
  intervals were retained.

Behavior tests cover pin/manual priority, duplicate and unknown IDs, recent ties,
and natural machine-name sorting. Existing Markdown safety/rendering tests and
the unsigned native macOS and iOS Simulator builds cover the rendering change.
