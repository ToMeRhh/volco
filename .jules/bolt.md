# Bolt's Performance Journal ⚡

This journal contains critical performance-related learnings for the Volco codebase.

## 2026-04-21 - [SliderProcessor Hot Path Optimization]
**Learning:** The `SliderProcessor`'s 20ms poll was a source of redundant work. Specifically, processing "unmapped" sliders triggered a full scan of the configuration to find mapped processes and a subsequent filter of active processes. In cases where multiple sliders were unmapped, this work was repeated multiple times per tick.
**Action:** Use proactive caching for configuration-derived data (like mapped processes) and per-tick caching for expensive transient computations (like the unmapped process list) in high-frequency loops.
