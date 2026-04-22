## 2024-05-15 - [ProtocolParser: Zero-Allocation Spans]
**Learning:** In .NET 8, `List<byte>` can be treated as a `Span<byte>` via `CollectionsMarshal.AsSpan`, allowing for zero-allocation slicing and vectorized `IndexOf` searches. This is significantly faster than `GetRange(...).ToArray()` which creates a new heap allocation and copy for every message.
**Action:** Use `Span<T>` and `CollectionsMarshal.AsSpan` for high-frequency byte processing and parsing loops.

## 2024-05-20 - [AudioManager: COM Disposal and Device Caching]
**Learning:** NAudio's `MMDevice` and `AudioSessionControl` objects are wrappers around COM objects and must be explicitly disposed to avoid resource leaks. Additionally, `EnumerateAudioEndPoints` is an O(N) COM operation; caching device IDs and using `GetDevice(id)` is significantly faster for high-frequency updates.
**Action:** Always wrap `MMDevice` and `AudioSessionControl` in `using` blocks. Cache device IDs by name to avoid repeated endpoint enumerations.
