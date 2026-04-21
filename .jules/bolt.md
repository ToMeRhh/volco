## 2024-05-15 - [ProtocolParser: Zero-Allocation Spans]
**Learning:** In .NET 8, `List<byte>` can be treated as a `Span<byte>` via `CollectionsMarshal.AsSpan`, allowing for zero-allocation slicing and vectorized `IndexOf` searches. This is significantly faster than `GetRange(...).ToArray()` which creates a new heap allocation and copy for every message.
**Action:** Use `Span<T>` and `CollectionsMarshal.AsSpan` for high-frequency byte processing and parsing loops.
