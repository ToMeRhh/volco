# Volco Audio Core Redesign — Shared Foundation

**Date:** 2026-09-26
**Status:** Implemented (`src/Volco.Windows/Audio`).

> The audio layer is where the CPU goes, and its redesign is the same whatever the
> UI framework.

---

## 1. Why: the diagnosis

The app is described as "laggy and heavy, the OSD is flaky, and it takes more GPU
and CPU than anticipated." Reading the code, the dominant cost is not the UI
framework — it is that the audio layer re-activates Core Audio COM objects on
every volume tick.

`SliderProcessor` (`src/Volco/Input/SliderProcessor.cs:53`) flushes pending slider
values on a **20 ms repeating timer** — up to 50 dispatches/second, across 5
sliders. Each dispatch lands in `AudioManager`, and each of the three hot paths
(`SetProcessVolume`, `SetProcessVolumes`, `SetDeviceVolume`) begins with:

```csharp
var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
```

That returns a **brand-new `MMDevice` COM object every time**, and it is never
disposed. What follows from that, per tick:

| Path | Hidden cost per tick |
|---|---|
| `device.AudioEndpointVolume` | NAudio lazily constructs `AudioEndpointVolume`, whose constructor calls **`RegisterControlChangeNotify`**. One callback registration leaked per tick, released only when the finalizer eventually runs. |
| `device.AudioSessionManager` | NAudio lazily constructs `AudioSessionManager`, whose constructor calls **`RefreshSessions()`** → `GetSessionEnumerator()` + a new `AudioSessionControl` COM wrapper **per audio session** + **`RegisterSessionNotification`**. A full session enumeration up to 50×/second, each leaking a session-notification registration. |
| `GetProcessName(session)` | `Process.GetProcessById` per session (PID-keyed, 5 s TTL — `AudioManager.cs:258`). |

Note the subtlety, because it is easy to misread: NAudio's `Sessions` *property*
is a cheap cached read. The expense is that a **fresh `MMDevice` is created every
tick**, so the lazily-built manager behind it is fresh every tick too, and *its
constructor* does the enumeration and the registration.

On top of that:

- `SessionMonitor` (`src/Volco/Audio/SessionMonitor.cs`) polls every **2000 ms**
  and calls `GetActiveProcessNames()`, repeating the whole pattern again.
- All of this runs on `System.Threading.Timer` thread-pool threads with **no COM
  apartment discipline**, so Core Audio objects are marshalled from an arbitrary
  thread on each tick.
- `AudioManager.GetOutputDevices()` / `GetInputDevices()` enumerate endpoints and
  are called from the settings UI (partially hoisted in commit `c7b63e5`, still
  uncached).

### 1.1 Reliability defects in the same layer

These are the "flaky" symptoms, and they are all consequences of the same design:

1. **Devices are keyed by friendly-name substring.** `FindDeviceByName`
   (`AudioManager.cs:228`) matches with `.Contains(name, OrdinalIgnoreCase)` and
   caches the resolved `MMDevice` **forever**. Config stores names
   (`OutputDevicesConfig.Device1/Device2`, `SliderConfig.DeviceName`), so a device
   rename, a driver update, or two devices sharing a substring breaks the mapping
   silently.
2. **Active-device reporting is a coin flip.** `App.xaml.cs:PushControllerState`
   decides the index with `currentDevice.Contains(devices.Device1) ? 0 : 1` —
   anything that is not device 1 is reported to the controller as device 2,
   lighting the wrong LED.
3. **No `IMMNotificationClient`.** Device add / remove / state change / default
   change are invisible to the app. The `catch (COMException) →
   InvalidateDeviceCache → retry` blocks are a band-aid for exactly this gap.
4. **No `IAudioSessionNotification`.** New audio sessions are only noticed on the
   2 s poll, and a newly launched app **never receives the slider's current
   level** — it plays at whatever volume Windows gives it until the user nudges
   the slider.
5. **Per-process volume only reaches the default device.** Every session path
   enumerates `GetDefaultAudioEndpoint(...).AudioSessionManager.Sessions`. An app
   playing to the *other* configured headset is silently not controlled.
6. **Default-device switching shells out to a bundled third-party binary.**
   `SetDefaultOutputDevice` (`AudioManager.cs:130`) runs
   `Tools/svcl.exe /SetDefault "<name>" all` with a 5 s `WaitForExit`. This is the
   "hacky solution" to remove. (The README claims COM `IPolicyConfig` — the README
   is wrong; fix it as part of the work.)

---

## 2. Target architecture

One component, `AudioEngine`, replacing `AudioManager` + `SessionMonitor`. It is
**stateful and event-driven**: it builds a registry of live COM handles once, keeps
it current via Core Audio notifications, and never enumerates on a hot path.

```
                    ┌──────────────────────────────────────────────┐
                    │ AudioEngine (owns one dedicated audio thread)│
                    │  CoInitializeEx(COINIT_MULTITHREADED)        │
                    │  single-consumer work queue                  │
                    └──────────────────────────────────────────────┘
                       │                │                    │
          ┌────────────┘                │                    └────────────┐
          ▼                             ▼                                 ▼
  ┌───────────────┐          ┌────────────────────┐          ┌──────────────────┐
  │ DeviceRegistry│          │  SessionRegistry   │          │ DefaultDevice-   │
  │               │          │                    │          │ Policy           │
  │ IMMDevice-    │          │ per render device: │          │                  │
  │  Enumerator   │          │  instanceId ->     │          │ IPolicyConfig    │
  │ IMMNotifi-    │          │   IAudioSession-   │          │  (undocumented;  │
  │  cationClient │          │    Control2        │          │   see §6)        │
  │               │          │   ISimpleAudio-    │          │                  │
  │ id -> {       │          │    Volume          │          └──────────────────┘
  │   IMMDevice,  │          │   IAudioSession-   │
  │   IAudioEnd-  │          │    Events          │
  │    pointVolume│          │   pid, imageName   │
  │   IAudioSess- │          │                    │
  │    ionMgr2,   │          │ IAudioSession-     │
  │   IAudioSess- │          │  Notification per  │
  │    ionNotif.  │          │  render device     │
  │ }             │          └────────────────────┘
  └───────────────┘
```

### 2.1 Threading model

This is the part most likely to be got wrong, so it is specified tightly.

- `AudioEngine` owns **one dedicated thread** ("the audio thread") that calls
  `CoInitializeEx(nullptr, COINIT_MULTITHREADED)` at start and `CoUninitialize` at
  shutdown. All COM activation, registration, and unregistration happens on it.
  Register endpoint/session notifications from **MTA** — registering
  `IMMNotificationClient` from an STA thread is a known source of deadlocks.
- The audio thread consumes a **work queue** (`BlockingCollection<T>` or
  `Channel<T>`). Callers (`SliderProcessor`, button actions, the settings UI) post
  work; they never touch COM directly.
- **Core Audio callbacks must not block.** Per Microsoft's documented contract for
  `IAudioSessionNotification` / `IAudioSessionEvents` / `IMMNotificationClient`:
  the methods must be non-blocking, must never wait on a synchronization object,
  and **must not register or unregister notifications from inside a callback**.
  Therefore every callback implementation does exactly one thing: enqueue a
  message onto the audio thread's queue and return `S_OK`. No exceptions.
- Slider writes are fire-and-forget posts. They must **not** allocate a closure per
  tick — use a pre-allocated command struct in a ring buffer, or a `struct`-based
  channel. The hot-path budget is "a dictionary lookup and a COM call."

### 2.2 Public interface

Platform-neutral. No COM type ever appears in it, so `Volco.Core` can reference it
and Linux CI can compile and test everything that consumes it.

```csharp
namespace Volco.Audio;

public enum AudioFlow  { Render, Capture }
public enum AudioRole  { Console, Multimedia, Communications }

/// <summary>Identifies a device: the default for a flow/role, or a specific endpoint id.</summary>
public readonly record struct DeviceRef
{
    public static DeviceRef Default(AudioFlow flow, AudioRole role);
    public static DeviceRef ById(string endpointId);
}

public sealed record AudioDevice(
    string  Id,            // IMMDevice::GetId, e.g. "{0.0.0.00000000}.{guid}"
    string  FriendlyName,  // PKEY_Device_FriendlyName
    AudioFlow Flow,
    bool    IsDefaultConsole,
    bool    IsDefaultMultimedia,
    bool    IsDefaultCommunications);

public sealed record AudioSessionInfo(
    string  InstanceId,    // IAudioSessionControl2::GetSessionInstanceIdentifier
    uint    ProcessId,
    string  ImageName,     // "spotify.exe" — resolved once, at session creation
    string  DeviceId,      // which endpoint this session lives on
    bool    IsSystemSounds);

/// <summary>Which sessions a volume write applies to.</summary>
public abstract record SessionFilter
{
    public sealed record ByImageNames(IReadOnlyCollection<string> Names) : SessionFilter;
    /// <summary>Every live session whose image name is NOT in <c>MappedNames</c>.</summary>
    public sealed record Unmapped(IReadOnlyCollection<string> MappedNames) : SessionFilter;
}

public interface IAudioEngine : IDisposable
{
    // ── Queries: served from the in-memory registry, never enumerate ──
    IReadOnlyList<AudioDevice>      GetDevices(AudioFlow flow);
    AudioDevice?                    GetDefaultDevice(AudioFlow flow, AudioRole role);
    IReadOnlyList<AudioSessionInfo> GetSessions();

    // ── Writes: O(1) lookup + one COM call. Posted to the audio thread. ──
    void SetDeviceVolume(DeviceRef device, float level);   // level in [0,1]
    void SetDeviceMute(DeviceRef device, bool muted);
    void ToggleDeviceMute(DeviceRef device);
    void SetSessionVolume(SessionFilter filter, float level);

    bool TryGetDeviceState(DeviceRef device, out float level, out bool muted);

    /// <summary>Sets the Windows default endpoint. Applies to all three roles.</summary>
    void SetDefaultDevice(string endpointId, AudioFlow flow);

    // ── Notifications: raised on the audio thread; consumers marshal. ──
    event EventHandler<DevicesChangedEventArgs>?      DevicesChanged;
    event EventHandler<SessionsChangedEventArgs>?     SessionsChanged;
    event EventHandler<DeviceVolumeChangedEventArgs>? DeviceVolumeChanged;
}
```

The three event-args types are plain records carrying what the consumer needs to
react without re-querying:

```csharp
public enum DeviceChangeKind  { Added, Removed, StateChanged, DefaultChanged }
public enum SessionChangeKind { Created, Expired }

public sealed record DevicesChangedEventArgs(DeviceChangeKind Kind, AudioDevice Device);
public sealed record SessionsChangedEventArgs(SessionChangeKind Kind, AudioSessionInfo Session);

/// <summary>
/// Raised for an endpoint volume/mute change. <c>IsOwnChange</c> is true when the
/// change carried this process's event-context GUID (see §2.3 invariant 6) and
/// should normally be ignored by consumers to avoid feedback loops.
/// </summary>
public sealed record DeviceVolumeChangedEventArgs(
    string DeviceId, float Level, bool Muted, bool IsOwnChange);
```

### 2.3 Invariants the implementation must hold

1. **`GetDefaultAudioEndpoint` / `EnumerateAudioEndpoints` are called only at
   startup and from `IMMNotificationClient` callbacks.** Never from a write path,
   never from a timer, never from the UI.
2. **One `IAudioEndpointVolume` per device**, created when the device enters the
   registry, released when it leaves.
3. **One `ISimpleAudioVolume` per session**, created on `OnSessionCreated`,
   released when `OnStateChanged` reports `AudioSessionStateExpired` or
   `OnSessionDisconnected` fires.
4. **Process image name is resolved exactly once per session**, at creation, via
   `IAudioSessionControl2::GetProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`
   → `QueryFullProcessImageNameW` → take the file name. Never
   `Process.GetProcessById` on a hot path. Handle the system-sounds session via
   `IAudioSessionControl2::IsSystemSoundsSession` (it reports PID 0).
5. **Every active render device gets a session manager and an
   `IAudioSessionNotification`**, not just the default one. This is what fixes
   "apps on the non-default device are not controlled."
6. **Volume writes pass an event-context GUID** (`SetMasterVolumeLevelScalar(level,
   &ctx)`, `ISimpleAudioVolume::SetMasterVolume(level, &ctx)`) using a process-wide
   GUID, so the engine can ignore its own change notifications and avoid feedback
   loops with the OSD and the controller LEDs.
7. **Session keys are `GetSessionInstanceIdentifier`**, not
   `GetSessionIdentifier` — the latter is shared by all sessions of the same
   application.

### 2.4 Known Core Audio quirks to handle

The implementer will hit these; they are recorded so they are not rediscovered the
hard way.

- **`RegisterSessionNotification` silently delivers nothing** unless
  `IAudioSessionManager2::GetSessionEnumerator()` has been called at least once on
  that manager first. Always enumerate once at registration time, then register.
- **The session enumerator may not know about sessions reported via
  `IAudioSessionNotification`** (Microsoft documents this explicitly). Treat the
  notification as authoritative and merge; do not re-enumerate to "confirm."
- Endpoint volume writes on a device that has just been removed throw
  `COMException` / `InvalidCastException`. The registry, driven by
  `OnDeviceStateChanged`, should make this rare; keep a narrow catch that drops
  the handle rather than the current retry-the-whole-lookup pattern.
- Windows keeps **three separate defaults** — Console, Multimedia, Communications.
  Today's `svcl.exe ... all` sets all three; `SetDefaultDevice` must too, or
  device switching will appear to half-work.

---

## 3. Behavioural improvements this unlocks

New capabilities; both option specs assume them:

1. **New apps inherit their slider's level.** On `OnSessionCreated`, if the new
   session's image name matches a slider mapping — or if no slider claims it and
   some slider is `SliderMappingType.Unmapped` — apply that slider's last computed
   level immediately.
2. **Device rename / unplug / re-plug survives**, because config stores endpoint
   IDs.
3. **External volume changes become visible.** `DeviceVolumeChanged` lets the app
   push correct mute-state LEDs to the controller when the user changes volume in
   Windows rather than on the hardware.
4. **`SliderProcessor`'s `SessionMonitor` dependency disappears.** The `Unmapped`
   filter is evaluated by the engine against its live registry;
   `GetUnmappedProcesses` stays as a pure, tested helper but is fed from
   `IAudioEngine.GetSessions()`.

---

## 4. Config schema migration (v1 → v2)

Required by both options, because device identity moves from name to ID.

```csharp
public class AppConfig
{
    public int SchemaVersion { get; set; } = 2;   // absent/0 => v1
    // ...
}

public class DeviceRefConfig                        // replaces bare name strings
{
    public string Id { get; set; } = "";            // IMMDevice endpoint id — authoritative
    public string FriendlyName { get; set; } = "";  // display + migration fallback only
}
```

Changed fields:
- `SliderConfig.DeviceName` (string) → `SliderConfig.Device` (`DeviceRefConfig?`;
  `null` = "current default output", preserving today's empty-string meaning).
- `OutputDevicesConfig.Device1/Device2` (string) → `DeviceRefConfig`.
- `ButtonActionConfig.DeviceId` — today this holds a *name* despite the property
  name → `DeviceRefConfig?`.

`ConfigMigrator` (new, in `Volco.Core/Config/`, pure and unit-testable):
- Detects `SchemaVersion` absent or `< 2`.
- For each name string, resolves against the current device list by exact
  `FriendlyName` match first, then the legacy `Contains` match, and writes the
  resolved `Id`. Unresolvable entries keep `Id = ""` and retain the name so the
  settings UI can show "device not found — reselect."
- Writes a one-time backup to `config.v1.bak` beside `config.json` before saving
  the migrated file.
- Runs once at startup, after `ConfigManager.Load()`, before the engine starts.

At runtime, `DeviceRefConfig` resolution is **ID first, friendly name as fallback**
— so a config carried to a machine where the device has a different ID still works
once, and self-heals by rewriting the ID.

---

## 5. Removing `svcl.exe`

Delete `src/Volco/Tools/svcl.exe`, its `<Content Include>` item in the csproj, and
`AudioManager.SetDefaultOutputDevice`. Correct the README's Tech Stack section,
which currently claims `COM IPolicyConfig` is already in use.

## 6. `IPolicyConfig` — the replacement, and its caveat

There is **no public Windows API for setting the default audio endpoint**, in any
language or framework. `IPolicyConfig` is undocumented and is what every tool in
this space uses — EarTrumpet, SoundSwitch, AudioSwitcher, the `AudioDeviceCmdlets`
PowerShell module, and `svcl.exe` itself. Calling it in-process is therefore not
*more* hacky than today; it is strictly less so: no child process, no 5 s
`WaitForExit`, no bundled third-party binary, and a real `HRESULT` instead of an
exit code.

Identifiers (the implementer **must** re-verify these against a current reference
implementation before relying on them):

| Name | GUID |
|---|---|
| `CLSID_CPolicyConfigClient` | `{870af99c-171d-4f9e-af0d-e63df40c2bc9}` |
| `IID_IPolicyConfig` (Win7+) | `{f8679f50-850a-41cf-9c72-430f290290c8}` |
| `IID_IPolicyConfigVista` | `{568b9108-44bf-40b4-9006-86afe5b5a620}` |

The Vista interface has a **different vtable layout** — they are not
interchangeable. Implementation notes:

- The method needed is `SetDefaultEndpoint(PCWSTR deviceId, ERole role)`. Call it
  for `eConsole` (0), `eMultimedia` (1), and `eCommunications` (2).
- Declare the interface with the correct vtable slot order; `SetDefaultEndpoint`
  is **not** the first method. Take the ordering from a reference header (the
  widely mirrored `PolicyConfig.h`).
- Wrap it behind an `IDefaultDevicePolicy` interface with a single implementation,
  so if a future Windows release breaks it there is one place to fix and one place
  to fall back from.
- Because it is undocumented, **failures must be non-fatal**: log, surface a
  non-blocking error in the UI, and leave the rest of the app running.

Rust equivalent, should a Rust core ever be revisited: the `com-policy-config`
crate.

---

## 7. Testing

The engine itself needs Windows and real audio hardware, so the split is:

**Platform-neutral, runs on Linux CI (the majority):**
- `IAudioEngine` is an interface; `FakeAudioEngine` in the test project records
  calls and raises events on demand. The existing fake-`IAudioManager` pattern in
  `tests/Volco.Tests/` carries over directly.
- `ConfigMigrator`: v1 → v2 for every field, exact-name match, `Contains`
  fallback, unresolvable entries, backup written, idempotent re-run.
- `SliderProcessor` against `FakeAudioEngine`: verify **exactly one** engine call
  per coalesce tick per slider, that curve / dead-zone / smoothing still apply,
  and that a `SessionsChanged` "created" event for a mapped image name triggers an
  immediate level apply.
- `SessionFilter.Unmapped` evaluation.
- Everything already tested (`ProtocolParser`, `ProtocolEncoder`,
  `ButtonPressDetector`, `ConfigManager`, `WebSocketConnection`, updates) keeps
  passing unchanged.

**Windows-only, manual — run this checklist per option:**
- Unplug the active USB headset mid-slider-sweep → no exception; mapping recovers
  when re-plugged.
- Rename a device in Sound Settings → mapping survives (the ID-vs-name regression
  test).
- Start Spotify while its slider sits at 30 % → it starts at 30 %.
- Play audio to the non-default device → its slider still controls it.
- Switch default device via a button → all three roles change (verify in Sound
  Settings → Advanced).

---

## 8. Measurement protocol

Both options must be measured identically or the comparison is worthless. Record a
**baseline on current `main` first**, then the same numbers per option. Run on the
Windows machine with the controller attached.

1. **Idle** — app running, controller connected, settings window closed, no audio
   playing. Sample for 60 s:
   ```powershell
   Get-Counter '\Process(Volco)\% Processor Time' -SampleInterval 1 -MaxSamples 60 |
     ForEach-Object { $_.CounterSamples.CookedValue } |
     Measure-Object -Average -Maximum
   ```
   Record average and max. Record the **GPU** column for Volco in Task Manager's
   Details tab over the same window.
2. **Sustained sweep** — move all 5 sliders continuously for 60 s, same counters.
3. **Handle / leak check** — before and after the sweep:
   ```powershell
   Get-Process Volco | Select-Object HandleCount, NPM, PM, WS
   ```
   Handle count must be **flat**, not growing. (On current `main` it will grow.)
4. **Cold start** — process launch to tray icon visible. Use the `AppLog`
   timestamp at "App launch complete" plus a stopwatch for the icon.
5. **Working set at idle**, from step 1's `WS`.
6. **OSD latency** — subjective but recorded: does the pill appear within one
   frame of a slider move, and does it ever flicker or steal focus?

Reference targets: idle CPU
under 0.1 % average, idle GPU at 0, flat handle count, cold start under 1 s.

---

## 9. Out of scope

- Any Linux or macOS audio backend. `IAudioEngine` is the seam that makes one
  possible later; writing one now is a separate project. (Note for whoever picks
  that up: PipeWire/PulseAudio expose per-app volume natively as sink-inputs and
  are genuinely straightforward; **macOS has no public per-app volume API at all**
  and would require shipping a virtual audio driver — a different product, not a
  port.)
- Changing the wire protocol. The ESP32 firmware and `tools/simulator/index.html`
  both depend on it; it is frozen.
- Per-app *device routing* (sending an app to a specific endpoint). Interesting,
  not asked for.
