## 2024-05-15 - [ProtocolParser: Zero-Allocation Spans]
**Learning:** In .NET 8, `List<byte>` can be treated as a `Span<byte>` via `CollectionsMarshal.AsSpan`, allowing for zero-allocation slicing and vectorized `IndexOf` searches. This is significantly faster than `GetRange(...).ToArray()` which creates a new heap allocation and copy for every message.
**Action:** Use `Span<T>` and `CollectionsMarshal.AsSpan` for high-frequency byte processing and parsing loops.

## 2024-05-16 - [AudioManager: COM/Process Disposal & Device Caching]
**Learning:** `MMDevice` and `AudioSessionControl` (NAudio) are wrappers around COM objects and must be explicitly disposed to avoid resource leaks in the Windows Audio service. `System.Diagnostics.Process` also leaks handles if not disposed. Additionally, `EnumerateAudioEndPoints` is an O(N) operation; caching device IDs with a TTL (e.g., 30s) drastically reduces the cost of frequent volume updates.
**Action:** Always use `using` blocks for COM-wrapped objects and `Process` handles. Implement caching for expensive system resource lookups in hot paths.
