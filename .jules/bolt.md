## 2024-05-15 - [ProtocolParser: Zero-Allocation Spans]
**Learning:** In .NET 8, `List<byte>` can be treated as a `Span<byte>` via `CollectionsMarshal.AsSpan`, allowing for zero-allocation slicing and vectorized `IndexOf` searches. This is significantly faster than `GetRange(...).ToArray()` which creates a new heap allocation and copy for every message.
**Action:** Use `Span<T>` and `CollectionsMarshal.AsSpan` for high-frequency byte processing and parsing loops.

## 2026-06-22 - [AppLog: Zero-Allocation Debug Logging]
**Learning:** For `static` logging methods, `[InterpolatedStringHandlerArgument("")]` cannot be used as there is no `this` receiver. Instead, the handler's constructor should directly check the static configuration flag (e.g., `AppLog.IsDebugEnabled`) and set the `out bool isEnabled` parameter. This allows the compiler to skip evaluating expensive interpolation arguments (like `string.Join` or complex object conversions) when logging is disabled.
**Action:** Implement `InterpolatedStringHandler` for all high-frequency logging paths to eliminate overhead when suppressed.
