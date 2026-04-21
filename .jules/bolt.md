# Bolt's Performance Journal ⚡

This journal contains critical performance-related learnings for the Volco codebase.

## 2026-04-21 - [SliderProcessor Hot Path Optimization]
**Learning:** The `SliderProcessor`'s 20ms poll was identified as a source of redundant work, but a proposed optimization to cache unmapped processes was rejected.
**Action:** Re-evaluate bottlenecks based on actual scale. O(N*M) is often fine if N and M are small (<20). Don't add complexity (events, locking) for micro-gains in non-critical paths.

## 2026-04-21 - [ProtocolParser Allocation Reduction]
**Learning:** `ProtocolParser` is a better target for optimization as it processes raw byte streams and currently uses `List.GetRange(...).ToArray()` which allocates on every valid message.
**Action:** Use `Span<byte>` and `CollectionsMarshal.AsSpan` to achieve zero-allocation parsing where possible.
