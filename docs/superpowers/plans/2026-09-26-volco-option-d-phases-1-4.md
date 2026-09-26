# Volco Option D — Phases 1–4 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Volco's per-tick COM-thrashing audio layer with an event-driven `AudioEngine`, behind a platform-neutral interface, without changing the UI framework — and measure the result.

**Architecture:** Introduce `IAudioEngine` as a platform-neutral seam and retarget `SliderProcessor` and the button actions onto it while a thin adapter keeps the existing `AudioManager` working (phase 1). Migrate config to endpoint IDs (phase 2) and extract the buggy controller-state reporting into a testable class (phase 3). Only then replace the implementation with a real Core Audio engine that caches COM handles and subscribes to notifications instead of polling (phase 4).

**Tech Stack:** C# / .NET 8 / WinUI 3 (unchanged), xUnit, CsWin32 for COM interop, GitHub Actions as the only test runner.

**Spec:** `docs/superpowers/specs/2026-09-26-volco-option-d-winui-remediation-design.md`
(depends on `docs/superpowers/specs/2026-09-26-volco-audio-core-design.md` — read both)

**Covers:** spec §7 phases 1–4. Phases 5–7 (native OSD, tray de-XAML, final measurement) are a separate plan, written after the phase-4 measurement gate.

---

## Global Constraints

- **Never run `dotnet` locally.** Per `CLAUDE.md`, this machine lacks the setup. CI is the only test runner. See "The Test Loop" below.
- **Never trigger the CI build job on a branch** unless the user explicitly asks. Pushing to a non-`main` branch runs tests only, which is what every task wants.
- **Target frameworks stay as they are:** `net8.0-windows10.0.22621.0` on Windows, `net8.0` elsewhere. `Platforms` = `x64`.
- **The `<Compile Remove>` platform boundary in `src/Volco/Volco.csproj` is load-bearing.** Anything referencing Windows APIs goes under a path excluded on non-Windows (`UI/**`, `App.xaml.cs`, and — new in this plan — `Audio/Windows/**`). Everything else must compile on Linux.
- **The wire protocol is frozen:** `0xAA | length | payload | XOR`. Message types `0x01` slider, `0x02` button, `0x03` mute state, `0x04` active device. The ESP32 firmware and `tools/simulator/index.html` depend on it.
- **Publish output must remain a single `Volco.exe`.** `GitHubUpdater.AssetName = "Volco.exe"`, `ArtifactName = "Volco-win-x64"`, and `UpdateInstaller.ApplyAndRestart` swaps exactly one file.
- **Config lives at** `%APPDATA%/Volco/config.json`; log at `%APPDATA%/Volco/volco.log`.
- Existing test style: xUnit, no mocking library — hand-written fakes as private nested classes or shared test doubles. Match it.

## Execution Order

**Task numbers follow the spec's phase grouping, not execution order.** Execute in this order:

```
1 → 2 → 3 → 6 → 7 → 4 → 5 → 8 → 9 → 10 → 11 → 12 → 13 → 14 → 15
        ^^^^^^^^^^^^^^^
        config v2 and the migrator come BEFORE the retargeting tasks
```

Tasks 4 (`SliderProcessor`) and 5 (button actions) both consume
`SliderConfig.Device` / `ButtonActionConfig.Device`, which Task 6 creates. Running
them earlier will not compile.

## The Test Loop

Every task's "run the test" step means this, from the repo root:

```bash
git add <files>
git commit -m "<message>"
git push                                  # triggers the test job automatically
gh run list --branch option-d-audio-engine --limit 1
gh run watch <run-id>                     # or poll gh run list
gh run view <run-id> --log-failed         # when red
```

Pushing a failing test and watching CI go red **is** the red step. Do not skip it — it is the only proof the test actually exercises new behavior. Two CI round-trips per task is expected and correct.

---

## Review Focus

Five failure modes the spec implies but that no task's happy-path tests would otherwise cover. Each has a test assigned to the task that owns the code.

1. **Corrupt or truncated `config.json` crashes the app at startup.** `ConfigManager.Load` calls `JsonSerializer.Deserialize` with no try/catch, so a half-written or hand-edited file takes the app down before the tray icon appears. A reasonable person expects it to fall back to defaults and log. → **Task 6.**
2. **A migrated device name that matches nothing** (device sold, driver renamed, config copied from another machine). Migration must not throw and must not silently blank the mapping — it must keep the name so the UI can say "reselect". → **Task 7.**
3. **Migration running twice** (app restarts, or a v2 config is loaded by a v2 build). Must be idempotent and must not overwrite the `config.v1.bak` backup with already-migrated content. → **Task 7.**
4. **The current default device matches neither configured toggle device.** Today `Contains(Device1) ? 0 : 1` reports device 2 and lights the wrong LED. Expected: report nothing. → **Task 9.**
5. **A mute/set-device action configured with an empty or null device reference.** `ActionFactory` currently passes `config.DeviceId ?? ""` straight through, producing a lookup for `""` that matches the first device by `Contains`. Expected: no-op. → **Task 5.**

---

## File Structure

**Phase 1 — the seam (all platform-neutral, Linux-testable)**

| File | Responsibility |
|---|---|
| `src/Volco/Audio/AudioPrimitives.cs` (create) | `AudioFlow`, `AudioRole`, `DeviceRefKind`, `DeviceRef`, `AudioDevice`, `AudioSessionInfo`, `SessionFilter` |
| `src/Volco/Audio/AudioEvents.cs` (create) | `DeviceChangeKind`, `SessionChangeKind`, and the three event-args records |
| `src/Volco/Audio/IAudioEngine.cs` (create) | The interface |
| `src/Volco/Audio/LegacyAudioEngineAdapter.cs` (create) | `IAudioEngine` over the existing `IAudioManager`. Transitional — deleted in Task 14 |
| `tests/Volco.Tests/Audio/FakeAudioEngine.cs` (create) | Recording test double, shared across test classes |

**Phase 2 — config v2**

| File | Responsibility |
|---|---|
| `src/Volco/Config/DeviceRefConfig.cs` (create) | `{ Id, FriendlyName }` |
| `src/Volco/Config/AppConfig.cs` (modify) | `SchemaVersion`; `SliderConfig.Device`, `OutputDevicesConfig.Device1/2`, `ButtonActionConfig.Device` become `DeviceRefConfig` |
| `src/Volco/Config/ConfigMigrator.cs` (create) | Pure v1→v2 transform + backup |
| `src/Volco/Config/ConfigManager.cs` (modify) | Resilient `Load`; invoke migrator |

**Phase 3 — controller state**

| File | Responsibility |
|---|---|
| `src/Volco/Communication/ControllerStateReporter.cs` (create) | Computes the `0x03`/`0x04` frames to send. Pure, testable |
| `src/Volco/App.xaml.cs` (modify) | Delegates to it |

**Phase 4 — the real engine (Windows-only)**

| File | Responsibility |
|---|---|
| `src/Volco/NativeMethods.txt` (create) | CsWin32 input |
| `src/Volco/Audio/Windows/AudioEngine.cs` (create) | Audio thread, work queue, composition of the registries |
| `src/Volco/Audio/Windows/DeviceRegistry.cs` (create) | `IMMDeviceEnumerator` + `IMMNotificationClient`; device handle cache |
| `src/Volco/Audio/Windows/SessionRegistry.cs` (create) | Per-device `IAudioSessionManager2`, `IAudioSessionNotification`, `IAudioSessionEvents`; session handle cache |
| `src/Volco/Audio/Windows/DefaultDevicePolicy.cs` (create) | `IPolicyConfig` |
| `src/Volco/Audio/AudioManager.cs`, `Audio/SessionMonitor.cs`, `Audio/IAudioManager.cs`, `Tools/svcl.exe` | **Deleted in Task 14** |

---

## Sequencing trap — read before starting

The spec's phase order (1 → 2 → 3 → 4) puts **config v2 (endpoint IDs) in phase 2 but the real engine in phase 4**. Between them, the app runs on `LegacyAudioEngineAdapter`, which wraps the old name-based `AudioManager` and cannot resolve an endpoint ID.

**The fix is built into this plan, not left to the implementer:** `DeviceRefConfig` carries **both** `Id` and `FriendlyName`, and `DeviceRef` has three kinds — `Default`, `ById`, `ByFriendlyName`. `DeviceRef.FromConfig` prefers `Id`, but the **adapter ignores `ById` and resolves by `FriendlyName`** (Task 3), while the real engine prefers `Id` and falls back to `FriendlyName` (Task 12). So the app stays functional across phases 2–3 with no behavior change, exactly as the spec promises.

Do not "simplify" `DeviceRefConfig` down to a single field. It is two fields on purpose.

---

### Task 1: The audio primitives and `IAudioEngine`

**Files:**
- Create: `src/Volco/Audio/AudioPrimitives.cs`
- Create: `src/Volco/Audio/AudioEvents.cs`
- Create: `src/Volco/Audio/IAudioEngine.cs`
- Test: `tests/Volco.Tests/Audio/AudioPrimitivesTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: everything below. Tasks 2–14 depend on these exact names and signatures.

- [ ] **Step 1: Create the working branch**

```bash
git checkout -b option-d-audio-engine
git push -u origin option-d-audio-engine
```

- [ ] **Step 2: Write the failing test**

Create `tests/Volco.Tests/Audio/AudioPrimitivesTests.cs`:

```csharp
using Volco.Audio;
using Xunit;

namespace Volco.Tests.Audio;

public class AudioPrimitivesTests
{
    [Fact]
    public void DeviceRef_Default_HasDefaultKindAndNoId()
    {
        var r = DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia);
        Assert.Equal(DeviceRefKind.Default, r.Kind);
        Assert.Equal(AudioFlow.Render, r.Flow);
        Assert.Equal(AudioRole.Multimedia, r.Role);
        Assert.Equal("", r.Id);
    }

    [Fact]
    public void DeviceRef_ById_CarriesTheEndpointId()
    {
        var r = DeviceRef.ById("{0.0.0.00000000}.{abc}", AudioFlow.Render);
        Assert.Equal(DeviceRefKind.ById, r.Kind);
        Assert.Equal("{0.0.0.00000000}.{abc}", r.Id);
    }

    [Fact]
    public void DeviceRef_ByFriendlyName_CarriesTheName()
    {
        var r = DeviceRef.ByFriendlyName("Headphones", AudioFlow.Render);
        Assert.Equal(DeviceRefKind.ByFriendlyName, r.Kind);
        Assert.Equal("Headphones", r.Id);
    }

    [Fact]
    public void DeviceRef_IsAValueType_SoEqualityIsStructural()
    {
        Assert.Equal(DeviceRef.ById("x"), DeviceRef.ById("x"));
        Assert.NotEqual(DeviceRef.ById("x"), DeviceRef.ById("y"));
    }
}
```

- [ ] **Step 3: Run the test loop, verify it fails**

Commit and push per "The Test Loop". Expected: compile failure, `The type or namespace name 'DeviceRef' could not be found`.

- [ ] **Step 4: Create `AudioPrimitives.cs`**

```csharp
namespace Volco.Audio;

public enum AudioFlow { Render, Capture }

public enum AudioRole { Console, Multimedia, Communications }

public enum DeviceRefKind { Default, ById, ByFriendlyName }

/// <summary>
/// Identifies a device without binding to a COM object: the default endpoint for a
/// flow/role, a specific endpoint id, or a friendly name (the fallback used while
/// config carries no id yet, and by the transitional legacy adapter).
/// </summary>
public readonly record struct DeviceRef(DeviceRefKind Kind, AudioFlow Flow, AudioRole Role, string Id)
{
    public static DeviceRef Default(AudioFlow flow, AudioRole role)
        => new(DeviceRefKind.Default, flow, role, "");

    public static DeviceRef ById(string endpointId, AudioFlow flow = AudioFlow.Render)
        => new(DeviceRefKind.ById, flow, AudioRole.Multimedia, endpointId);

    public static DeviceRef ByFriendlyName(string friendlyName, AudioFlow flow = AudioFlow.Render)
        => new(DeviceRefKind.ByFriendlyName, flow, AudioRole.Multimedia, friendlyName);
}

public sealed record AudioDevice(
    string    Id,
    string    FriendlyName,
    AudioFlow Flow,
    bool      IsDefaultConsole,
    bool      IsDefaultMultimedia,
    bool      IsDefaultCommunications);

public sealed record AudioSessionInfo(
    string InstanceId,
    uint   ProcessId,
    string ImageName,
    string DeviceId,
    bool   IsSystemSounds);

/// <summary>Selects which live sessions a volume write applies to.</summary>
public abstract record SessionFilter
{
    private SessionFilter() { }

    public sealed record ByImageNames(IReadOnlyCollection<string> Names) : SessionFilter;

    /// <summary>Every live session whose image name is NOT in <paramref name="MappedNames"/>.</summary>
    public sealed record Unmapped(IReadOnlyCollection<string> MappedNames) : SessionFilter;
}
```

- [ ] **Step 5: Create `AudioEvents.cs`**

```csharp
namespace Volco.Audio;

public enum DeviceChangeKind { Added, Removed, StateChanged, DefaultChanged }

public enum SessionChangeKind { Created, Expired }

public sealed record DevicesChangedEventArgs(DeviceChangeKind Kind, AudioDevice Device);

public sealed record SessionsChangedEventArgs(SessionChangeKind Kind, AudioSessionInfo Session);

/// <summary>
/// An endpoint volume or mute change. <paramref name="IsOwnChange"/> is true when the
/// change carried this process's event-context GUID; consumers normally ignore those
/// to avoid feedback loops with the OSD and the controller LEDs.
/// </summary>
public sealed record DeviceVolumeChangedEventArgs(
    string DeviceId, float Level, bool Muted, bool IsOwnChange);
```

- [ ] **Step 6: Create `IAudioEngine.cs`**

```csharp
namespace Volco.Audio;

public interface IAudioEngine : IDisposable
{
    // Queries — served from the in-memory registry, never enumerate.
    IReadOnlyList<AudioDevice>      GetDevices(AudioFlow flow);
    AudioDevice?                    GetDefaultDevice(AudioFlow flow, AudioRole role);
    IReadOnlyList<AudioSessionInfo> GetSessions();

    // Writes — O(1) lookup plus one COM call, posted to the audio thread.
    void SetDeviceVolume(DeviceRef device, float level);
    void SetDeviceMute(DeviceRef device, bool muted);
    void ToggleDeviceMute(DeviceRef device);
    void SetSessionVolume(SessionFilter filter, float level);

    bool TryGetDeviceState(DeviceRef device, out float level, out bool muted);

    /// <summary>Sets the Windows default endpoint for all three roles.</summary>
    void SetDefaultDevice(string endpointId, AudioFlow flow);

    event EventHandler<DevicesChangedEventArgs>?      DevicesChanged;
    event EventHandler<SessionsChangedEventArgs>?     SessionsChanged;
    event EventHandler<DeviceVolumeChangedEventArgs>? DeviceVolumeChanged;
}
```

- [ ] **Step 7: Run the test loop, verify green**

- [ ] **Step 8: Commit**

```bash
git add src/Volco/Audio/AudioPrimitives.cs src/Volco/Audio/AudioEvents.cs \
        src/Volco/Audio/IAudioEngine.cs tests/Volco.Tests/Audio/AudioPrimitivesTests.cs
git commit -m "feat(audio): add platform-neutral IAudioEngine seam and primitives"
git push
```

---

### Task 2: `FakeAudioEngine` test double

**Files:**
- Create: `tests/Volco.Tests/Audio/FakeAudioEngine.cs`
- Test: `tests/Volco.Tests/Audio/FakeAudioEngineTests.cs`

**Interfaces:**
- Consumes: `IAudioEngine` and all primitives from Task 1.
- Produces: `FakeAudioEngine` with public fields `DeviceVolumeCalls` (`List<(DeviceRef, float)>`), `SessionVolumeCalls` (`List<(SessionFilter, float)>`), `ToggleMuteCalls` (`List<DeviceRef>`), `SetDefaultCalls` (`List<(string, AudioFlow)>`), settable `Devices` (`List<AudioDevice>`), `Sessions` (`List<AudioSessionInfo>`), `DefaultRender`/`DefaultCapture` (`AudioDevice?`), `DeviceStates` (`Dictionary<string,(float,bool)>`), and methods `RaiseSessionCreated(AudioSessionInfo)`, `RaiseSessionExpired(AudioSessionInfo)`, `RaiseDeviceChanged(DeviceChangeKind, AudioDevice)`. Tasks 4, 5, 9 all use these.

- [ ] **Step 1: Write the failing test**

Create `tests/Volco.Tests/Audio/FakeAudioEngineTests.cs`:

```csharp
using Volco.Audio;
using Xunit;

namespace Volco.Tests.Audio;

public class FakeAudioEngineTests
{
    [Fact]
    public void RecordsDeviceVolumeCalls()
    {
        using var fake = new FakeAudioEngine();
        fake.SetDeviceVolume(DeviceRef.ById("dev-1"), 0.25f);

        var (device, level) = Assert.Single(fake.DeviceVolumeCalls);
        Assert.Equal(DeviceRef.ById("dev-1"), device);
        Assert.Equal(0.25f, level, 3);
    }

    [Fact]
    public void RecordsSessionVolumeCalls()
    {
        using var fake = new FakeAudioEngine();
        fake.SetSessionVolume(new SessionFilter.ByImageNames(new[] { "spotify.exe" }), 0.5f);

        var (filter, level) = Assert.Single(fake.SessionVolumeCalls);
        var byName = Assert.IsType<SessionFilter.ByImageNames>(filter);
        Assert.Equal(new[] { "spotify.exe" }, byName.Names);
        Assert.Equal(0.5f, level, 3);
    }

    [Fact]
    public void RaiseSessionCreated_FiresSessionsChanged()
    {
        using var fake = new FakeAudioEngine();
        SessionsChangedEventArgs? seen = null;
        fake.SessionsChanged += (_, e) => seen = e;

        var session = new AudioSessionInfo("inst-1", 42, "spotify.exe", "dev-1", false);
        fake.RaiseSessionCreated(session);

        Assert.NotNull(seen);
        Assert.Equal(SessionChangeKind.Created, seen!.Kind);
        Assert.Equal("spotify.exe", seen.Session.ImageName);
    }

    [Fact]
    public void TryGetDeviceState_ReturnsSeededState()
    {
        using var fake = new FakeAudioEngine();
        fake.DeviceStates["dev-1"] = (0.4f, true);

        Assert.True(fake.TryGetDeviceState(DeviceRef.ById("dev-1"), out var level, out var muted));
        Assert.Equal(0.4f, level, 3);
        Assert.True(muted);
    }

    [Fact]
    public void TryGetDeviceState_UnknownDevice_ReturnsFalse()
    {
        using var fake = new FakeAudioEngine();
        Assert.False(fake.TryGetDeviceState(DeviceRef.ById("nope"), out _, out _));
    }
}
```

- [ ] **Step 2: Run the test loop, verify it fails**

Expected: `The type or namespace name 'FakeAudioEngine' could not be found`.

- [ ] **Step 3: Create `FakeAudioEngine.cs`**

```csharp
using Volco.Audio;

namespace Volco.Tests.Audio;

/// <summary>
/// Recording in-memory <see cref="IAudioEngine"/> for tests. Synchronous: writes land in
/// the call lists immediately, with no thread hop, so tests need no waiting.
/// </summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    public List<(DeviceRef Device, float Level)>  DeviceVolumeCalls  { get; } = new();
    public List<(DeviceRef Device, bool Muted)>   SetMuteCalls       { get; } = new();
    public List<DeviceRef>                        ToggleMuteCalls    { get; } = new();
    public List<(SessionFilter Filter, float Level)> SessionVolumeCalls { get; } = new();
    public List<(string EndpointId, AudioFlow Flow)> SetDefaultCalls { get; } = new();

    public List<AudioDevice>      Devices  { get; set; } = new();
    public List<AudioSessionInfo> Sessions { get; set; } = new();
    public AudioDevice? DefaultRender  { get; set; }
    public AudioDevice? DefaultCapture { get; set; }

    /// <summary>Endpoint id -> (level, muted). Seed this to drive TryGetDeviceState.</summary>
    public Dictionary<string, (float Level, bool Muted)> DeviceStates { get; } = new();

    public event EventHandler<DevicesChangedEventArgs>?      DevicesChanged;
    public event EventHandler<SessionsChangedEventArgs>?     SessionsChanged;
    public event EventHandler<DeviceVolumeChangedEventArgs>? DeviceVolumeChanged;

    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow)
        => Devices.Where(d => d.Flow == flow).ToList();

    public AudioDevice? GetDefaultDevice(AudioFlow flow, AudioRole role)
        => flow == AudioFlow.Render ? DefaultRender : DefaultCapture;

    public IReadOnlyList<AudioSessionInfo> GetSessions() => Sessions;

    public void SetDeviceVolume(DeviceRef device, float level)
        => DeviceVolumeCalls.Add((device, level));

    public void SetDeviceMute(DeviceRef device, bool muted)
        => SetMuteCalls.Add((device, muted));

    public void ToggleDeviceMute(DeviceRef device)
        => ToggleMuteCalls.Add(device);

    public void SetSessionVolume(SessionFilter filter, float level)
        => SessionVolumeCalls.Add((filter, level));

    public void SetDefaultDevice(string endpointId, AudioFlow flow)
        => SetDefaultCalls.Add((endpointId, flow));

    public bool TryGetDeviceState(DeviceRef device, out float level, out bool muted)
    {
        var key = device.Kind == DeviceRefKind.Default
            ? (device.Flow == AudioFlow.Render ? DefaultRender?.Id : DefaultCapture?.Id) ?? ""
            : device.Id;

        if (DeviceStates.TryGetValue(key, out var state))
        {
            (level, muted) = state;
            return true;
        }
        level = 0f;
        muted = false;
        return false;
    }

    public void RaiseSessionCreated(AudioSessionInfo session)
        => SessionsChanged?.Invoke(this, new SessionsChangedEventArgs(SessionChangeKind.Created, session));

    public void RaiseSessionExpired(AudioSessionInfo session)
        => SessionsChanged?.Invoke(this, new SessionsChangedEventArgs(SessionChangeKind.Expired, session));

    public void RaiseDeviceChanged(DeviceChangeKind kind, AudioDevice device)
        => DevicesChanged?.Invoke(this, new DevicesChangedEventArgs(kind, device));

    public void RaiseDeviceVolumeChanged(string deviceId, float level, bool muted, bool isOwnChange)
        => DeviceVolumeChanged?.Invoke(this, new DeviceVolumeChangedEventArgs(deviceId, level, muted, isOwnChange));

    public void Dispose() { }
}
```

- [ ] **Step 4: Run the test loop, verify green**

- [ ] **Step 5: Commit**

```bash
git add tests/Volco.Tests/Audio/
git commit -m "test(audio): add recording FakeAudioEngine test double"
git push
```

---

### Task 3: `LegacyAudioEngineAdapter`

Keeps the app running on the old `AudioManager` while phases 1–3 land. Deleted in Task 14.

**Files:**
- Create: `src/Volco/Audio/LegacyAudioEngineAdapter.cs`
- Test: `tests/Volco.Tests/Audio/LegacyAudioEngineAdapterTests.cs`

**Interfaces:**
- Consumes: `IAudioEngine` (Task 1), existing `IAudioManager`.
- Produces: `LegacyAudioEngineAdapter(IAudioManager legacy) : IAudioEngine`.

- [ ] **Step 1: Write the failing test**

Create `tests/Volco.Tests/Audio/LegacyAudioEngineAdapterTests.cs`:

```csharp
using Volco.Audio;
using Xunit;

namespace Volco.Tests.Audio;

public class LegacyAudioEngineAdapterTests
{
    [Fact]
    public void SetDeviceVolume_Default_PassesNullDeviceName()
    {
        var legacy = new RecordingAudioManager();
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDeviceVolume(DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia), 0.3f);

        var (name, volume) = Assert.Single(legacy.DeviceVolumeCalls);
        Assert.Null(name);
        Assert.Equal(0.3f, volume, 3);
    }

    [Fact]
    public void SetDeviceVolume_ByFriendlyName_PassesTheName()
    {
        var legacy = new RecordingAudioManager();
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDeviceVolume(DeviceRef.ByFriendlyName("Headphones"), 0.6f);

        var (name, _) = Assert.Single(legacy.DeviceVolumeCalls);
        Assert.Equal("Headphones", name);
    }

    [Fact]
    public void SetDeviceVolume_ById_FallsBackToDefaultDevice()
    {
        // The legacy manager has no concept of endpoint ids. Rather than pass an id into a
        // name-based Contains() lookup (which would match the wrong device), the adapter
        // treats an id-only ref as "the default device".
        var legacy = new RecordingAudioManager();
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetDeviceVolume(DeviceRef.ById("{0.0.0.00000000}.{abc}"), 0.7f);

        var (name, _) = Assert.Single(legacy.DeviceVolumeCalls);
        Assert.Null(name);
    }

    [Fact]
    public void SetSessionVolume_ByImageNames_ForwardsTheNames()
    {
        var legacy = new RecordingAudioManager();
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetSessionVolume(new SessionFilter.ByImageNames(new[] { "spotify.exe" }), 0.4f);

        var (names, volume) = Assert.Single(legacy.ProcessVolumeCalls);
        Assert.Equal(new[] { "spotify.exe" }, names);
        Assert.Equal(0.4f, volume, 3);
    }

    [Fact]
    public void SetSessionVolume_Unmapped_ResolvesAgainstActiveProcesses()
    {
        var legacy = new RecordingAudioManager
        {
            ActiveProcessNames = new List<string> { "chrome.exe", "spotify.exe", "discord.exe" }
        };
        using var adapter = new LegacyAudioEngineAdapter(legacy);

        adapter.SetSessionVolume(new SessionFilter.Unmapped(new[] { "spotify.exe" }), 0.2f);

        var (names, _) = Assert.Single(legacy.ProcessVolumeCalls);
        Assert.Contains("chrome.exe", names);
        Assert.Contains("discord.exe", names);
        Assert.DoesNotContain("spotify.exe", names);
    }

    private sealed class RecordingAudioManager : IAudioManager
    {
        public List<(string? DeviceName, float Volume)> DeviceVolumeCalls { get; } = new();
        public List<(IReadOnlyCollection<string> Names, float Volume)> ProcessVolumeCalls { get; } = new();
        public List<string?> ToggleMuteOutputCalls { get; } = new();
        public List<string> SetDefaultCalls { get; } = new();
        public List<string> ActiveProcessNames { get; set; } = new();

        public void SetProcessVolume(string processName, float volume)
            => ProcessVolumeCalls.Add((new[] { processName }, volume));
        public void SetProcessVolumes(IReadOnlyCollection<string> processNames, float volume)
            => ProcessVolumeCalls.Add((processNames, volume));
        public void SetDeviceVolume(float volume, string? deviceName = null)
            => DeviceVolumeCalls.Add((deviceName, volume));
        public void ToggleMuteOutput(string? deviceId = null) => ToggleMuteOutputCalls.Add(deviceId);
        public void ToggleMuteInput() { }
        public void SetDefaultOutputDevice(string deviceName) => SetDefaultCalls.Add(deviceName);
        public string? GetDefaultOutputDeviceName() => "Speakers";
        public List<AudioDeviceInfo> GetOutputDevices() => new();
        public List<AudioDeviceInfo> GetInputDevices() => new();
        public List<string> GetActiveProcessNames() => ActiveProcessNames;
        public bool IsOutputMuted(string? deviceId = null) => false;
        public bool IsInputMuted() => false;
    }
}
```

- [ ] **Step 2: Run the test loop, verify it fails**

- [ ] **Step 3: Create `LegacyAudioEngineAdapter.cs`**

```csharp
namespace Volco.Audio;

/// <summary>
/// Transitional <see cref="IAudioEngine"/> implemented over the old name-based
/// <see cref="IAudioManager"/>, so the app keeps working while the seam lands.
/// Deleted once the real Core Audio engine replaces it.
///
/// Deliberately lossy: the legacy manager has no endpoint ids and no session identity,
/// so <see cref="DeviceRefKind.ById"/> degrades to "the default device" rather than
/// feeding an id into a friendly-name substring match, and the change events never fire.
/// </summary>
public sealed class LegacyAudioEngineAdapter : IAudioEngine
{
    private readonly IAudioManager _legacy;

    public LegacyAudioEngineAdapter(IAudioManager legacy) => _legacy = legacy;

#pragma warning disable 67 // The legacy manager has no notifications to forward.
    public event EventHandler<DevicesChangedEventArgs>?      DevicesChanged;
    public event EventHandler<SessionsChangedEventArgs>?     SessionsChanged;
    public event EventHandler<DeviceVolumeChangedEventArgs>? DeviceVolumeChanged;
#pragma warning restore 67

    private static string? ToLegacyName(DeviceRef device)
        => device.Kind == DeviceRefKind.ByFriendlyName && !string.IsNullOrEmpty(device.Id)
            ? device.Id
            : null;

    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow)
    {
        var infos = flow == AudioFlow.Render ? _legacy.GetOutputDevices() : _legacy.GetInputDevices();
        return infos.Select(i => new AudioDevice(i.Id, i.Name, flow, false, false, false)).ToList();
    }

    public AudioDevice? GetDefaultDevice(AudioFlow flow, AudioRole role)
    {
        if (flow != AudioFlow.Render) return null;
        var name = _legacy.GetDefaultOutputDeviceName();
        return name == null ? null : new AudioDevice("", name, flow, true, true, true);
    }

    public IReadOnlyList<AudioSessionInfo> GetSessions()
        => _legacy.GetActiveProcessNames()
                  .Select(n => new AudioSessionInfo(n, 0, n, "", false))
                  .ToList();

    public void SetDeviceVolume(DeviceRef device, float level)
        => _legacy.SetDeviceVolume(level, ToLegacyName(device));

    public void SetDeviceMute(DeviceRef device, bool muted)
    {
        // The legacy manager only toggles. Read, then toggle if it disagrees.
        var current = _legacy.IsOutputMuted(ToLegacyName(device));
        if (current != muted) _legacy.ToggleMuteOutput(ToLegacyName(device));
    }

    public void ToggleDeviceMute(DeviceRef device)
    {
        if (device.Flow == AudioFlow.Capture) _legacy.ToggleMuteInput();
        else _legacy.ToggleMuteOutput(ToLegacyName(device));
    }

    public void SetSessionVolume(SessionFilter filter, float level)
    {
        switch (filter)
        {
            case SessionFilter.ByImageNames byNames:
                _legacy.SetProcessVolumes(byNames.Names, level);
                break;

            case SessionFilter.Unmapped unmapped:
                var mapped = new HashSet<string>(unmapped.MappedNames, StringComparer.OrdinalIgnoreCase);
                var targets = _legacy.GetActiveProcessNames()
                                     .Where(n => !mapped.Contains(n))
                                     .ToList();
                _legacy.SetProcessVolumes(targets, level);
                break;
        }
    }

    public bool TryGetDeviceState(DeviceRef device, out float level, out bool muted)
    {
        level = 0f;
        muted = device.Flow == AudioFlow.Capture
            ? _legacy.IsInputMuted()
            : _legacy.IsOutputMuted(ToLegacyName(device));
        return true;
    }

    public void SetDefaultDevice(string endpointId, AudioFlow flow)
        => _legacy.SetDefaultOutputDevice(endpointId);

    public void Dispose() { }
}
```

- [ ] **Step 4: Run the test loop, verify green**

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Audio/LegacyAudioEngineAdapter.cs tests/Volco.Tests/Audio/LegacyAudioEngineAdapterTests.cs
git commit -m "feat(audio): add transitional IAudioManager -> IAudioEngine adapter"
git push
```

---

### Task 4: Retarget `SliderProcessor` to `IAudioEngine`

**Files:**
- Modify: `src/Volco/Input/SliderProcessor.cs`
- Modify: `tests/Volco.Tests/Input/SliderProcessorTests.cs`

**Interfaces:**
- Consumes: `IAudioEngine`, `SessionFilter`, `FakeAudioEngine`.
- Produces: `SliderProcessor(IAudioEngine audio, ConfigManager configManager)` — **the `SessionMonitor` parameter is removed**. `GetUnmappedProcesses` keeps its current signature and stays public and pure.

- [ ] **Step 1: Write the failing test**

Add to `tests/Volco.Tests/Input/SliderProcessorTests.cs` (and delete the `NoOpAudioManager` nested class and the `SessionMonitor` usage in the existing `SliderLevelChanged_...` test, replacing them with `FakeAudioEngine`):

```csharp
[Fact]
public async Task DeviceSlider_IssuesExactlyOneEngineCallPerTick()
{
    var configManager = NewConfigManager();
    configManager.Config.Sliders.Clear();
    configManager.Config.Sliders.Add(new SliderConfig
    {
        Index = 0,
        Type = SliderMappingType.Device,
        Device = null,                     // null => current default output
    });

    using var audio = new FakeAudioEngine();
    using var processor = new SliderProcessor(audio, configManager);

    processor.HandleSliderEvent(sliderIndex: 0, adcValue: 2048);
    await WaitForAsync(() => audio.DeviceVolumeCalls.Count >= 1);

    var (device, level) = Assert.Single(audio.DeviceVolumeCalls);
    Assert.Equal(DeviceRefKind.Default, device.Kind);
    Assert.InRange(level, 0.4f, 0.6f);
}

[Fact]
public async Task ProcessSlider_SendsByImageNamesFilter()
{
    var configManager = NewConfigManager();
    configManager.Config.Sliders.Clear();
    configManager.Config.Sliders.Add(new SliderConfig
    {
        Index = 0,
        Type = SliderMappingType.Processes,
        Processes = new List<string> { "spotify.exe" },
    });

    using var audio = new FakeAudioEngine();
    using var processor = new SliderProcessor(audio, configManager);

    processor.HandleSliderEvent(0, 4095);
    await WaitForAsync(() => audio.SessionVolumeCalls.Count >= 1);

    var (filter, level) = Assert.Single(audio.SessionVolumeCalls);
    var byNames = Assert.IsType<SessionFilter.ByImageNames>(filter);
    Assert.Equal(new[] { "spotify.exe" }, byNames.Names);
    Assert.Equal(1f, level, 2);
}

[Fact]
public async Task UnmappedSlider_SendsUnmappedFilterCarryingMappedNames()
{
    var configManager = NewConfigManager();
    configManager.Config.Sliders.Clear();
    configManager.Config.Sliders.Add(new SliderConfig
    {
        Index = 0, Type = SliderMappingType.Processes,
        Processes = new List<string> { "spotify.exe" },
    });
    configManager.Config.Sliders.Add(new SliderConfig { Index = 1, Type = SliderMappingType.Unmapped });

    using var audio = new FakeAudioEngine();
    using var processor = new SliderProcessor(audio, configManager);

    processor.HandleSliderEvent(1, 1024);
    await WaitForAsync(() => audio.SessionVolumeCalls.Any(c => c.Filter is SessionFilter.Unmapped));

    var call = audio.SessionVolumeCalls.First(c => c.Filter is SessionFilter.Unmapped);
    var unmapped = Assert.IsType<SessionFilter.Unmapped>(call.Filter);
    Assert.Contains("spotify.exe", unmapped.MappedNames);
}

[Fact]
public async Task NewSessionForMappedProcess_ImmediatelyAppliesThatSlidersLevel()
{
    var configManager = NewConfigManager();
    configManager.Config.Sliders.Clear();
    configManager.Config.Sliders.Add(new SliderConfig
    {
        Index = 0, Type = SliderMappingType.Processes,
        Processes = new List<string> { "spotify.exe" },
    });

    using var audio = new FakeAudioEngine();
    using var processor = new SliderProcessor(audio, configManager);

    // Establish a level, then let the coalesce tick flush it.
    processor.HandleSliderEvent(0, 1024);
    await WaitForAsync(() => audio.SessionVolumeCalls.Count >= 1);
    var before = audio.SessionVolumeCalls.Count;

    audio.RaiseSessionCreated(new AudioSessionInfo("inst-9", 7, "spotify.exe", "dev-1", false));
    await WaitForAsync(() => audio.SessionVolumeCalls.Count > before);

    var last = audio.SessionVolumeCalls[^1];
    var byNames = Assert.IsType<SessionFilter.ByImageNames>(last.Filter);
    Assert.Contains("spotify.exe", byNames.Names);
    Assert.InRange(last.Level, 0.2f, 0.3f);
}

// ── helpers ──────────────────────────────────────────────────────────────

private static ConfigManager NewConfigManager()
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"volco-slidertest-{Guid.NewGuid()}", "config.json");
    var cm = new ConfigManager(tempPath);
    cm.Load();
    return cm;
}

/// <summary>Polls until <paramref name="condition"/> holds or the timeout expires.</summary>
private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 1000)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        if (condition()) return;
        await Task.Delay(10);
    }
    Assert.Fail($"Condition not met within {timeoutMs}ms");
}
```

- [ ] **Step 2: Run the test loop, verify it fails**

- [ ] **Step 3: Modify `SliderProcessor`**

Change the constructor and the dispatch switch. Replace the `_audio`/`_sessionMonitor` fields:

```csharp
private readonly IAudioEngine _audio;
private readonly ConfigManager _configManager;
private readonly ushort?[] _pendingValues = new ushort?[16];
private readonly float?[] _lastVolume = new float?[16];
private readonly Timer _coalesceTimer;
private readonly object _lock = new();

public SliderProcessor(IAudioEngine audio, ConfigManager configManager)
{
    _audio = audio;
    _configManager = configManager;
    _audio.SessionsChanged += OnSessionsChanged;
    _coalesceTimer = new Timer(_ => ProcessPending(), null, 20, 20);
}
```

Replace the dispatch switch inside `ProcessPending`:

```csharp
switch (sliderConfig.Type)
{
    case SliderMappingType.Device:
        _audio.SetDeviceVolume(DeviceRefFor(sliderConfig), volume);
        break;

    case SliderMappingType.Processes:
        _audio.SetSessionVolume(new SessionFilter.ByImageNames(sliderConfig.Processes), volume);
        break;

    case SliderMappingType.Unmapped:
        _audio.SetSessionVolume(new SessionFilter.Unmapped(MappedProcessNames(config)), volume);
        break;
}
```

Add these members:

```csharp
/// <summary>Resolves a slider's configured device to a <see cref="DeviceRef"/>.</summary>
internal static DeviceRef DeviceRefFor(SliderConfig slider)
{
    if (slider.Device == null) return DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia);
    if (!string.IsNullOrEmpty(slider.Device.Id)) return DeviceRef.ById(slider.Device.Id);
    if (!string.IsNullOrEmpty(slider.Device.FriendlyName)) return DeviceRef.ByFriendlyName(slider.Device.FriendlyName);
    return DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia);
}

/// <summary>Every process name claimed by a Processes-type slider.</summary>
internal static List<string> MappedProcessNames(AppConfig config)
{
    var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var slider in config.Sliders)
        if (slider.Type == SliderMappingType.Processes)
            foreach (var p in slider.Processes)
                mapped.Add(p);
    return mapped.ToList();
}

/// <summary>
/// A newly created session that a Processes slider claims gets that slider's current
/// level applied immediately, so an app launched mid-session does not play at whatever
/// volume Windows gave it.
/// </summary>
private void OnSessionsChanged(object? sender, SessionsChangedEventArgs e)
{
    if (e.Kind != SessionChangeKind.Created) return;

    var config = _configManager.Config;
    for (int i = 0; i < config.Sliders.Count && i < _lastVolume.Length; i++)
    {
        var slider = config.Sliders[i];
        if (_lastVolume[i] is not { } level) continue;

        if (slider.Type == SliderMappingType.Processes &&
            slider.Processes.Any(p => string.Equals(p, e.Session.ImageName, StringComparison.OrdinalIgnoreCase)))
        {
            _audio.SetSessionVolume(new SessionFilter.ByImageNames(new[] { e.Session.ImageName }), level);
            return;
        }
    }

    // Not claimed by any Processes slider — an Unmapped slider owns it, if one exists.
    for (int i = 0; i < config.Sliders.Count && i < _lastVolume.Length; i++)
    {
        if (config.Sliders[i].Type == SliderMappingType.Unmapped && _lastVolume[i] is { } level)
        {
            _audio.SetSessionVolume(new SessionFilter.ByImageNames(new[] { e.Session.ImageName }), level);
            return;
        }
    }
}
```

Update `Dispose` to unsubscribe:

```csharp
public void Dispose()
{
    _audio.SessionsChanged -= OnSessionsChanged;
    _coalesceTimer.Dispose();
}
```

Also update the `VolumeChanged` label lookup: replace `_audio.GetDefaultOutputDeviceName()` with `_audio.GetDefaultDevice(AudioFlow.Render, AudioRole.Multimedia)?.FriendlyName`.

**Keep `BuildSliderLabel`'s signature and all eight of its existing tests unchanged.** It reads `slider.DeviceName`, and Task 6 adds a `[JsonIgnore]` computed `DeviceName => Device?.FriendlyName ?? ""` property to `SliderConfig` for exactly this reason. Adapt the call site, never the function.

- [ ] **Step 4: Run the test loop, verify green**

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Input/SliderProcessor.cs tests/Volco.Tests/Input/SliderProcessorTests.cs
git commit -m "refactor(slider): retarget SliderProcessor onto IAudioEngine"
git push
```

---

### Task 5: Retarget button actions to `IAudioEngine`

Includes **Review Focus item 5** — an empty device reference must no-op.

**Files:**
- Modify: `src/Volco/Input/Actions/ActionFactory.cs`, `MuteCurrentOutputAction.cs`, `MuteCurrentInputAction.cs`, `MuteSpecificDeviceAction.cs`, `SetOutputDeviceAction.cs`, `ToggleOutputDeviceAction.cs`
- Modify: `tests/Volco.Tests/Input/Actions/ActionFactoryTests.cs`

**Interfaces:**
- Consumes: `IAudioEngine`, `DeviceRef`, `FakeAudioEngine`, `DeviceRefConfig` (Task 6 — so **run Task 6 before this task** if the implementer prefers; otherwise use `string` here and adjust in Task 6).
- Produces: `ActionFactory.Create(ButtonActionConfig config, IAudioEngine audio, ConfigManager configManager)`.

> **Ordering note:** this task references `ButtonActionConfig.Device` (a `DeviceRefConfig`), which Task 6 creates. See "Execution Order" at the top — Tasks 6 and 7 run before this one.

- [ ] **Step 1: Write the failing test**

Replace the `FakeAudioManager` nested class in `ActionFactoryTests.cs` with `FakeAudioEngine`, and add:

```csharp
[Fact]
public void MuteSpecificDevice_WithEmptyDeviceRef_DoesNothing()
{
    var audio = new FakeAudioEngine();
    var config = new ButtonActionConfig
    {
        Action = ButtonActionType.MuteSpecificDevice,
        Device = new DeviceRefConfig()          // both Id and FriendlyName empty
    };

    ActionFactory.Create(config, audio, _configManager)?.Execute();

    Assert.Empty(audio.ToggleMuteCalls);
}

[Fact]
public void MuteSpecificDevice_WithNullDeviceRef_DoesNothing()
{
    var audio = new FakeAudioEngine();
    var config = new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = null };

    ActionFactory.Create(config, audio, _configManager)?.Execute();

    Assert.Empty(audio.ToggleMuteCalls);
}

[Fact]
public void SetOutputDevice_WithEmptyDeviceRef_DoesNothing()
{
    var audio = new FakeAudioEngine();
    var config = new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = new DeviceRefConfig() };

    ActionFactory.Create(config, audio, _configManager)?.Execute();

    Assert.Empty(audio.SetDefaultCalls);
}

[Fact]
public void MuteSpecificDevice_WithId_TogglesThatDevice()
{
    var audio = new FakeAudioEngine();
    var config = new ButtonActionConfig
    {
        Action = ButtonActionType.MuteSpecificDevice,
        Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Headphones" }
    };

    ActionFactory.Create(config, audio, _configManager)!.Execute();

    Assert.Equal(DeviceRef.ById("dev-1"), Assert.Single(audio.ToggleMuteCalls));
}

[Fact]
public void ToggleOutputDevice_WhenOnDevice1_SwitchesToDevice2()
{
    var audio = new FakeAudioEngine
    {
        DefaultRender = new AudioDevice("dev-1", "Speakers", AudioFlow.Render, true, true, true)
    };
    _configManager.Config.OutputDevices.Device1 = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" };
    _configManager.Config.OutputDevices.Device2 = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" };

    var config = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice };
    ActionFactory.Create(config, audio, _configManager)!.Execute();

    var (endpointId, flow) = Assert.Single(audio.SetDefaultCalls);
    Assert.Equal("dev-2", endpointId);
    Assert.Equal(AudioFlow.Render, flow);
}

[Fact]
public void ToggleOutputDevice_WhenOnNeitherDevice_SwitchesToDevice1()
{
    var audio = new FakeAudioEngine
    {
        DefaultRender = new AudioDevice("dev-99", "Monitor", AudioFlow.Render, true, true, true)
    };
    _configManager.Config.OutputDevices.Device1 = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" };
    _configManager.Config.OutputDevices.Device2 = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" };

    ActionFactory.Create(new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice },
                         audio, _configManager)!.Execute();

    Assert.Equal("dev-1", Assert.Single(audio.SetDefaultCalls).EndpointId);
}
```

- [ ] **Step 2: Run the test loop, verify it fails**

- [ ] **Step 3: Rewrite the actions**

`ActionFactory.cs`:

```csharp
using Volco.Audio;
using Volco.Config;

namespace Volco.Input.Actions;

public static class ActionFactory
{
    public static IButtonAction? Create(ButtonActionConfig config, IAudioEngine audio, ConfigManager configManager)
        => config.Action switch
        {
            ButtonActionType.MuteCurrentOutput  => new MuteCurrentOutputAction(audio),
            ButtonActionType.MuteCurrentInput   => new MuteCurrentInputAction(audio),
            ButtonActionType.MuteSpecificDevice => new MuteSpecificDeviceAction(audio, config.Device),
            ButtonActionType.ToggleOutputDevice => new ToggleOutputDeviceAction(audio, configManager),
            ButtonActionType.SetOutputDevice    => new SetOutputDeviceAction(audio, config.Device),
            _                                   => null
        };

    /// <summary>
    /// Resolves a configured device reference, or null when the config carries neither an
    /// id nor a name — in which case the caller must do nothing rather than guess.
    /// </summary>
    internal static DeviceRef? Resolve(DeviceRefConfig? cfg)
    {
        if (cfg == null) return null;
        if (!string.IsNullOrEmpty(cfg.Id)) return DeviceRef.ById(cfg.Id);
        if (!string.IsNullOrEmpty(cfg.FriendlyName)) return DeviceRef.ByFriendlyName(cfg.FriendlyName);
        return null;
    }
}
```

`MuteCurrentOutputAction.cs`:

```csharp
using Volco.Audio;

namespace Volco.Input.Actions;

public class MuteCurrentOutputAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    public MuteCurrentOutputAction(IAudioEngine audio) => _audio = audio;
    public void Execute()
        => _audio.ToggleDeviceMute(DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia));
}
```

`MuteCurrentInputAction.cs`:

```csharp
using Volco.Audio;

namespace Volco.Input.Actions;

public class MuteCurrentInputAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    public MuteCurrentInputAction(IAudioEngine audio) => _audio = audio;
    public void Execute()
        => _audio.ToggleDeviceMute(DeviceRef.Default(AudioFlow.Capture, AudioRole.Communications));
}
```

`MuteSpecificDeviceAction.cs`:

```csharp
using Volco.Audio;
using Volco.Config;

namespace Volco.Input.Actions;

public class MuteSpecificDeviceAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    private readonly DeviceRef? _device;

    public MuteSpecificDeviceAction(IAudioEngine audio, DeviceRefConfig? device)
    {
        _audio = audio;
        _device = ActionFactory.Resolve(device);
    }

    public void Execute()
    {
        if (_device is not { } device) return;   // unconfigured -> no-op
        _audio.ToggleDeviceMute(device);
    }
}
```

`SetOutputDeviceAction.cs`:

```csharp
using Volco.Audio;
using Volco.Config;

namespace Volco.Input.Actions;

public class SetOutputDeviceAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    private readonly DeviceRefConfig? _device;

    public SetOutputDeviceAction(IAudioEngine audio, DeviceRefConfig? device)
    {
        _audio = audio;
        _device = device;
    }

    public void Execute()
    {
        var id = _device?.Id;
        if (string.IsNullOrEmpty(id)) return;    // unconfigured -> no-op
        _audio.SetDefaultDevice(id, AudioFlow.Render);
    }
}
```

`ToggleOutputDeviceAction.cs`:

```csharp
using Volco.Audio;
using Volco.Config;

namespace Volco.Input.Actions;

public class ToggleOutputDeviceAction : IButtonAction
{
    private readonly IAudioEngine _audio;
    private readonly ConfigManager _configManager;

    public ToggleOutputDeviceAction(IAudioEngine audio, ConfigManager configManager)
    {
        _audio = audio;
        _configManager = configManager;
    }

    public void Execute()
    {
        var devices = _configManager.Config.OutputDevices;
        var current = _audio.GetDefaultDevice(AudioFlow.Render, AudioRole.Multimedia);

        // On device 1 -> go to 2. Anywhere else (including a third device) -> go to 1.
        var onDevice1 = current != null && DeviceMatches(current, devices.Device1);
        var target = onDevice1 ? devices.Device2 : devices.Device1;

        if (string.IsNullOrEmpty(target?.Id)) return;
        _audio.SetDefaultDevice(target.Id, AudioFlow.Render);
    }

    internal static bool DeviceMatches(AudioDevice device, DeviceRefConfig? cfg)
    {
        if (cfg == null) return false;
        if (!string.IsNullOrEmpty(cfg.Id))
            return string.Equals(device.Id, cfg.Id, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(cfg.FriendlyName)
            && string.Equals(device.FriendlyName, cfg.FriendlyName, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run the test loop, verify green**

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Input/Actions/ tests/Volco.Tests/Input/Actions/
git commit -m "refactor(actions): retarget button actions onto IAudioEngine; no-op on unconfigured device"
git push
```

---

### Task 6: Config schema v2

**Files:**
- Create: `src/Volco/Config/DeviceRefConfig.cs`
- Modify: `src/Volco/Config/AppConfig.cs`
- Modify: `src/Volco/Config/ConfigManager.cs` (resilient `Load` — **Review Focus item 1**)
- Create: `src/Volco/UI/ViewModels/DeviceRefLookup.cs`
- Modify: `src/Volco/UI/ViewModels/SliderItemViewModel.cs:136,167-169`
- Modify: `src/Volco/UI/ViewModels/ButtonItemViewModel.cs:69,80-82`
- Modify: `src/Volco/UI/ViewModels/OutputDevicesViewModel.cs:35-36,44-52`
- Modify: `src/Volco/UI/ViewModels/SettingsViewModel.cs` (seed `DeviceRefLookup.Devices`)
- Modify: `tests/Volco.Tests/Config/ConfigManagerTests.cs`

**Interfaces:**
- Produces: `DeviceRefConfig { string Id; string FriendlyName; }`; `AppConfig.CurrentSchemaVersion` (const int = 2); `AppConfig.SchemaVersion` (int); `SliderConfig.Device` (`DeviceRefConfig?`); `SliderConfig.DeviceName` (computed, `[JsonIgnore]`, read-only); `OutputDevicesConfig.Device1/Device2` (`DeviceRefConfig`); `ButtonActionConfig.Device` (`DeviceRefConfig?`); `DeviceRefLookup.DeviceRefFromName(string?)`. Tasks 4, 5, 7, 9 depend on these.
- **Breaks on contact:** the six ViewModel writes listed in Step 6. They are part of this task because a ViewModel that does not compile against the new config is not separately reviewable.

- [ ] **Step 1: Write the failing test**

Add to `tests/Volco.Tests/Config/ConfigManagerTests.cs`:

```csharp
[Fact]
public void Load_CorruptJson_FallsBackToDefaultsInsteadOfThrowing()
{
    var dir = Path.Combine(Path.GetTempPath(), $"volco-corrupt-{Guid.NewGuid()}");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "config.json");
    File.WriteAllText(path, "{ \"connection\": { \"type\": ");   // truncated mid-write

    var cm = new ConfigManager(path);
    var ex = Record.Exception(() => cm.Load());

    Assert.Null(ex);
    Assert.Equal(5, cm.Config.Sliders.Count);
    Assert.Equal(3, cm.Config.Buttons.Count);
}

[Fact]
public void Load_EmptyFile_FallsBackToDefaults()
{
    var dir = Path.Combine(Path.GetTempPath(), $"volco-empty-{Guid.NewGuid()}");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "config.json");
    File.WriteAllText(path, "");

    var cm = new ConfigManager(path);
    cm.Load();

    Assert.Equal(5, cm.Config.Sliders.Count);
}

[Fact]
public void CreateDefault_StampsCurrentSchemaVersion()
{
    Assert.Equal(AppConfig.CurrentSchemaVersion, AppConfig.CreateDefault().SchemaVersion);
}

[Fact]
public void DeviceRefConfig_RoundTripsThroughJson()
{
    var dir = Path.Combine(Path.GetTempPath(), $"volco-roundtrip-{Guid.NewGuid()}");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "config.json");

    var cm = new ConfigManager(path);
    cm.Load();
    cm.Config.OutputDevices.Device1 = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" };
    cm.Save();

    var reloaded = new ConfigManager(path);
    reloaded.Load();

    Assert.Equal("dev-1", reloaded.Config.OutputDevices.Device1.Id);
    Assert.Equal("Speakers", reloaded.Config.OutputDevices.Device1.FriendlyName);
}
```

- [ ] **Step 2: Run the test loop, verify it fails**

- [ ] **Step 3: Create `DeviceRefConfig.cs`**

```csharp
namespace Volco.Config;

/// <summary>
/// A persisted reference to an audio endpoint. <see cref="Id"/> is authoritative;
/// <see cref="FriendlyName"/> is for display and as a fallback when the id does not
/// resolve (a config copied between machines, or migrated from schema v1).
/// </summary>
public class DeviceRefConfig
{
    public string Id { get; set; } = "";
    public string FriendlyName { get; set; } = "";
}
```

- [ ] **Step 4: Modify `AppConfig.cs`**

Add to `AppConfig`:

```csharp
public const int CurrentSchemaVersion = 2;

/// <summary>Absent or 0 means schema v1 (device references stored as bare names).</summary>
public int SchemaVersion { get; set; } = CurrentSchemaVersion;
```

Change the three device-bearing types:

```csharp
public class SliderConfig
{
    public int Index { get; set; }
    public SliderMappingType Type { get; set; } = SliderMappingType.Device;
    public List<string> Processes { get; set; } = new();

    /// <summary>null means "the current default output device".</summary>
    public DeviceRefConfig? Device { get; set; }

    public bool Invert { get; set; }
    public ResponseCurve Curve { get; set; } = ResponseCurve.Linear;
    public double DeadZonePercent { get; set; } = 0;
    public double SmoothingFactor { get; set; } = 0;
}

public class ButtonActionConfig
{
    public ButtonActionType Action { get; set; } = ButtonActionType.None;
    public DeviceRefConfig? Device { get; set; }
}

public class OutputDevicesConfig
{
    public DeviceRefConfig Device1 { get; set; } = new();
    public DeviceRefConfig Device2 { get; set; } = new();
}
```

Update `CreateDefault` to stamp `SchemaVersion = CurrentSchemaVersion`.

Also add a read-only shim so `BuildSliderLabel` and its existing tests keep working unchanged:

```csharp
public partial class SliderConfig
{
    /// <summary>Display name for the configured device; "" means the current default output.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DeviceName => Device?.FriendlyName ?? "";
}
```

(Make `SliderConfig` `partial`, or fold the property into the class body directly — either is fine, but do not serialize it.)

- [ ] **Step 5: Make `ConfigManager.Load` resilient**

```csharp
public void Load()
{
    if (File.Exists(_configPath))
    {
        AppLog.Info($"Loading config from {_configPath}");
        try
        {
            var json = File.ReadAllText(_configPath);
            Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A truncated or hand-edited config must not stop the app from starting.
            AppLog.Error($"Config at {_configPath} could not be read ({ex.GetType().Name}: {ex.Message}); using defaults");
            Config = AppConfig.CreateDefault();
        }
    }
    else
    {
        AppLog.Info("Config file not found, using default config");
        Config = AppConfig.CreateDefault();
    }

    // Defensive: a valid-but-sparse config (hand-edited) must still have entries to index.
    while (Config.Sliders.Count < 5) Config.Sliders.Add(new SliderConfig { Index = Config.Sliders.Count });
    while (Config.Buttons.Count < 3) Config.Buttons.Add(new ButtonConfig { Index = Config.Buttons.Count });

    AppLog.IsDebugEnabled = Config.DebugLogging;
}
```

- [ ] **Step 6: Update the settings ViewModels for the v2 config shape**

**This step is not optional and cannot be deferred.** Six ViewModels write directly to the properties Task 6 just changed. The Ubuntu test job excludes `UI/**`, so **no test will catch this** — it surfaces only on a Windows build.

Files and the exact writes to fix:

| File | Current | Change to |
|---|---|---|
| `UI/ViewModels/SliderItemViewModel.cs:136` | `_deviceName = cfg.DeviceName;` | `_deviceName = cfg.Device?.FriendlyName ?? "";` |
| `UI/ViewModels/SliderItemViewModel.cs:167-169` | `_cfg.DeviceName = value;` | `_cfg.Device = DeviceRefFromName(value);` (helper below) |
| `UI/ViewModels/ButtonItemViewModel.cs:69` | `_deviceId = cfg.DeviceId ?? "";` | `_deviceId = cfg.Device?.FriendlyName ?? "";` |
| `UI/ViewModels/ButtonItemViewModel.cs:80-82` | `_cfg.DeviceId = string.IsNullOrEmpty(value) ? null : value;` | `_cfg.Device = DeviceRefFromName(value);` |
| `UI/ViewModels/OutputDevicesViewModel.cs:35-36` | `_device1 = cfg.Device1;` (string) | `_device1 = cfg.Device1.FriendlyName;` |
| `UI/ViewModels/OutputDevicesViewModel.cs:44-52` | `_config.Config.OutputDevices.Device1 = value;` | `_config.Config.OutputDevices.Device1 = DeviceRefFromName(value) ?? new DeviceRefConfig();` |

The combo boxes bind to `List<string> OutputDeviceNames`, so the ViewModels keep exposing a
`string` to XAML and convert at the config boundary. **No `.xaml` file changes are needed in
this task** — that keeps the blast radius to C#.

Add this shared helper. Put it in `src/Volco/UI/ViewModels/DeviceRefLookup.cs`:

```csharp
using Volco.Audio;
using Volco.Config;

namespace Volco.UI.ViewModels;

/// <summary>
/// Bridges the settings combo boxes (which bind to friendly-name strings) and the v2
/// config (which stores endpoint ids). Resolving here means the id is captured at the
/// moment the user picks a device, rather than being looked up later when the name may
/// no longer match anything.
/// </summary>
internal static class DeviceRefLookup
{
    /// <summary>The device list the ViewModels were constructed with. Set once per VM tree.</summary>
    public static IReadOnlyList<AudioDevice> Devices { get; set; } = Array.Empty<AudioDevice>();

    /// <summary>Null when <paramref name="friendlyName"/> is blank, so "no selection" round-trips.</summary>
    public static DeviceRefConfig? DeviceRefFromName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return null;

        var match = Devices.FirstOrDefault(d =>
            string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

        return match != null
            ? new DeviceRefConfig { Id = match.Id, FriendlyName = match.FriendlyName }
            : new DeviceRefConfig { Id = "", FriendlyName = friendlyName };
    }
}
```

In each ViewModel, add `using static Volco.UI.ViewModels.DeviceRefLookup;` and set
`DeviceRefLookup.Devices` in `SettingsViewModel`'s constructor from the output-device list it
already fetches once (commit `c7b63e5` hoisted that call — keep it hoisted).

- [ ] **Step 7: Run the test loop, verify green**

Ubuntu tests confirm the config changes. Then ask the user to authorize one Windows build run — this is the only thing that proves the ViewModel edits compile:

```bash
gh workflow run build.yml --ref option-d-audio-engine --field run_build=true
```

- [ ] **Step 8: Commit**

```bash
git add src/Volco/Config/ src/Volco/UI/ViewModels/ tests/Volco.Tests/Config/
git commit -m "feat(config): schema v2 with DeviceRefConfig; survive corrupt config files"
git push
```

---

### Task 7: `ConfigMigrator` (v1 → v2)

Includes **Review Focus items 2 and 3**.

**Files:**
- Create: `src/Volco/Config/ConfigMigrator.cs`
- Test: `tests/Volco.Tests/Config/ConfigMigratorTests.cs`

**Interfaces:**
- Consumes: `AppConfig`, `DeviceRefConfig`, `AudioDevice`.
- Produces: `ConfigMigrator.NeedsMigration(AppConfig)`, `ConfigMigrator.Migrate(AppConfig config, IReadOnlyList<AudioDevice> devices)`, `ConfigMigrator.MigrateFile(string configPath, AppConfig config, IReadOnlyList<AudioDevice> devices)`, and `ConfigMigrator.ResolveName(string? name, IReadOnlyList<AudioDevice> devices)`.

> **Migration input note:** v1 JSON used `deviceName` (string) on sliders, `device1`/`device2` (strings) on output devices, and `deviceId` (string, actually a *name*) on button actions. Those properties no longer exist on the v2 types, so `System.Text.Json` silently drops them during `Load`. The migrator therefore reads the **raw JSON**, not the deserialized object.

- [ ] **Step 1: Write the failing test**

Create `tests/Volco.Tests/Config/ConfigMigratorTests.cs`:

```csharp
using System.Text.Json;
using Volco.Audio;
using Volco.Config;
using Xunit;

namespace Volco.Tests.Config;

public class ConfigMigratorTests
{
    private static readonly IReadOnlyList<AudioDevice> Devices = new List<AudioDevice>
    {
        new("dev-1", "Speakers (Realtek High Definition Audio)", AudioFlow.Render, true,  true,  true),
        new("dev-2", "Headphones (USB Audio Device)",            AudioFlow.Render, false, false, false),
    };

    private const string V1Json = """
    {
      "connection": { "type": "Udp", "udp": { "listenPort": 12345 } },
      "sliders": [
        { "index": 0, "type": "Device",    "deviceName": "Headphones", "processes": [] },
        { "index": 1, "type": "Processes", "deviceName": "",           "processes": ["spotify.exe"] }
      ],
      "buttons": [
        { "index": 0, "singlePress": { "action": "MuteSpecificDevice", "deviceId": "Speakers" },
                      "doublePress": { "action": "None" }, "longPress": { "action": "None" } }
      ],
      "outputDevices": { "device1": "Speakers", "device2": "Headphones" },
      "debugLogging": false
    }
    """;

    private static string WriteTempConfig(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"volco-migrate-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void NeedsMigration_TrueWhenSchemaVersionAbsent()
        => Assert.True(ConfigMigrator.NeedsMigration(new AppConfig { SchemaVersion = 0 }));

    [Fact]
    public void NeedsMigration_FalseWhenAlreadyCurrent()
        => Assert.False(ConfigMigrator.NeedsMigration(new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion }));

    [Fact]
    public void ResolveName_PrefersExactFriendlyNameMatch()
    {
        var d = ConfigMigrator.ResolveName("Headphones (USB Audio Device)", Devices);
        Assert.Equal("dev-2", d!.Id);
    }

    [Fact]
    public void ResolveName_FallsBackToSubstringMatch()
    {
        // v1 stored whatever substring the user typed.
        var d = ConfigMigrator.ResolveName("Headphones", Devices);
        Assert.Equal("dev-2", d!.Id);
    }

    [Fact]
    public void ResolveName_UnknownDevice_ReturnsNull()
        => Assert.Null(ConfigMigrator.ResolveName("Nonexistent Soundcard", Devices));

    [Fact]
    public void MigrateFile_PopulatesIdsFromV1Names()
    {
        var path = WriteTempConfig(V1Json);
        var cm = new ConfigManager(path);
        cm.Load();

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        Assert.Equal(AppConfig.CurrentSchemaVersion, cm.Config.SchemaVersion);
        Assert.Equal("dev-2", cm.Config.Sliders[0].Device!.Id);
        Assert.Null(cm.Config.Sliders[1].Device);                 // Processes slider, no device
        Assert.Equal("dev-1", cm.Config.OutputDevices.Device1.Id);
        Assert.Equal("dev-2", cm.Config.OutputDevices.Device2.Id);
        Assert.Equal("dev-1", cm.Config.Buttons[0].SinglePress.Device!.Id);
    }

    [Fact]
    public void MigrateFile_UnresolvableName_KeepsNameAndLeavesIdEmpty()
    {
        var json = V1Json.Replace("\"device1\": \"Speakers\"", "\"device1\": \"Old Soundcard\"");
        var path = WriteTempConfig(json);
        var cm = new ConfigManager(path);
        cm.Load();

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        Assert.Equal("", cm.Config.OutputDevices.Device1.Id);
        Assert.Equal("Old Soundcard", cm.Config.OutputDevices.Device1.FriendlyName);
    }

    [Fact]
    public void MigrateFile_WritesBackupOfTheOriginal()
    {
        var path = WriteTempConfig(V1Json);
        var cm = new ConfigManager(path);
        cm.Load();

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        var backup = Path.Combine(Path.GetDirectoryName(path)!, "config.v1.bak");
        Assert.True(File.Exists(backup));
        Assert.Contains("\"deviceName\": \"Headphones\"", File.ReadAllText(backup));
    }

    [Fact]
    public void MigrateFile_IsIdempotent_AndDoesNotOverwriteAnExistingBackup()
    {
        var path = WriteTempConfig(V1Json);
        var cm = new ConfigManager(path);
        cm.Load();

        ConfigMigrator.MigrateFile(path, cm.Config, Devices);
        var backup = Path.Combine(Path.GetDirectoryName(path)!, "config.v1.bak");
        var firstBackup = File.ReadAllText(backup);

        // Second run: config is now v2, so nothing should happen.
        ConfigMigrator.MigrateFile(path, cm.Config, Devices);

        Assert.Equal(firstBackup, File.ReadAllText(backup));
        Assert.Equal("dev-2", cm.Config.Sliders[0].Device!.Id);
    }

    [Fact]
    public void MigrateFile_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"volco-missing-{Guid.NewGuid()}", "config.json");
        var config = AppConfig.CreateDefault();
        config.SchemaVersion = 0;

        Assert.Null(Record.Exception(() => ConfigMigrator.MigrateFile(path, config, Devices)));
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
    }
}
```

- [ ] **Step 2: Run the test loop, verify it fails**

- [ ] **Step 3: Create `ConfigMigrator.cs`**

```csharp
using System.Text.Json;
using Volco.Audio;

namespace Volco.Config;

/// <summary>
/// Migrates config from schema v1 (device references stored as bare friendly-name
/// substrings) to v2 (endpoint ids, with the name kept for display and fallback).
///
/// Reads the raw JSON rather than the deserialized <see cref="AppConfig"/>, because the
/// v1 property names no longer exist on the v2 types and System.Text.Json drops them
/// silently on load.
/// </summary>
public static class ConfigMigrator
{
    public static bool NeedsMigration(AppConfig config)
        => config.SchemaVersion < AppConfig.CurrentSchemaVersion;

    /// <summary>
    /// Resolves a v1 name to a device: exact friendly-name match first, then the legacy
    /// substring match v1 relied on. Null when nothing matches.
    /// </summary>
    public static DeviceRefConfig? ResolveName(string? name, IReadOnlyList<AudioDevice> devices)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var exact = devices.FirstOrDefault(d =>
            string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return new DeviceRefConfig { Id = exact.Id, FriendlyName = exact.FriendlyName };

        var partial = devices.FirstOrDefault(d =>
            d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (partial != null)
            return new DeviceRefConfig { Id = partial.Id, FriendlyName = partial.FriendlyName };

        return null;
    }

    /// <summary>Unresolvable names keep the name so the UI can prompt for reselection.</summary>
    private static DeviceRefConfig Preserve(string? name, IReadOnlyList<AudioDevice> devices)
        => ResolveName(name, devices) ?? new DeviceRefConfig { Id = "", FriendlyName = name ?? "" };

    /// <summary>
    /// Backs up the v1 file, applies the migration to <paramref name="config"/> in place,
    /// and stamps the new schema version. Caller saves. No-op when already current.
    /// </summary>
    public static void MigrateFile(string configPath, AppConfig config, IReadOnlyList<AudioDevice> devices)
    {
        if (!NeedsMigration(config)) return;

        try
        {
            if (File.Exists(configPath))
            {
                var raw = File.ReadAllText(configPath);

                var dir = Path.GetDirectoryName(configPath);
                if (dir != null)
                {
                    var backup = Path.Combine(dir, "config.v1.bak");
                    if (!File.Exists(backup)) File.WriteAllText(backup, raw);
                }

                Migrate(config, raw, devices);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Config migration could not read {configPath} ({ex.GetType().Name}: {ex.Message}); stamping v2 with defaults");
        }

        config.SchemaVersion = AppConfig.CurrentSchemaVersion;
    }

    /// <summary>Applies v1 device names from <paramref name="rawV1Json"/> onto <paramref name="config"/>.</summary>
    internal static void Migrate(AppConfig config, string rawV1Json, IReadOnlyList<AudioDevice> devices)
    {
        using var doc = JsonDocument.Parse(rawV1Json);
        var root = doc.RootElement;

        if (root.TryGetProperty("sliders", out var sliders) && sliders.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var slider in sliders.EnumerateArray())
            {
                if (i >= config.Sliders.Count) break;
                var name = slider.TryGetProperty("deviceName", out var dn) ? dn.GetString() : null;
                if (config.Sliders[i].Type == SliderMappingType.Device && !string.IsNullOrWhiteSpace(name))
                    config.Sliders[i].Device = Preserve(name, devices);
                i++;
            }
        }

        if (root.TryGetProperty("outputDevices", out var od))
        {
            if (od.TryGetProperty("device1", out var d1))
                config.OutputDevices.Device1 = Preserve(d1.GetString(), devices);
            if (od.TryGetProperty("device2", out var d2))
                config.OutputDevices.Device2 = Preserve(d2.GetString(), devices);
        }

        if (root.TryGetProperty("buttons", out var buttons) && buttons.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var button in buttons.EnumerateArray())
            {
                if (i >= config.Buttons.Count) break;
                MigrateAction(button, "singlePress", config.Buttons[i].SinglePress, devices);
                MigrateAction(button, "doublePress", config.Buttons[i].DoublePress, devices);
                MigrateAction(button, "longPress",   config.Buttons[i].LongPress,   devices);
                i++;
            }
        }
    }

    private static void MigrateAction(JsonElement button, string property,
        ButtonActionConfig target, IReadOnlyList<AudioDevice> devices)
    {
        if (!button.TryGetProperty(property, out var action)) return;
        if (!action.TryGetProperty("deviceId", out var idElement)) return;

        var name = idElement.GetString();
        if (!string.IsNullOrWhiteSpace(name))
            target.Device = Preserve(name, devices);
    }
}
```

- [ ] **Step 4: Run the test loop, verify green**

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Config/ConfigMigrator.cs tests/Volco.Tests/Config/ConfigMigratorTests.cs
git commit -m "feat(config): migrate v1 name-based device refs to v2 endpoint ids"
git push
```

---

### Task 8: Wire the migrator into startup

**Files:**
- Modify: `src/Volco/App.xaml.cs` (Windows-only — CI build job verifies compilation; see Step 3)

**Interfaces:**
- Consumes: `ConfigMigrator`, `IAudioEngine`, `LegacyAudioEngineAdapter`.
- Produces: nothing new.

- [ ] **Step 1: Update `OnLaunched`**

Replace the config + audio block in `OnLaunched` with:

```csharp
var configPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Volco", "config.json");
_configManager = new ConfigManager(configPath);
_configManager.Load();

// Audio first — the migrator needs the device list to resolve v1 names to endpoint ids.
_audioManager = new AudioManager();
_audioEngine = new LegacyAudioEngineAdapter(_audioManager);

if (ConfigMigrator.NeedsMigration(_configManager.Config))
{
    AppLog.Info("Migrating config to schema v2");
    ConfigMigrator.MigrateFile(configPath, _configManager.Config,
                               _audioEngine.GetDevices(AudioFlow.Render));
    _configManager.Save();
}

_configManager.ConnectionConfigChanged += OnConfigChanged;

_buttonPressDetector = new ButtonPressDetector();
_buttonPressDetector.PressDetected += OnButtonPressDetected;
_sliderProcessor = new SliderProcessor(_audioEngine, _configManager);
```

Update the fields: replace `private IAudioManager _audioManager` usages with `private IAudioEngine _audioEngine` where the consumers are `SliderProcessor`, `ActionFactory`, and `SettingsWindow`. Keep `_audioManager` only as the concrete backing object for the adapter. Remove `_sessionMonitor` entirely, including from `Shutdown()`.

- [ ] **Step 2: Update the remaining call sites in `App.xaml.cs`**

- `OnButtonPressDetected`: `ActionFactory.Create(actionConfig, _audioEngine, _configManager)`.
- `ShowMuteOsd`: replace `_audioManager.GetDefaultOutputDeviceName()` with
  `_audioEngine.GetDefaultDevice(AudioFlow.Render, AudioRole.Multimedia)?.FriendlyName ?? "Output"`,
  and each `IsOutputMuted(...)` / `IsInputMuted()` with
  `_audioEngine.TryGetDeviceState(<ref>, out _, out var muted) && muted`.
  Use `ActionFactory.Resolve(actionConfig.Device)` for the specific-device case; when it
  returns null, skip the OSD entirely.
- `ShowSettings`: pass `_audioEngine` to `SettingsWindow`.

- [ ] **Step 3: Verify the Windows build compiles**

The Ubuntu test job excludes `App.xaml.cs`, so it cannot catch errors here. Ask the user to authorize one build run (per `CLAUDE.md`, never trigger it unprompted):

```bash
gh workflow run build.yml --ref option-d-audio-engine --field run_build=true
gh run list --branch option-d-audio-engine --limit 1
```

Expected: both jobs green.

- [ ] **Step 4: Commit**

```bash
git add src/Volco/App.xaml.cs
git commit -m "feat(app): run config migration at startup; wire IAudioEngine through"
git push
```

---

### Task 9: `ControllerStateReporter`

Includes **Review Focus item 4**.

**Files:**
- Create: `src/Volco/Communication/ControllerStateReporter.cs`
- Test: `tests/Volco.Tests/Communication/ControllerStateReporterTests.cs`
- Modify: `src/Volco/App.xaml.cs`

**Interfaces:**
- Consumes: `IAudioEngine`, `AppConfig`, `ProtocolEncoder`.
- Produces: `ControllerStateReporter.BuildFrames(AppConfig config, IAudioEngine audio)` returning `IReadOnlyList<byte[]>`; `ControllerStateReporter.ResolveActiveDeviceIndex(AudioDevice? current, OutputDevicesConfig devices)` returning `int?`; `ControllerStateReporter.GetMuteStateForButton(ButtonConfig, IAudioEngine)` returning `bool?`.

- [ ] **Step 1: Write the failing test**

Create `tests/Volco.Tests/Communication/ControllerStateReporterTests.cs`:

```csharp
using Volco.Audio;
using Volco.Communication;
using Volco.Config;
using Volco.Tests.Audio;
using Xunit;

namespace Volco.Tests.Communication;

public class ControllerStateReporterTests
{
    private static OutputDevicesConfig Pair() => new()
    {
        Device1 = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" },
        Device2 = new DeviceRefConfig { Id = "dev-2", FriendlyName = "Headphones" },
    };

    private static AudioDevice Dev(string id, string name)
        => new(id, name, AudioFlow.Render, true, true, true);

    [Fact]
    public void ResolveActiveDeviceIndex_OnDevice1_ReturnsZero()
        => Assert.Equal(0, ControllerStateReporter.ResolveActiveDeviceIndex(Dev("dev-1", "Speakers"), Pair()));

    [Fact]
    public void ResolveActiveDeviceIndex_OnDevice2_ReturnsOne()
        => Assert.Equal(1, ControllerStateReporter.ResolveActiveDeviceIndex(Dev("dev-2", "Headphones"), Pair()));

    [Fact]
    public void ResolveActiveDeviceIndex_OnNeitherDevice_ReturnsNull()
    {
        // The bug this class exists to fix: today a third device reports as device 2.
        Assert.Null(ControllerStateReporter.ResolveActiveDeviceIndex(Dev("dev-99", "Monitor"), Pair()));
    }

    [Fact]
    public void ResolveActiveDeviceIndex_NoCurrentDevice_ReturnsNull()
        => Assert.Null(ControllerStateReporter.ResolveActiveDeviceIndex(null, Pair()));

    [Fact]
    public void ResolveActiveDeviceIndex_FallsBackToFriendlyNameWhenIdIsBlank()
    {
        var pair = new OutputDevicesConfig
        {
            Device1 = new DeviceRefConfig { Id = "", FriendlyName = "Speakers" },
            Device2 = new DeviceRefConfig { Id = "", FriendlyName = "Headphones" },
        };
        Assert.Equal(1, ControllerStateReporter.ResolveActiveDeviceIndex(Dev("whatever", "Headphones"), pair));
    }

    [Fact]
    public void BuildFrames_OnNeitherDevice_EmitsNoActiveDeviceFrame()
    {
        var config = AppConfig.CreateDefault();
        config.OutputDevices = Pair();
        var audio = new FakeAudioEngine
        {
            DefaultRender = Dev("dev-99", "Monitor")
        };

        var frames = ControllerStateReporter.BuildFrames(config, audio);

        Assert.DoesNotContain(frames, f => f[2] == (byte)MessageType.ActiveDevice);
    }

    [Fact]
    public void BuildFrames_EmitsMuteStateForMuteButtons()
    {
        var config = AppConfig.CreateDefault();
        config.OutputDevices = Pair();
        config.Buttons[0].SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };

        var audio = new FakeAudioEngine { DefaultRender = Dev("dev-1", "Speakers") };
        audio.DeviceStates["dev-1"] = (0.5f, true);

        var frames = ControllerStateReporter.BuildFrames(config, audio);

        var mute = Assert.Single(frames, f => f[2] == (byte)MessageType.MuteState);
        Assert.Equal(0, mute[3]);       // button index
        Assert.Equal(1, mute[4]);       // muted
    }

    [Fact]
    public void BuildFrames_ButtonWithNoMuteAction_EmitsNoMuteFrame()
    {
        var config = AppConfig.CreateDefault();
        config.OutputDevices = Pair();
        config.Buttons[0].SinglePress = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice };
        config.Buttons[1].SinglePress = new ButtonActionConfig { Action = ButtonActionType.None };

        var audio = new FakeAudioEngine { DefaultRender = Dev("dev-1", "Speakers") };

        var frames = ControllerStateReporter.BuildFrames(config, audio);

        Assert.DoesNotContain(frames, f => f[2] == (byte)MessageType.MuteState);
    }
}
```

- [ ] **Step 2: Run the test loop, verify it fails**

- [ ] **Step 3: Create `ControllerStateReporter.cs`**

```csharp
using Volco.Audio;
using Volco.Config;

namespace Volco.Communication;

/// <summary>
/// Computes the state frames pushed to the controller (mute LEDs, active-device LED).
/// Pure and side-effect free so it can be tested without a transport or real audio.
/// </summary>
public static class ControllerStateReporter
{
    /// <summary>Mute LEDs exist for buttons 0 and 1 only.</summary>
    private const int MuteButtonCount = 2;

    public static IReadOnlyList<byte[]> BuildFrames(AppConfig config, IAudioEngine audio)
    {
        var frames = new List<byte[]>();

        for (int i = 0; i < Math.Min(MuteButtonCount, config.Buttons.Count); i++)
        {
            var muted = GetMuteStateForButton(config.Buttons[i], audio);
            if (muted.HasValue)
                frames.Add(ProtocolEncoder.EncodeMuteState(i, muted.Value));
        }

        var current = audio.GetDefaultDevice(AudioFlow.Render, AudioRole.Multimedia);
        var index = ResolveActiveDeviceIndex(current, config.OutputDevices);
        if (index.HasValue)
            frames.Add(ProtocolEncoder.EncodeActiveDevice(index.Value));

        return frames;
    }

    /// <summary>
    /// 0 or 1 when the current default output is one of the configured toggle devices;
    /// null when it is neither, so the caller sends nothing rather than lighting the
    /// wrong LED.
    /// </summary>
    public static int? ResolveActiveDeviceIndex(AudioDevice? current, OutputDevicesConfig devices)
    {
        if (current == null) return null;
        if (Matches(current, devices.Device1)) return 0;
        if (Matches(current, devices.Device2)) return 1;
        return null;
    }

    private static bool Matches(AudioDevice device, DeviceRefConfig? cfg)
    {
        if (cfg == null) return false;
        if (!string.IsNullOrEmpty(cfg.Id))
            return string.Equals(device.Id, cfg.Id, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(cfg.FriendlyName)
            && string.Equals(device.FriendlyName, cfg.FriendlyName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The mute state of the first mute-type action on the button, or null if it has none.</summary>
    public static bool? GetMuteStateForButton(ButtonConfig button, IAudioEngine audio)
    {
        foreach (var action in new[] { button.SinglePress, button.DoublePress, button.LongPress })
        {
            switch (action.Action)
            {
                case ButtonActionType.MuteCurrentOutput:
                    return Muted(audio, DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia));

                case ButtonActionType.MuteCurrentInput:
                    return Muted(audio, DeviceRef.Default(AudioFlow.Capture, AudioRole.Communications));

                case ButtonActionType.MuteSpecificDevice:
                    var reference = Input.Actions.ActionFactory.Resolve(action.Device);
                    if (reference is not { } r) return null;
                    return Muted(audio, r);
            }
        }
        return null;
    }

    private static bool Muted(IAudioEngine audio, DeviceRef device)
        => audio.TryGetDeviceState(device, out _, out var muted) && muted;
}
```

- [ ] **Step 4: Run the test loop, verify green**

- [ ] **Step 5: Replace `PushControllerState` in `App.xaml.cs`**

```csharp
private void PushControllerState()
{
    if (_connection?.Status != ConnectionStatus.Connected) return;

    foreach (var frame in ControllerStateReporter.BuildFrames(_configManager.Config, _audioEngine))
        _connection.Send(frame);
}
```

Delete the private `GetMuteStateForButton` method from `App.xaml.cs` — it now lives in the reporter.

- [ ] **Step 6: Commit**

```bash
git add src/Volco/Communication/ControllerStateReporter.cs \
        tests/Volco.Tests/Communication/ControllerStateReporterTests.cs src/Volco/App.xaml.cs
git commit -m "fix(controller): report no active-device frame when on neither configured device"
git push
```

---

## Phase 4 — the real engine

> **Verification model changes here.** The Core Audio engine needs Windows and real
> audio hardware, so CI cannot test it — CI only proves it *compiles*. Correctness comes
> from the shared spec §7 manual checklist and §8 measurements, run by the user on their
> machine. Tasks 10–13 therefore have no red/green TDD cycle; they end with a Windows
> build check and a commit. Task 14 is where it becomes observable.

---

### Task 10: CsWin32 setup and the COM surface

**Files:**
- Create: `src/Volco/NativeMethods.txt`
- Modify: `src/Volco/Volco.csproj`

- [ ] **Step 1: Add the CsWin32 package reference**

In the Windows-only `ItemGroup` of `Volco.csproj`:

```xml
<PackageReference Include="Microsoft.Windows.CsWin32" Version="0.3.106">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

Verify the current version on nuget.org before pinning.

- [ ] **Step 2: Create `src/Volco/NativeMethods.txt`**

```
IMMDeviceEnumerator
IMMDevice
IMMDeviceCollection
IMMNotificationClient
IAudioEndpointVolume
IAudioEndpointVolumeCallback
IAudioSessionManager2
IAudioSessionControl
IAudioSessionControl2
IAudioSessionEnumerator
IAudioSessionNotification
IAudioSessionEvents
ISimpleAudioVolume
MMDeviceEnumerator
EDataFlow
ERole
DEVICE_STATE_ACTIVE
AudioSessionState
STGM
PROPERTYKEY
IPropertyStore
CoCreateInstance
CoInitializeEx
CoUninitialize
OpenProcess
QueryFullProcessImageNameW
CloseHandle
PROCESS_QUERY_LIMITED_INFORMATION
```

Add `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` to the Windows `PropertyGroup` if the generator requires it.

- [ ] **Step 3: Add the `Audio/Windows` exclusion for non-Windows builds**

In the non-Windows `ItemGroup` of `Volco.csproj`, alongside the existing removals:

```xml
<Compile Remove="Audio/Windows/**/*.cs" />
```

- [ ] **Step 4: Verify both CI jobs compile**

Ask the user to authorize a build run (`--field run_build=true`). Expected: Ubuntu tests green, Windows build green.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/NativeMethods.txt src/Volco/Volco.csproj
git commit -m "build: add CsWin32 and the Core Audio interop surface"
git push
```

---

### Task 11: `DeviceRegistry`

**Files:**
- Create: `src/Volco/Audio/Windows/DeviceRegistry.cs`

**Interfaces:**
- Produces: `DeviceRegistry(Action<DevicesChangedEventArgs> onChanged) : IDisposable` with `void Start()`, `IReadOnlyList<AudioDevice> GetDevices(AudioFlow)`, `AudioDevice? GetDefault(AudioFlow, AudioRole)`, `DeviceHandle? Resolve(DeviceRef)`, and a nested `sealed class DeviceHandle { string Id; string FriendlyName; IMMDevice Device; IAudioEndpointVolume Volume; IAudioSessionManager2 SessionManager; }`. Tasks 12–13 consume `Resolve` and `DeviceHandle`.

- [ ] **Step 1: Implement the registry**

Structure (the implementer fills in the CsWin32 call bodies; the contract is fixed):

```csharp
namespace Volco.Audio.Windows;

/// <summary>
/// Owns every live IMMDevice and its IAudioEndpointVolume / IAudioSessionManager2.
/// Built once at Start(), kept current by IMMNotificationClient. Nothing outside this
/// class calls EnumerateAudioEndpoints or GetDefaultAudioEndpoint.
///
/// All methods must be called on the AudioEngine's audio thread (MTA).
/// </summary>
internal sealed class DeviceRegistry : IDisposable
{
    /// <summary>One cached endpoint and the COM objects hanging off it. Created once, disposed once.</summary>
    internal sealed class DeviceHandle : IDisposable
    {
        public required string    Id            { get; init; }   // IMMDevice::GetId
        public required string    FriendlyName  { get; init; }   // PKEY_Device_FriendlyName
        public required AudioFlow Flow          { get; init; }
        public required IMMDevice Device        { get; init; }

        /// <summary>Created with the handle; its change callback keeps <see cref="CachedLevel"/> fresh.</summary>
        public required IAudioEndpointVolume Volume { get; init; }

        /// <summary>Render devices only; null for capture endpoints.</summary>
        public IAudioSessionManager2? SessionManager { get; init; }

        /// <summary>Last known state, so TryGetDeviceState never blocks on the audio thread.</summary>
        public float CachedLevel { get; set; }
        public bool  CachedMuted { get; set; }

        public void Dispose();   // UnregisterControlChangeNotify, then release every interface
    }

    public DeviceRegistry(Action<DevicesChangedEventArgs> onChanged);

    public void Start();                                   // enumerate once, register IMMNotificationClient
    public IReadOnlyList<AudioDevice> GetDevices(AudioFlow flow);
    public AudioDevice? GetDefault(AudioFlow flow, AudioRole role);
    public DeviceHandle? Resolve(DeviceRef reference);     // Id -> FriendlyName -> Default, in that order
    public void Dispose();                                 // unregister, release every handle
}
```

Required behaviors, each traceable to the shared spec:

1. `Start()` enumerates `DEVICE_STATE_ACTIVE` endpoints for both flows, builds a `DeviceHandle` each, then registers the notification client. **Register from MTA** (§2.1).
2. `IMMNotificationClient` callbacks **only post to the audio thread queue and return `S_OK`** — never touch the registry inline (§2.1).
3. One `IAudioEndpointVolume` per device, created with the handle, released with it (§2.3 invariant 2).
4. `Resolve` order: `ById` → exact `FriendlyName` → `Default`. An unresolvable `ById` falls through to `FriendlyName` when the ref carries one; otherwise returns null.
5. `OnDefaultDeviceChanged` updates the cached default and raises `DeviceChangeKind.DefaultChanged`.
6. `OnDeviceRemoved` / `OnDeviceStateChanged` to a non-active state disposes and drops the handle.

- [ ] **Step 2: Verify the Windows build compiles** (authorize a build run)

- [ ] **Step 3: Commit**

```bash
git add src/Volco/Audio/Windows/DeviceRegistry.cs
git commit -m "feat(audio): device registry with IMMNotificationClient-driven cache"
git push
```

---

### Task 12: `SessionRegistry`

**Files:**
- Create: `src/Volco/Audio/Windows/SessionRegistry.cs`

**Interfaces:**
- Produces: `SessionRegistry(Action<SessionsChangedEventArgs> onChanged) : IDisposable` with `void Attach(DeviceRegistry.DeviceHandle device)`, `void Detach(string deviceId)`, `IReadOnlyList<AudioSessionInfo> GetSessions()`, `void ApplyVolume(SessionFilter filter, float level)`.

- [ ] **Step 1: Implement the registry**

```csharp
namespace Volco.Audio.Windows;

/// <summary>
/// Owns every live audio session across ALL active render devices — not just the default
/// one — so an app playing to the secondary headset is still controlled.
///
/// All methods must be called on the AudioEngine's audio thread (MTA).
/// </summary>
internal sealed class SessionRegistry : IDisposable
{
    public SessionRegistry(Action<SessionsChangedEventArgs> onChanged);

    public void Attach(DeviceRegistry.DeviceHandle device);   // enumerate once, then register
    public void Detach(string deviceId);
    public IReadOnlyList<AudioSessionInfo> GetSessions();
    public void ApplyVolume(SessionFilter filter, float level);
    public void Dispose();
}
```

Required behaviors:

1. **`Attach` must call `GetSessionEnumerator()` before `RegisterSessionNotification()`.** Without the enumerate, registration silently delivers nothing (shared spec §2.4). This is the single most likely thing to get wrong.
2. One `ISimpleAudioVolume` per session, created on `OnSessionCreated`, released when `OnStateChanged` reports `AudioSessionStateExpired` or `OnSessionDisconnected` fires (§2.3 invariant 3).
3. Session key is **`GetSessionInstanceIdentifier`**, not `GetSessionIdentifier` (§2.3 invariant 7).
4. Image name resolved **once**, at session creation: `GetProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `QueryFullProcessImageNameW` → `Path.GetFileName`. Never `Process.GetProcessById` (§2.3 invariant 4). Use `IsSystemSoundsSession` for the PID-0 case.
5. `ApplyVolume` walks the in-memory dictionary — **no enumeration** — and writes `ISimpleAudioVolume::SetMasterVolume(level, ref EventContext)`.
6. Callbacks post to the audio thread and return `S_OK` immediately (§2.1).
7. Treat `IAudioSessionNotification` as authoritative; do not re-enumerate to confirm (§2.4).

- [ ] **Step 2: Verify the Windows build compiles** (authorize a build run)

- [ ] **Step 3: Commit**

```bash
git add src/Volco/Audio/Windows/SessionRegistry.cs
git commit -m "feat(audio): session registry across all render devices, event-driven"
git push
```

---

### Task 13: `DefaultDevicePolicy` and `AudioEngine`

**Files:**
- Create: `src/Volco/Audio/Windows/DefaultDevicePolicy.cs`
- Create: `src/Volco/Audio/Windows/AudioEngine.cs`

**Interfaces:**
- Produces: `AudioEngine : IAudioEngine` with a parameterless constructor and a `Start()` method.

- [ ] **Step 1: Implement `DefaultDevicePolicy`**

```csharp
namespace Volco.Audio.Windows;

/// <summary>
/// Sets the Windows default endpoint via the undocumented IPolicyConfig COM interface.
/// There is no public API for this; every tool in this space uses the same route.
/// Failures are logged and swallowed — an OS change here must not take the app down.
/// </summary>
internal sealed class DefaultDevicePolicy
{
    // CLSID_CPolicyConfigClient
    private static readonly Guid ClsidPolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    // IID_IPolicyConfig (Win7+). The Vista IID {568b9108-...} has a DIFFERENT vtable — not interchangeable.
    private static readonly Guid IidPolicyConfig = new("f8679f50-850a-41cf-9c72-430f290290c8");

    /// <summary>Sets the endpoint as default for Console, Multimedia and Communications.</summary>
    public bool TrySetDefault(string endpointId);
}
```

**Verify the vtable slot order against a current reference `PolicyConfig.h` before relying on it** — `SetDefaultEndpoint` is not the first method. Declare the interface with `[ComImport]`/`[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]` and placeholder methods in the correct slots.

- [ ] **Step 2: Implement `AudioEngine`**

```csharp
namespace Volco.Audio.Windows;

/// <summary>
/// The real Core Audio engine. Owns one dedicated MTA thread; every COM activation,
/// registration and write happens on it. Callers post work and never touch COM.
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    /// <summary>Process-wide context passed to every volume write, so our own change
    /// notifications can be recognised and ignored (shared spec §2.3 invariant 6).</summary>
    internal static readonly Guid EventContext = Guid.NewGuid();

    public AudioEngine();
    public void Start();     // starts the thread, CoInitializeEx(MTA), registry.Start()
    // ... IAudioEngine members, each posting to the work queue ...
}
```

Required behaviors:

1. One dedicated thread, `CoInitializeEx(COINIT_MULTITHREADED)` at start, `CoUninitialize` at shutdown (§2.1).
2. Work queue is a `Channel<AudioCommand>` where `AudioCommand` is a **struct**, not a closure — the hot path must not allocate per tick (§2.1).
3. `SetDeviceVolume` / `SetSessionVolume` post and return immediately.
4. `TryGetDeviceState` reads a cached snapshot updated by `IAudioEndpointVolumeCallback` — it must **not** block on the audio thread, because the OSD and `ControllerStateReporter` call it from the UI thread.
5. `SetDefaultDevice` delegates to `DefaultDevicePolicy` and logs failures.
6. `Dispose` drains the queue, unregisters every callback, releases every handle, then `CoUninitialize`.

- [ ] **Step 3: Verify the Windows build compiles** (authorize a build run)

- [ ] **Step 4: Commit**

```bash
git add src/Volco/Audio/Windows/
git commit -m "feat(audio): Core Audio engine with dedicated MTA thread and IPolicyConfig"
git push
```

---

### Task 14: Swap in the real engine and delete the old layer

**Files:**
- Modify: `src/Volco/App.xaml.cs`
- Modify: `src/Volco/UI/SettingsWindow.xaml.cs` and all six of `src/Volco/UI/ViewModels/*ViewModel.cs` (Step 2)
- Delete: `src/Volco/Audio/AudioManager.cs`, `src/Volco/Audio/SessionMonitor.cs`, `src/Volco/Audio/IAudioManager.cs`, `src/Volco/Audio/LegacyAudioEngineAdapter.cs`, `src/Volco/Tools/svcl.exe`
- Delete: `tests/Volco.Tests/Audio/LegacyAudioEngineAdapterTests.cs`
- Modify: `src/Volco/Volco.csproj` (drop `NAudio`, drop the `svcl.exe` `<Content Include>`)
- Modify: `README.md`

- [ ] **Step 1: Swap the construction in `App.xaml.cs`**

```csharp
var engine = new Volco.Audio.Windows.AudioEngine();
engine.Start();
_audioEngine = engine;
```

Remove the `_audioManager` field entirely.

- [ ] **Step 2: Retarget the ViewModels off `IAudioManager` — do this BEFORE the deletes**

Deleting `IAudioManager.cs` also deletes `AudioDeviceInfo`, and six ViewModels plus
`SettingsWindow` reference both. Again, the Ubuntu job excludes `UI/**`, so **only a Windows
build catches a mistake here.**

Mechanical substitutions across `src/Volco/UI/`:

| Current | Replace with |
|---|---|
| `IAudioManager _audio` (field/ctor param) in `SettingsViewModel`, `SlidersViewModel`, `ButtonsViewModel`, `OutputDevicesViewModel`, `SliderItemViewModel`, `ButtonItemViewModel` | `IAudioEngine _audio` |
| `IReadOnlyList<AudioDeviceInfo> outputDevices` (ctor params) | `IReadOnlyList<AudioDevice> outputDevices` |
| `_audio.GetOutputDevices()` | `_audio.GetDevices(AudioFlow.Render)` |
| `_audio.GetInputDevices()` | `_audio.GetDevices(AudioFlow.Capture)` |
| `.Select(d => d.Name)` | `.Select(d => d.FriendlyName)` |
| `SettingsWindow(ConfigManager, IAudioManager, ...)` | `SettingsWindow(ConfigManager, IAudioEngine, ...)` |

`ButtonItemViewModel`'s `TestCommand` calls `ActionFactory.Create(...)`, whose signature
already changed in Task 5 — pass `_audio` (now an `IAudioEngine`) and it compiles unchanged.

Keep the hoisted single `GetDevices` call per tab that commit `c7b63e5` introduced. Do not
reintroduce a per-item lookup; with the new engine it is a cheap registry read, but the
hoist is still the right shape.

- [ ] **Step 3: Delete the old files and dependencies**

```bash
git rm src/Volco/Audio/AudioManager.cs src/Volco/Audio/SessionMonitor.cs \
       src/Volco/Audio/IAudioManager.cs src/Volco/Audio/LegacyAudioEngineAdapter.cs \
       src/Volco/Tools/svcl.exe tests/Volco.Tests/Audio/LegacyAudioEngineAdapterTests.cs
```

Remove from `Volco.csproj`: the `NAudio` `PackageReference` and the `<Content Include="Tools\svcl.exe">` item group.

- [ ] **Step 4: Fix the README**

The Tech Stack section claims `COM IPolicyConfig` is in use for device switching — which only becomes true with this task. Also remove the NAudio line and update the architecture diagram's Audio Layer box to `AudioEngine (Core Audio, event-driven)`.

- [ ] **Step 5: Run the test loop, verify green**

Every remaining test must pass. Then authorize a build run and confirm the Windows job is green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(audio): replace AudioManager with the Core Audio engine; drop NAudio and svcl.exe"
git push
```

---

### Task 15: Measure

**Files:**
- Create: `MEASUREMENTS.md`

- [ ] **Step 1: Ask the user to produce a `main` baseline**

They need a build of `main` (pre-change) running on their Windows machine with the controller attached, then the full shared spec §8 protocol: idle CPU/GPU for 60 s, sustained 5-slider sweep for 60 s, handle count before/after, cold start, idle working set.

- [ ] **Step 2: Ask the user to run the same protocol on this branch**

Requires a build artifact from the branch:

```bash
gh workflow run build.yml --ref option-d-audio-engine --field run_build=true
```

- [ ] **Step 3: Run the shared spec §7 Windows manual checklist**

- Unplug the active USB headset mid-sweep → no exception, recovers on re-plug.
- Rename a device in Sound Settings → mapping survives.
- Start Spotify while its slider sits at 30 % → it starts at 30 %.
- Play audio to the non-default device → its slider still controls it.
- Switch default device via a button → all three roles change (Sound Settings → Advanced).
- A v1 `config.json` migrates silently and `config.v1.bak` appears.
- The simulator (`tools/simulator/index.html`) still drives the app, LEDs included.

- [ ] **Step 4: Record results in `MEASUREMENTS.md`**

Table with a row per metric and columns `main baseline` / `after phase 4`. Reference targets: idle CPU < 0.1 % average, idle GPU 0, **handle count flat** across the sweep, cold start < 1 s.

- [ ] **Step 5: Commit**

```bash
git add MEASUREMENTS.md
git commit -m "docs: phase 4 measurements against the main baseline"
git push
```

- [ ] **Step 6: Decision gate**

Review the numbers with the user before starting the phases 5–7 plan. If idle CPU and GPU are already where they want them, phases 5–6 become optional polish rather than fixes — and the Option A question changes shape. That conversation is the point of this branch.

---

## Notes for the executor

- **Task order is 1, 2, 3, 6, 7, 4, 5, 8, 9, 10–15** — see "Execution Order" at the top. Tasks 4 and 5 both depend on `DeviceRefConfig` from Task 6.
- **`UI/**` and `App.xaml.cs` are excluded from the Ubuntu test build, so no test can catch a break in them.** This is the single biggest hazard in this plan: Tasks 6, 8, 9 and 14 all edit code the test job cannot see. Each ends with an authorized Windows build run — do not skip those, and do not batch several tasks before running one.
- The three tasks that touch the ViewModels are **6** (v2 config shape), **14** (off `IAudioManager`), and nothing else. If a fourth task finds itself editing a ViewModel, something upstream was missed.
- When a CI test run is red, `gh run view <id> --log-failed` gives the xUnit output directly.
- Do not add `svcl.exe` back for any reason. If `IPolicyConfig` misbehaves, log and surface a UI error — the spec requires failures there to be non-fatal.
