# Controller Config, Stage 4b: App Config Session Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The app side of the config session, in `Volco.Core` with no UI:
- `ControllerConfigService` reads the controller's config (§5.3) and saves one (§5.4), with retries, implied OK and confirmation.
- The app keeps one mirror of the controller's config bytes (schema v5), and the baud setting is removed.
- A pure banner function (§8.3) says what the Controller page (stage 5) will show after each Hello, and the service runs the one automatic read it calls for.
- The web simulator stores a config and answers reads and writes (§10).

**Out of scope (stage 5):** every UI piece (Controller page, banner rendering, sidebar dot), test mode, and the editing model.

**Ruling (recorded in the spec):** hardware checklist items 6, 8 and 13 move from stage 4 to stage 5, because they need a UI to start a save from Volco. Their firmware halves were proven in stage 4a with `config_tool.py` over serial (write → reboot → new CRC; failures keep the old config). In 4b the app half is proven against the simulator (a node test) and by the service tests.

**Architecture:**
- `Volco.Core/Controller/ControllerConfigService.cs` (owned by `VolcoRuntime`):
  - It talks to the session through a small interface, `IConfigSessionLink { bool SendRaw(byte[] burst); ConnectionType ConnectionType { get; } }`, which `VolcoRuntime` implements over `ControllerLink.SendRaw`.
  - Each operation is one async loop that reads events (ConfigData, ConfigResult, Hello, connection replaced) from a `Channel`. Timeouts use `CancellationTokenSource(TimeSpan, TimeProvider)`.
  - The `TimeProvider` comes from `VolcoRuntimeOptions.Time` (default `TimeProvider.System`); tests use `FakeTimeProvider`.
- `Volco.Core/Controller/ControllerBanner.cs` is a pure `Compute(BannerInputs) → ControllerBanner`.
- The mirror is `AppConfig.ControllerConfigBlob` (base64, `string?`).
- Simulator: `tools/simulator/config.js` holds the config session logic (receiver rules, read reply, validation subset). `index.html` uses it in the browser, and `tools/simulator/test/config.test.mjs` uses it under Node with protobufjs from npm. CI gets a `simulator-test` job.

**Tech Stack:** .NET 8 (CI only), xUnit 2, `Microsoft.Extensions.TimeProvider.Testing` 8.10.0 (tests only), Node 22 + protobufjs 7.x (simulator test only; the browser loads the same major from jsDelivr).

**Spec:** `docs/superpowers/specs/2026-10-03-controller-config-design.md` §5.3, §5.4, §8.1–§8.3, §10, §11, §12.3, §15 stage 4.

## Global Constraints

- `/usr/bin/git`; no local `dotnet` (C# runs in CI on push); Write tool for files; Node runs locally.
- **Every `TaskCompletionSource` uses `RunContinuationsAsynchronously`.** The event channel is created with `AllowSynchronousContinuations = false`, so no continuation runs on a transport's receive thread.
- **Events** (`BannerChanged`, `MirrorChanged`, `OperationChanged`) are raised outside every lock, on any thread, and never after the runtime is disposed.
- **Existing runtime tests must not change behaviour.** The 3-argument `HelloMessage` constructor now gives `ConfigState.None`. A test Hello therefore lands in the "Set up your controller" row, which starts no automatic read and sends nothing extra.
- **Timeouts** (§5.3, §5.4) are named constants in `ControllerConfigService.Timeouts`: read 2 s per wait, 3 read attempts; result 3 s, 3 write attempts; confirming 10 s, or 20 s on UDP.

## Review Focus

1. **One save, one outcome.**
   - Implied OK confirms the save in any phase.
   - Leftovers from an earlier attempt (an older `seq`) are ignored.
   - A connection replacement fails the save with the mirror unchanged; a plain Detach/Attach doesn't fail it.
   - A second request while one is running is rejected.
2. **The mirror changes only on a confirmed save or an adopted read,** and is written through `RequestSave()`. Nothing else writes `ControllerConfigBlob`.
3. **Repeated identical Hellos give one automatic read and an unchanged banner.** A failed automatic read is not retried until the user asks, or until a different Hello arrives.
4. **Thread safety:** messages arrive on transport threads, timeouts fire on timer threads, and callers await on the UI thread. Shared state lives under one lock, and events are raised outside it.
5. **The banner's version check comes before every configState row.** An undecodable mirror counts as absent.

---

### Task 1: Schema v5 (mirror field, baud removed); fixed serial baud; `HelloMessage` default state

**Files:**
- `src/Volco.Core/Config/AppConfig.cs`: `CurrentSchemaVersion = 5`; `SerialConfig.BaudRate` removed; `public string? ControllerConfigBlob { get; set; }` with doc "base64 of the controller's config bytes exactly as stored (spec §8.2); written only by `ControllerConfigService`".
- `src/Volco.Core/Config/ConfigManager.cs`: the `version == 3` branch becomes `version is 3 or 4`, backing up to `config.v{version}.bak`. Every loaded JSON (any version) has `connection.serial.baudRate` removed before deserializing.
- `src/Volco.Core/Communication/SerialConnection.cs`: `public const int BaudRate = 115200;`, used in `new SerialPort(...)`.
- `src/Volco.Core/Communication/ControllerMessage.cs`: the 3-argument `HelloMessage` constructor gives `ControllerConfigState.None`.
- `src/Volco.App/ViewModels/ConnectionViewModel.cs` and `src/Volco.App/Views/Pages/ConnectionPage.axaml`: remove `BaudRate`, `BaudRateIndex`, `BaudCombo` and its label row. The serial summary becomes just `ComPort`.
- Tests: `ConfigManagerTests`, `ConfigMigratorTests:370`, `SerialConnectionTests`, `SettingsCoverageTests:66/76`, `ConnectionPageTests:103-114`, and any App test asserting the "COM · 115,200" summary.

- [ ] **Step 1 (RED, C#):** In `ConfigManagerTests`:
  - `Load_V4_MigratesToV5_WritesBackup_RemovesBaudRate`: v4 JSON with `"baudRate": 9600` → `config.v4.bak` equals the original, the rewritten file has `schemaVersion` 5 and no `baudRate`, and `ControllerConfigBlob` is null.
  - `Load_V5_RoundTripsControllerConfigBlob`.
  - `Load_V3_GoesStraightToV5`: backup `config.v3.bak`.

  Remove baud from the test setups. Push; CI fails to compile (`ControllerConfigBlob` is missing).
- [ ] **Step 2 (GREEN):** Implement. Grep `BaudRate` in `src` and `tests`: only `SerialConnection.BaudRate` remains. Grep `"COM3 · 115` and `N0` in App tests and fix the summaries. Push; CI green. Commit `feat(config): schema v5 - controller config mirror, baud setting removed (spec 8.1, 8.2)`.

### Task 2: Runtime plumbing (`SendRaw`, routing, replacement, `TimeProvider`)

**Files:** `src/Volco.Core/Runtime/{ControllerLink.cs,VolcoRuntime.cs,VolcoRuntimeOptions.cs}`, `src/Volco.Core/Controller/IConfigSessionLink.cs`, a stub `ControllerConfigService` (constructor, `OnMessage`, `OnConnectionReplaced`, `Dispose`), `tests/Volco.Core.Tests/Runtime/{ControllerLinkTests.cs,VolcoRuntimeTests.cs}`, `tests/Volco.Core.Tests/Volco.Core.Tests.csproj` (+ `Microsoft.Extensions.TimeProvider.Testing` 8.10.0).

**Interfaces:**
- `ControllerLink.SendRaw(byte[] burst) → bool`:
  - false when no connection is attached or the link is disposed
  - otherwise one outbox item (one `Send`), drained before return for a single-threaded caller
  - needs only Attach, not Ready
- `VolcoRuntimeOptions.Time` (`TimeProvider`, default `TimeProvider.System`).
- `public ControllerConfigService ControllerConfig { get; }` on `VolcoRuntime`, created in the constructor with `(ConfigManager, IConfigSessionLink, TimeProvider)`.
- **Routing in `OnMessageReceived`:** `ConfigDataMessage` and `ConfigResultMessage` from the current connection go to `ControllerConfig.OnMessage`. A Hello from the current connection goes to `_link.OnMessage` first, then to `ControllerConfig.OnMessage`. Every Hello goes to the service, version-only ones too.
- **`OnConnectionReplaced()`** is called where a connection is retired in `StartConnection` (both the `old` and the `displaced` paths) and in `Dispose`. Status-driven Detach/Attach do not call it.

- [ ] **Step 1 (RED):**
  - **`ControllerLinkTests`:**
    - `SendRaw_WithoutConnection_ReturnsFalse`
    - `SendRaw_AttachedButAwaitingHello_SendsOneFrameAsIs`: the burst is exactly one `SentFrames` entry
    - `SendRaw_AfterDispose_ReturnsFalse`
  - **`VolcoRuntimeTests`:**
    - `ConfigMessages_FromARetiredConnection_AreDropped`
    - `Reconnect_TellsTheConfigServiceTheConnectionWasReplaced`, observed through a save that fails with `ConnectionLost`. Write it in Task 4 and mark it here as a placeholder.
    - `Hello_ReachesTheConfigService_AfterTheLink`: after `ConnectAndGreet`, `runtime.ControllerConfig.LastHello` is that Hello.
- [ ] **Step 2 (GREEN):** Implement. CI green. Commit `feat(runtime): config session plumbing - SendRaw, routing, replacement, TimeProvider (spec 8.2)`.

### Task 3: `ControllerConfigService.ReadAsync` (§5.3)

**Interfaces** (`Volco.Core.Controller`):
```csharp
public enum ConfigOperation { Idle, Reading, Writing }
public enum ReadStatus { Ok, Failed, Cancelled, Busy }
public sealed record ConfigReadResult(ReadStatus Status, byte[]? Blob);   // Blob: empty array when the controller has none
public Task<ConfigReadResult> ReadAsync(bool adopt, CancellationToken ct = default);
public HelloMessage? LastHello { get; }
public ConfigOperation Operation { get; }
public event EventHandler? OperationChanged;
public event EventHandler? MirrorChanged;
```

- **The operation:** each attempt takes the next `seq` (a u8 counter shared with writes, wrapping) and calls `SendRaw(EncodeConfigRead(seq))`. It assembles ConfigData by the 5 acceptance rules of §5.3. Frames with another `seq` are ignored.
- **A failed attempt:** a gap, a disagreeing total or CRC, a size or CRC mismatch, no frame within 2 s of the send or of the last accepted chunk, or `SendRaw` false followed by the 2 s wait. Up to 3 attempts, then `Failed`.
- **A Hello during the read:** equal (configState, configCrc) to `LastHello` when the read started → ignored. Different → `Cancelled`.
- **Adopting:** `adopt` and `Ok` with a non-empty blob → mirror = base64(blob), `RequestSave()`, raise `MirrorChanged`. An empty blob is never adopted.
- **Busy:** a second request while one runs → `Busy` at once.
- **Replacement:** `Cancelled`.

- [ ] **Step 1 (RED):** `tests/Volco.Core.Tests/Controller/ControllerConfigServiceReadTests.cs`, with a `FakeSessionLink` that records bursts and a `FakeTimeProvider`. Cases, each from §12.3:
  - single-chunk read assembles
  - three-chunk read (a 600-byte blob) assembles
  - stale `seq` frames are ignored
  - a gap → a new attempt with a new `seq`
  - a CRC mismatch → a new attempt
  - three failures → `Failed`, and exactly 3 ConfigRead bursts were sent
  - an empty config (total 0) → `Ok` with an empty blob, and the mirror is untouched even with adopt
  - adopt writes the mirror and raises `MirrorChanged` once
  - a Hello equal to the starting one is ignored; a different one → `Cancelled`
  - a second ReadAsync while busy → `Busy`
  - replacement → `Cancelled`
  - the 2 s timeout restarts on each accepted chunk (advance 1.5 s, chunk, 1.5 s, last chunk → `Ok`)
- [ ] **Step 2 (GREEN):** Implement the event loop and the read operation. CI green. Commit `feat(controller): ControllerConfigService read with retries (spec 5.3)`.

### Task 4: `ControllerConfigService.SaveAsync` (§5.4)

**Interfaces:**
```csharp
public enum SaveStatus { Confirmed, Rejected, NotRunningNewSetup, DidNotComeBack, NoAnswer, ConnectionLost, Busy, Cancelled }
public sealed record ConfigSaveResult(SaveStatus Status, ConfigResultCode? Code = null, byte Item = 0xFF, byte Field = 0xFF);
public Task<ConfigSaveResult> SaveAsync(byte[] blob, CancellationToken ct = default);
public SavePhase? Phase { get; }   // Sending(attempt 1..3) | WaitingForImpliedOk | Confirming
```

- `Rejected` carries TOO_LARGE, DECODE_FAILED, INVALID or STORAGE_FAILED with `item` and `field`.
- `ct` is honoured only before the first burst is enqueued; after that, cancellation is ignored.
- Every rule of the §5.4 "App write rules" list, in that order, including the final implied-OK wait after the 3rd unanswered attempt.
- On `Confirmed`: mirror = base64(blob), `RequestSave()`, `MirrorChanged`.

- [ ] **Step 1 (RED):** `ControllerConfigServiceSaveTests.cs`, one test per §12.3 save bullet:
  - OK + confirming Hello → `Confirmed`, mirror updated
  - OK + Hello with the old CRC → `NotRunningNewSetup`, mirror unchanged
  - OK + no Hello → `DidNotComeBack` after 10 s on serial and 20 s on UDP (two tests; advance 9.9 s and assert still pending)
  - BAD_CRC, then OK → two bursts with different `seq`s; a late result with attempt 1's `seq` is ignored
  - OK lost, then a new-CRC Hello → `Confirmed`
  - an unexpected Hello before the result → next attempt
  - INVALID → `Rejected` (Invalid, item, field) after exactly one burst
  - no result ×3, then nothing for the confirming timeout → `NoAnswer`
  - no result ×3, then a new-CRC Hello within the confirming timeout → `Confirmed`
  - `SendRaw` false → counted as unanswered
  - replacement mid-save → `ConnectionLost`, mirror unchanged
  - second request while busy → `Busy`
  - pre-cancelled `ct` → `Cancelled`, nothing sent; cancelling after the burst does not stop the save
  - the burst equals `ProtocolEncoder.EncodeConfigWrite(seq, blob)`

  In `VolcoRuntimeTests`:
  - **Detach and re-Attach of the same fake connection while confirming** → still `Confirmed`
  - `Reconnect()` mid-save → `ConnectionLost`
- [ ] **Step 2 (GREEN):** Implement. CI green. Commit `feat(controller): ControllerConfigService save - retries, implied OK, confirmation (spec 5.4)`.

### Task 5: Banner function and automatic read (§8.3)

**Interfaces:**
```csharp
public enum BannerKind { None, Incompatible, Operation, DifferentSetup, ReadFailed, NotSetUpHasMirror, NotSetUp, Unusable, BrokenWithFirmware, Reading }
[Flags] public enum BannerActions { None = 0, UseControllers = 1, ApplyMine = 2, Retry = 4, SetUpFromScratch = 8, ApplySaved = 16, SetUp = 32, FixIt = 64, Update = 128 }
public sealed record ControllerBanner(BannerKind Kind, BannerActions Actions, bool FirmwareUpdateAvailable, bool DevelopmentFirmware);
public sealed record BannerInputs(HelloMessage? Hello, byte[]? Mirror, ConfigOperation Operation, bool ReadFailed,
                                  ConnectionType ConnectionType, string? BundledFirmwareVersion);
public static class ControllerBannerLogic { public static ControllerBanner Compute(BannerInputs i); }
// service:
public ControllerBanner Banner { get; }
public event EventHandler? BannerChanged;
public Task<ConfigReadResult> RetryReadAsync();   // the Retry action: a read that adopts
```

- **Messages are not in Core.** The UI maps `BannerKind` to the §8.3 strings in stage 5. Core only exposes the kinds, and the spec text is quoted in their XML docs.
- **The mirror's decode check** (`ControllerConfigCodec.Decode` throws → treated as absent) happens inside `Compute`.
- **No Hello yet** → `None`.
- **Operation running:** a write shows `Operation`; a read shows `Reading`.
- **`BundledFirmwareVersion`** is null until stage 6 (no catalog), so `FirmwareUpdateAvailable` is false in the app. The tests pass a value to cover every condition.
- **`DevelopmentFirmware`** is true when the Hello's firmware is `"dev"`.
- **Automatic read:** after each Hello, the service recomputes the banner. If the row is "configState 1, no mirror", no operation runs, and this (configState, configCrc) hasn't had an automatic read yet, it starts `ReadAsync(adopt: true)`, fire-and-forget with its exceptions logged. A failure sets `ReadFailed` for that Hello key. A different Hello clears `ReadFailed` and the key.
- **Recompute triggers:** Hello, `MirrorChanged`, operation changes, read failure. `BannerChanged` is raised only when the value differs (record equality).

- [ ] **Step 1 (RED):** `ControllerBannerTests.cs`:
  - one test per §8.3 table row (8 rows, plus the failed-read variants)
  - the version check before the configState rows
  - an undecodable mirror counts as absent
  - firmware notice: true only with Serial + Esp32 + catalog + different version + not dev; false for WebSocket (the simulator), and on a version match
  - `Update` is offered on a version-only Hello over Serial with a catalog, even though `Chip` is Unknown, and not over WebSocket

  In `ControllerConfigServiceReadTests`:
  - `RepeatedIdenticalHellos_OneAutomaticRead_UnchangedBanner` (3 identical Hellos → 1 ConfigRead burst, `BannerChanged` raised once per real change)
  - `FailedAutomaticRead_NotRetriedForTheSameHello_RetriedForADifferentOne`
  - `RetryReadAsync_Adopts`
- [ ] **Step 2 (GREEN):** Implement. CI green. Commit `feat(controller): banner logic and the automatic read (spec 8.3)`.

### Task 6: Simulator config support (§10) with a Node test and a CI job

**Files:** create `tools/simulator/config.js`, `tools/simulator/test/{package.json,package-lock.json,config.test.mjs}`; modify `tools/simulator/index.html`, `tools/simulator/README.md`, `.github/workflows/build.yml` (new job `simulator-test`: checkout, setup-node 22, `npm ci` and `node --test` in `tools/simulator/test`).

**`config.js`** (no DOM; `globalThis.VolcoSimConfig` in the browser, `export` via a tiny ESM wrapper for Node, or a CommonJS check):
- `createSession({ protobuf, root, crc32, now, send, reboot, store })`: `onFrame(payload, now)` handles 0x87–0x8A, and `tick(now)`.
  - The receiver rules are §5.4 1–7 with the 2000 ms timeout. One transport, so rule 2 doesn't apply.
  - **Validation:** decode with protobufjs (`ControllerConfig.decode`) inside a try → DECODE_FAILED; then the §6.1 limits and NUL checks on strings; then counts (sliders ≤ 16, buttons ≤ 32) and step 3's screen rules (bus 0–3, address 0x3C/0x3D, bus has sda/scl) → INVALID with the same item/field codes as `volco_config.h`. Pins are not checked.
  - **On OK:** `store(blob)`, `send(ConfigResult OK)`, then `reboot()`.
  - **Read reply:** the stored blob in 240-byte ConfigData frames. The browser has one WebSocket, so it sends each frame.
- `hello(type)` gives the Hello payload: chip 1, firmware `"sim"`, configState and CRC from the stored blob. The sliders, buttons and kinds come from the stored config (a button with a led → 1, a led pair → 2, a screen → 3, none → 0), or else from the selected controller type.
- The protobuf schema is embedded as a JSON descriptor generated from `proto/volco_config.proto` by `tools/simulator/test/gen_descriptor.mjs` (`protobufjs-cli`'s `pbjs -t json`; committed as `tools/simulator/volco_config.json` and checked for staleness in CI), so neither the browser nor Node parses `.proto` at runtime.

**`index.html`:**
- loads protobufjs 7 from jsDelivr (`protobufjs@7/dist/light/protobuf.min.js`), `volco_config.json` and `config.js`
- `handle()` routes 0x87–0x8A to the session
- the reboot drops the WebSocket for 1 s, then reconnects (which sends Hello)
- the config is kept in `localStorage` under `volco-sim-config` (base64, try/catch)
- a **Clear config** button
- the status line shows "config: none" or "config: N bytes, crc XXXXXXXX"

- [ ] **Step 1 (RED):** `config.test.mjs` (Node's built-in test runner), which reads `firmware/test/golden/frames.txt` and `config.txt`:
  - the `config_write_*_seq9` burst (`32 02 08 64` is a valid `screens { brightness: 100 }` config) → result OK, `store` called with those bytes, `reboot` called
  - a following ConfigRead seq 7 → one ConfigData frame whose payload has total 4, offset 0, CRC `FA E4 14 11` and the bytes
  - every `config.txt` `decode-fail` case → DECODE_FAILED
  - every `validate` case whose rule is a count or a screen rule → the same triple; pin-rule cases → OK
  - BAD_SEQUENCE, BAD_CRC and TOO_LARGE cases as in stage 2's receiver tests (a subset)
  - the 2000 ms timeout → no result
  - Hello: no config → the selected type's hardware with state 0; after the DIY blob → 5 sliders, kinds [1,1,2], state 1, CRC of the blob

  Run `cd tools/simulator/test && npm ci && node --test`: fails (no `config.js`).
- [ ] **Step 2 (GREEN):** Implement `config.js`, the descriptor and its generator, and the `index.html` wiring. Node tests pass. Add the CI job; push; CI green. Commit `feat(simulator): stores a config and answers reads and writes (spec 10)`.
- [ ] **Step 3 [USER-RUN, optional]:** Open the simulator and connect Volco over WebSocket: the status shows "config: none"; the Hello is still accepted, and slider and button input still works. Saving a config from Volco needs stage 5's page.

### Task 7: Spec ruling, docs, CI

- [ ] **Spec §15 stage 4 row:** "Done when" becomes "Goldens, service and banner tests pass; the simulator Node test round-trips a config; a write is confirmed on hardware with `config_tool.py` (4a)". Stage 5's row gains items 6, 8 and 13.
- [ ] **CLAUDE.md:**
  - the Config section (schema v5: `controllerConfigBlob`, baud removed)
  - `Volco.Core` `Controller/` (`ControllerConfigService`, `ControllerBannerLogic`)
  - the Web Simulator paragraph (config support, Node test)
  - "The app side isn't wired in yet" → "The app side runs the session (`ControllerConfigService`); the Controller page is stage 5"
- [ ] **`tools/simulator/README.md`:** config support, Clear config, the Node test.
- [ ] **Final:** push; CI green (all jobs, including `simulator-test`).
