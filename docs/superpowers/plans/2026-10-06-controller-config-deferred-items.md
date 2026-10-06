# Controller Config Deferred Items Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the deferred review findings in spec §14.1, except the install speed, which the user handles separately. Also carry out the user's LGPL decision (spec §14, point 3): option C, a relink bundle in every release's firmware zip.

**Architecture:** Nine small, independent fixes across the firmware tools, `Volco.Core`, `Volco.App` and CI.
- One row of §14.1 is obsolete: the firmware starts UDP once per boot and every config save reboots, so a restart can no longer interrupt a Wi-Fi connect. Task 1 closes that row with the evidence.
- Each task deletes its own row from §14.1 when it lands.

**Tech Stack:** C++17/PlatformIO (native tests), Python 3 (vector generator, build scripts), .NET 8 with xUnit 2 (Core) and xUnit v3 with Avalonia headless (App), GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-03-controller-config-design.md` §14.1, and the sections each task cites.

## Global Constraints

- **Local tools:** no local `dotnet`: C# is verified by pushing the branch (CI runs tests on every push). Never trigger the build job unless the user asks.
- **Git:** use `/usr/bin/git`; write files with the Write tool, not heredocs.
- **No backward compatibility:** old firmware, protocol or config gets no special handling (single user).
- **Platform-free:** `Volco.App` and `Volco.Core` stay platform-free (`ArchitectureTests`).
- **Threading:** runtime and service events are marshalled with `IUiDispatcher.Post`, never Invoke.
- **Branch:** `controller-config-deferred`, worktree `.claude/worktrees/deferred`; one PR into `main`.

## Review Focus

1. **A Hello from a replaced connection** (Task 4) must not restore that controller's banner: the new connection's state wins, also when the old connection's message is processed after the replacement.
2. **Exit during a flash** (Task 6): Exit from the tray, and the app updater's restart, both wait for the write to finish, then exit. The test checks that exactly one exit follows.
3. **The flush at exit** (Task 5) runs after the runtime is disposed, so a mirror written during dispose is saved too, and it never throws during shutdown.
4. **The port saved after a flash** (Task 8) is written on the UI thread while the connection is still suspended, so the resumed connection opens the new port.
5. **A cleared number box** (Task 3) shows the field's default, and saving sends the field as absent, which the firmware reads as the same default.

---

### Task 1: Shared vectors one byte over each text limit; a guard for the NUL scan; the obsolete UDP row

**Files:**
- Modify: `firmware/tools/gen_config_vectors.py` (the `decode_fail(...)` block near line 147, and a new check before the cases are written)
- Regenerate: `firmware/test/golden/config.txt`
- Modify: `docs/superpowers/specs/2026-10-03-controller-config-design.md` §14.1 (remove three rows)

**Interfaces:**
- Consumes: the existing `decode_fail(name, hex_bytes)` helper.
- Produces: new `config.txt` cases `preset_25` and `password_65`. Every suite that reads `config.txt` checks them with no code change: the firmware native tests, the C# `ConfigGoldenTests` and the simulator's Node test.

- [ ] **Step 1: Add the two decode-fail vectors.** Next to `decode_fail("ssid_33", ...)`, add:

```python
# One byte over each remaining text limit (spec §6.1): preset max 24 bytes, password max 64.
decode_fail("preset_25", "4219" + "70" * 25)               # field 8 (preset), 25 bytes
decode_fail("password_65", "2a43" + "1a41" + "61" * 65)    # udp (5) { password (3): 65 bytes }
```

- [ ] **Step 2: Guard the NUL scan's field list.** Before the file is written, add a check that walks the descriptor. `hasNulInStrings` (C++) and the codec's checks (C#) look only at `preset` and `udp.ssid`/`udp.password`, so a new string field anywhere else must fail the generator:

```python
# hasNulInStrings (firmware/lib/volco_config) and ControllerConfigCodec.CheckLimits scan exactly
# these string fields. A string anywhere else (e.g. inside a repeated message) must be added there
# first, so the generator refuses to run until it is.
SCANNED = {"volco.config.ControllerConfig.preset", "volco.config.Udp.ssid", "volco.config.Udp.password"}

def string_fields(desc, seen=None):
    seen = seen or set()
    if desc.full_name in seen:
        return set()
    seen.add(desc.full_name)
    out = set()
    for f in desc.fields:
        if f.type == f.TYPE_STRING:
            out.add(f.full_name)
        elif f.type == f.TYPE_MESSAGE:
            out |= string_fields(f.message_type, seen)
    return out

found = string_fields(pb.ControllerConfig.DESCRIPTOR)
if found != SCANNED:
    raise SystemExit(f"string fields changed: {sorted(found ^ SCANNED)} - teach hasNulInStrings and CheckLimits first")
```

- [ ] **Step 3: Regenerate and run every suite.**

```bash
~/.platformio/penv/bin/python firmware/tools/gen_config_vectors.py
cd firmware && ~/.platformio/penv/bin/pio test -e native       # expect all pass, 2 more decode-fail cases
cd tools/simulator/test && node --test                          # expect all pass
```

Expected: `wrote …/config.txt` with 2 more cases; every suite green.

- [ ] **Step 4: Prove the guard.** Temporarily add a string field to a repeated message in a scratch copy of the proto; the generator must exit with "string fields changed". Then discard the scratch copy.

- [ ] **Step 5: Update §14.1.**
  - Delete the rows "No golden decode-fail vectors…" and "The NUL prescan…".
  - Delete "Re-starting UDP can interrupt a Wi-Fi connect…", and record why at the end of §14.1: *"Closed 2026-10-06: `transport_begin_udp` runs once, in `setup()`, and every config save reboots, so nothing restarts UDP at run time."*

- [ ] **Step 6: Commit and push.** Run `/usr/bin/git add …`, commit `test(config): vectors one byte over the preset and password limits; guard the NUL scan's field list; close the UDP-restart item`, push, and wait for CI green (`ConfigGoldenTests` runs the new cases).

### Task 2: Presets hand out copies

**Files:**
- Modify: `src/Volco.Core/Controller/ControllerPresets.cs` (the `ControllerPreset` record)
- Test: `tests/Volco.Core.Tests/Controller/ControllerPresetsTests.cs`
- Modify: spec §14.1 (remove the presets row)

**Interfaces:**
- Produces: `ControllerPreset` keeps `Id`, `DisplayName`, `IsAvailable` and `Hardware`, but `Hardware` now returns a fresh `ControllerConfig?` clone on every read. Callers are unchanged: `ApplyTo`, `ControllerDraft.IsModified`, `ControllerDraft.BasedOn` and the page's preset combo.

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public void Hardware_IsACopy_EditingItLeavesThePresetAlone()
{
    var diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    diy.Hardware!.Sliders[0].Pin = 39;
    Assert.Equal(33u, diy.Hardware!.Sliders[0].Pin);
    Assert.NotSame(diy.Hardware, diy.Hardware);
}
```

- [ ] **Step 2: Implement.** Replace the positional record with a class holding a private config:

```csharp
/// <summary>A preset in the hardware picker (spec §6.2). <see cref="Hardware"/> is a fresh copy
/// on every read, so no caller can change the preset for everyone; null for a placeholder.</summary>
public sealed class ControllerPreset(string id, string displayName, bool isAvailable, ControllerConfig? hardware)
{
    private readonly ControllerConfig? _hardware = hardware;
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public bool IsAvailable { get; } = isAvailable;
    public ControllerConfig? Hardware => _hardware?.Clone();
}
```

Check the users: `ControllerPresets.ApplyTo` reads `preset.Hardware` once into a local, which it already does (`var hw = preset.Hardware;`). In `ControllerDraft.BasedOn`, the predicate `p.Hardware is not null` creates a copy per call; that's fine, because presets are small.

- [ ] **Step 3: Push. CI green.** `ControllerPresetsTests`, `ControllerDraftTests` and `ControllerViewModelTests` all run in CI.
- [ ] **Step 4: Remove the row; commit** `fix(controller): presets hand out copies of their hardware`.

### Task 3: A cleared number box means "the default"

**Files:**
- Modify: `src/Volco.App/ViewModels/Controller/ControllerViewModel.cs`: the Behaviour block (`Brightness` … `TransitionMs`), the Advanced block (`DebounceMs` … `Deadband`), and `PcPort`/`LocalPort`
- Test: `tests/Volco.App.Tests/ViewModels/ControllerViewModelTests.cs`
- Modify: spec §14.1 (remove the number-box row)

**Interfaces:**
- Produces: these properties become `decimal?`. Reading returns the effective value, the proto default when the field is absent, so it is never null. Writing `null` clears the field (`ClearX()`), so it encodes as absent and the firmware applies the same default. The XAML is unchanged: `NumericUpDown.Value` is already `decimal?`.

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public void ClearingANumberBox_RestoresTheDefault_AndSavesTheFieldAsAbsent()
{
    var vm = Vm(DiyBlob);
    GreetInSync(DiyBlob);
    vm.Brightness = 40;
    Assert.True(vm.Draft.Screens.HasBrightness);
    vm.Brightness = null;                       // the user empties the box
    Assert.Equal(255m, vm.Brightness);          // the proto default shows again
    Assert.False(vm.Draft.Screens.HasBrightness);
    vm.DebounceMs = null;
    Assert.Equal(30m, vm.DebounceMs);
    vm.PcPort = null;
    Assert.Equal(12345m, vm.PcPort);
}
```

- [ ] **Step 2: Implement.** Use one helper per message, so every property is a one-liner:

```csharp
// ScreenBehaviour: a value sets the field, null clears it (the default applies).
public decimal? Brightness
{
    get => S.Brightness;
    set => EditS(s => { if (value is { } v) s.Brightness = (uint)v; else s.ClearBrightness(); });
}
```

Repeat the same pattern for every `decimal` in Behaviour (`DimAfterS`, `DimBrightness`, `OffAfterS`, `PixelShiftEveryS`, `ScrollHoldMs`, `ScrollSpeed` → `ScrollSpeedPxS`, `ScrollEndHoldMs`, `TransitionMs`), Advanced (`DebounceMs`, `SliderIntervalMs`, `SmoothingPct` → `FilterWeightPct`, `Deadband` → `SliderDeadband`) and Wi-Fi (`PcPort`, `LocalPort`), using `EditS`/`EditIn`/`EditU` and the matching `Clear…()`. Booleans are unchanged.

- [ ] **Step 3: Push. CI green.** Remove the row; commit `fix(app): an emptied number box restores the field's default`.

### Task 4: A Hello from a replaced connection can't come back

**Files:**
- Modify: `src/Volco.Core/Controller/ControllerConfigService.cs`
- Modify: `src/Volco.Core/Runtime/VolcoRuntime.cs` (`StartConnection`, `StopConnection`, `OnMessageReceived`)
- Test: `tests/Volco.Core.Tests/Controller/ControllerConfigServiceReadTests.cs`, `tests/Volco.Core.Tests/Runtime/VolcoRuntimeTests.cs`
- Modify: spec §14.1 (remove the late-Hello row)

**Interfaces:**
- Produces:
  - `ControllerConfigService.OnConnectionStarted(object connection)`: the current source.
  - `OnMessage(object source, ControllerMessage message)`: drops the message unless `source` is the current source. The check happens under `_gate`, the same lock the replacement takes.
  - The existing `OnMessage(ControllerMessage)` stays: "from the current source", used by the service tests.
  - `OnConnectionReplaced()` also clears the current source.

- [ ] **Step 1: Write the failing tests.**

```csharp
// ControllerConfigServiceReadTests
[Fact]
public void AHelloFromAReplacedSource_IsDropped()
{
    var old = new object();
    var current = new object();
    _k.Service.OnConnectionStarted(old);
    _k.Service.OnConnectionReplaced();
    _k.Service.OnConnectionStarted(current);
    _k.Service.OnMessage(old, Hello(ControllerConfigState.None, 0));    // arrived late
    Assert.Null(_k.Service.LastHello);
    _k.Service.OnMessage(current, Hello(ControllerConfigState.None, 0));
    Assert.NotNull(_k.Service.LastHello);
}
```

```csharp
// VolcoRuntimeTests: the race through the runtime. The old connection's handler is detached on
// retirement, so deliver through the service the way a message already past the runtime's
// sender check would arrive.
[Fact]
public void AHelloAlreadyPastTheSenderCheck_WhenTheConnectionIsReplaced_IsDropped()
{
    var runtime = StartRuntime();
    var old = Connection(0);
    ConnectAndGreet(old);
    runtime.Reconnect();
    runtime.ControllerConfig.OnMessage(old, LedHello);
    Assert.Null(runtime.ControllerConfig.LastHello);
}
```

- [ ] **Step 2: Implement.** In the service:
  - add `private object? _source;`
  - `OnConnectionStarted(object c)` does `lock (_gate) _source = c;`
  - `OnConnectionReplaced` sets `_source = null` inside its existing lock
  - `OnMessage(object source, ControllerMessage m)` takes `_gate`, returns if `!ReferenceEquals(source, _source)`, then runs the existing body; refactor the body into a private `Accept(ControllerMessage)` that runs inside that lock region
  - `OnMessage(ControllerMessage m)` calls `Accept` directly

  In the runtime:
  - in `StartConnection`, right after the `Interlocked.Exchange` publish (before `connection.Connect()`), call `ControllerConfig.OnConnectionStarted(connection)`
  - in `OnMessageReceived`, call `ControllerConfig.OnMessage(sender!, msg)` instead of `OnMessage(msg)`

- [ ] **Step 3: Push. CI green.** Remove the row; commit `fix(runtime): a Hello from a replaced connection can't restore its banner`.

### Task 5: The config is flushed at exit

**Files:**
- Modify: `src/Volco.Core/Config/ConfigManager.cs` (`RequestSave`, `FlushDebounced`, a new `FlushPendingSave`)
- Modify: `src/Volco.App/CompositionRoot.cs` (`Shutdown`)
- Test: `tests/Volco.Core.Tests/Config/ConfigManagerTests.cs`
- Modify: spec §14.1 (remove the flush row)

**Interfaces:**
- Produces: `public void FlushPendingSave()`. When a debounced save is pending, it cancels the timer and writes now, raising the same event the timer would have. Otherwise it does nothing. It never throws (errors are logged).

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public void FlushPendingSave_WritesADebouncedSaveNow()
{
    var cm = new ConfigManager(_configPath);
    cm.Load();
    cm.Config.ControllerConfigBlob = "MgIIZA==";
    cm.RequestSave();
    cm.FlushPendingSave();                                   // before the 400 ms debounce
    var again = new ConfigManager(_configPath);
    again.Load();
    Assert.Equal("MgIIZA==", again.Config.ControllerConfigBlob);
    cm.FlushPendingSave();                                   // nothing pending: no-op
}
```

- [ ] **Step 2: Implement.**
  - Add `private bool _savePending;` (under `_saveLock`). `RequestSave` sets it, and `FlushDebounced` clears it at the start.
  - Add:

    ```csharp
    /// <summary>Writes a pending debounced save now (shutdown): the 400 ms timer would never fire.</summary>
    public void FlushPendingSave()
    {
        lock (_saveLock)
        {
            if (!_savePending) return;
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        FlushDebounced();
    }
    ```

  - In `CompositionRoot.Shutdown`, call `_config?.FlushPendingSave();` right after `_runtime?.Dispose();`.

- [ ] **Step 3: Push. CI green.** Remove the row; commit `fix(config): flush a pending save at exit`.

### Task 6: Exit waits for a firmware write

**Files:**
- Create: `src/Volco.App/ExitGate.cs`
- Modify: `src/Volco.App/VolcoApplication.axaml.cs` (`RequestExit`), `src/Volco.App/CompositionRoot.cs` (wire the gate to `FirmwareService`)
- Test: `tests/Volco.App.Tests/Shell/ExitGateTests.cs`
- Modify: spec §14.1 (remove the exit row)

**Interfaces:**
- Produces:
  - `internal sealed class ExitGate`: `bool ShouldDefer()` reads a `Func<bool> isBusy`; `void Defer(Action exit)` remembers the exit; `void OnBusyChanged()` runs the deferred exit once busy is false, exactly once.
  - `VolcoApplication.Gate` (`internal static ExitGate?`, set by `CompositionRoot.Start`). `RequestExit()` defers through it while busy, logging "Exit requested during a firmware write; exiting when it finishes".
  - OS logoff/shutdown (`ShutdownRequested`) is never deferred.

- [ ] **Step 1: Write the failing test.**

```csharp
public class ExitGateTests
{
    [Fact]
    public void AnExitDuringAWrite_RunsOnceTheWriteEnds_ExactlyOnce()
    {
        var busy = true;
        var exits = 0;
        var gate = new ExitGate(() => busy);
        Assert.True(gate.ShouldDefer());
        gate.Defer(() => exits++);
        gate.Defer(() => exits++);           // a second Exit click: still one exit
        gate.OnBusyChanged();
        Assert.Equal(0, exits);
        busy = false;
        gate.OnBusyChanged();
        gate.OnBusyChanged();
        Assert.Equal(1, exits);
    }

    [Fact]
    public void NotBusy_NothingIsDeferred() => Assert.False(new ExitGate(() => false).ShouldDefer());
}
```

- [ ] **Step 2: Implement `ExitGate`** (UI-thread only, no locks):

```csharp
namespace Volco.App;

/// <summary>Holds an Exit while a firmware write runs (an interrupted write leaves the controller
/// without firmware until Install runs again), then exits once it ends. UI thread only.</summary>
internal sealed class ExitGate(Func<bool> isBusy)
{
    private Action? _deferred;

    public bool ShouldDefer() => isBusy();

    public void Defer(Action exit) => _deferred ??= exit;

    public void OnBusyChanged()
    {
        if (_deferred is null || isBusy()) return;
        var exit = _deferred;
        _deferred = null;
        exit();
    }
}
```

- [ ] **Step 3: Wire it.**
  - In `CompositionRoot.Start`, after creating `_firmware`, set `VolcoApplication.Gate = new ExitGate(() => _firmware.IsBusy);` and subscribe `_firmware.BusyChanged += (_, _) => Dispatcher.UIThread.Post(() => VolcoApplication.Gate?.OnBusyChanged());`.
  - In `RequestExit`, at the top: `if (Gate is { } g && g.ShouldDefer()) { AppLog.Info("Exit requested during a firmware write; exiting when it finishes"); g.Defer(RequestExit); return; }`.
  - The tray's Exit and the updater both call `RequestExit`, so both are covered.

- [ ] **Step 4: Push. CI green.** Remove the row; commit `fix(app): Exit waits for a firmware write to finish`.

### Task 7: The firmware manifest is written on every build

**Files:**
- Modify: `firmware/scripts/manifest.py` (the `AddPostAction` target)
- Modify: spec §14.1 (remove the `dist/` row)

- [ ] **Step 1: Reproduce.**

```bash
cd firmware && ~/.platformio/penv/bin/pio run -e esp32
VOLCO_FW_DISPLAY=probe-a ~/.platformio/penv/bin/pio run -e esp32
grep displayVersion .pio/build/esp32/dist/firmware-manifest.json
```

Expected (the bug): `"displayVersion": "dev"`, not `probe-a`, because nothing relinked.

- [ ] **Step 2: Implement.** Attach the action to PlatformIO's always-run program target instead of the `.bin` file:

```python
if env is not None:
    # "buildprog" runs on every build, also when nothing relinks, so a changed VOLCO_FW_DISPLAY
    # (or a dist/ deleted by hand) is always reflected (spec §9.1).
    env.AddPostAction("buildprog", write_manifest)
```

- [ ] **Step 3: Verify.** Re-run Step 1. Expected: `"displayVersion": "probe-a"`. Then run `python3 firmware/scripts/manifest.py --selftest` → `SELFTEST OK`.
- [ ] **Step 4: Remove the row; commit** `fix(firmware): write the manifest on every build`.

### Task 8: The port saved after a flash is written on the UI thread

**Files:**
- Modify: `src/Volco.Core/Firmware/FirmwareService.cs` (constructor, `SavePort`, both call sites)
- Modify: `src/Volco.App/CompositionRoot.cs` (pass the UI runner)
- Test: `tests/Volco.Core.Tests/Firmware/FirmwareTests.cs`
- Modify: spec §14.1 (remove the SavePort row)

**Interfaces:**
- Produces: a new last constructor parameter on `FirmwareService`, `Func<Action, Task>? configThread = null`. This is how the service runs a config edit on the thread that owns the config (the UI thread in the app). It defaults to running the action inline. `SavePort` becomes `async Task SavePortAsync(string port)`, awaited at both call sites while the suspension is still held.

- [ ] **Step 1: Write the failing test.**

```csharp
[Fact]
public async Task TheNewPort_IsSavedThroughTheConfigThread_WhileSuspended()
{
    var ran = new List<bool>();
    var service = new FirmwareService(_runtime, _flasher, _catalog, _time, a =>
    {
        ran.Add(_runtime.IsSuspended);
        a();
        return Task.CompletedTask;
    });
    var flash = service.FlashAsync("COM7", FlashMode.Install, NoProgress);
    await Until(() => Count == 2);
    Last.SetStatus(ConnectionStatus.Connected);
    Last.RaiseMessage(Hello("3f9c2a1b5d07"));
    await flash;
    Assert.Equal(new[] { true }, ran);          // once, and still suspended
    Assert.Equal("COM7", Last.Config.Serial.ComPort);
}
```

- [ ] **Step 2: Implement.**
  - Store `_configThread = configThread ?? (a => { a(); return Task.CompletedTask; });`.
  - `SavePortAsync` returns early when the port is unchanged; otherwise it awaits `_configThread(() => { serial.ComPort = port; _runtime.Config.Save(); })` inside a try/catch that logs.
  - Await it at both call sites, inside the `using (_runtime.SuspendConnection())` block.

- [ ] **Step 3: Pass the runner in `CompositionRoot.Start`:**

```csharp
_firmware = new FirmwareService(_runtime, _platform.Flasher, catalog, configThread: a =>
{
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Dispatcher.UIThread.Post(() => { try { a(); done.SetResult(); } catch (Exception e) { done.SetException(e); } });
    return done.Task;
});
```

- [ ] **Step 4: Push. CI green.** Remove the row; commit `fix(firmware): save the dialog's port on the UI thread`.

### Task 9: The build-id check runs on every push

**Files:**
- Modify: `.github/workflows/build.yml`: move the Windows compile from `test` into `firmware-test`, with the real firmware and `RequireFirmware`
- Modify: spec §14.1 (remove the CheckFirmware row), `CLAUDE.md` (the CI paragraph)

- [ ] **Step 1: Change `firmware-test`.** After "Upload firmware", add:

```yaml
    - name: Setup .NET
      uses: actions/setup-dotnet@v5
      with:
        dotnet-version: |
          8.0.x
          10.0.x
        cache: true
        cache-dependency-path: Volco.sln

    - name: The Windows project embeds this firmware (RequireFirmware, resource names)
      run: |
        mkdir -p src/Volco.Windows/Firmware/esp32
        cp firmware/.pio/build/esp32/dist/* src/Volco.Windows/Firmware/esp32/
        dotnet build src/Volco.Windows/Volco.Windows.csproj --configuration Release -p:EnableWindowsTargeting=true \
          -p:RequireFirmware=true -p:VolcoFirmwareBuildId=${{ steps.fwid.outputs.id }}
        grep -qa "Firmware/esp32/firmware-manifest.json" src/Volco.Windows/bin/Release/net8.0-windows10.0.22621.0/Volco.dll

    - name: RequireFirmware refuses another build's firmware
      run: |
        if dotnet build src/Volco.Windows/Volco.Windows.csproj --configuration Release -p:EnableWindowsTargeting=true \
             -p:RequireFirmware=true -p:VolcoFirmwareBuildId=000000000000 --no-incremental; then
          echo "RequireFirmware accepted a mismatched build id" >&2; exit 1
        fi
```

- [ ] **Step 2:** Delete the "Compile the Windows project" step from the `test` job; it's covered above, now with the real firmware. Run `actionlint` on the workflow.
- [ ] **Step 3: Push.** CI green. In the log, check that the negative step printed `RequireFirmware: the embedded firmware isn't build 000000000000.` and passed. This is spec §12.5's check, automated.
- [ ] **Step 4:** Remove the row; update CLAUDE.md's CI paragraph; commit `ci: check the embedded firmware's build id on every push`.

### Task 10: The LGPL relink bundle in the release zip (the user chose option C, 2026-10-06) — DONE

**Done 2026-10-06:**
- **Script:** the bundle comes from `firmware/scripts/relink_bundle.py`, a post action on the `.elf` link step. It copies exactly the inputs on the link line and writes `link-command.txt`, with `objects/` and `$HOME` paths.
- **Relink proof:** run from a copy of `relink/` alone, the relink plus `elf2image`, with the build's flags, reproduced `firmware.bin` byte for byte.
- **Size:** about 27 MB uncompressed, mostly U8g2's font objects.
- **CI:** `firmware-test` checks that the bundle exists, and `release.yml` zips it.

The steps below are the original plan, kept for reference.

**Why:** the firmware statically links the Arduino core (LGPL 2.1). A release given to others must let its recipients relink the firmware with a modified core. The user keeps the repository private, so each release's firmware zip carries the firmware's **object files** and the **link command** instead of its source.

**What goes in the bundle:**
- **Objects:** the compiled objects of Volco's own code and of the non-core libraries (nanopb, U8g2), i.e. everything under `.pio/build/esp32/` that is an input to the final link.
- **The link command:** the exact final link command line, with paths made relative.
- **`README-relink.txt`:** how to relink against a modified core.
- **Not included:** the core's own libraries and linker scripts. Recipients get those from Espressif, at arduino-esp32 3.3.8 / pioarduino 55.03.38.

**Files:**
- Create: `firmware/scripts/relink_bundle.py`. It runs as a post action, after the manifest; nothing in the regular build depends on its output.
- Modify:
  - `firmware/platformio.ini` (add it to `extra_scripts`)
  - `.github/workflows/release.yml` (the zip includes `relink/`)
  - `THIRD-PARTY-NOTICES.txt` (the Arduino core paragraph)
  - spec §14 point 3 (resolved)

**Interfaces:**
- Produces: `$BUILD_DIR/relink/`, containing:
  - `objects/` (the `.o` and `.a` link inputs under `$BUILD_DIR`, copied with their relative paths)
  - `link-command.txt` (`env.subst("$LINKCOM")` with `$BUILD_DIR` replaced by `objects`)
  - `README-relink.txt`

- [x] **Step 1: Write the script.** Use a post action on `buildprog`, like the manifest after Task 7:

```python
# PlatformIO post-script: the LGPL 2.1 relink bundle (spec §14, point 3, option C). The firmware
# statically links the Arduino core; recipients of a release must be able to relink it with a
# modified core. This copies the link inputs built from Volco's code and the non-core libraries,
# and the exact link command, into $BUILD_DIR/relink/. The core's own libraries and linker scripts
# are not included: they come from Espressif (arduino-esp32 3.3.8, pioarduino 55.03.38).
import os
import shutil

Import("env")  # noqa: F821

README = """Relinking the Volco controller firmware (GNU LGPL 2.1, section 6)

The firmware statically links the Arduino core for the ESP32 (arduino-esp32 3.3.8, LGPL 2.1).
To relink it with a modified core:
1. Install PlatformIO with the pioarduino platform 55.03.38 (Arduino core 3.3.8) and apply your
   changes to the core.
2. Build the core's libraries as usual (any PlatformIO esp32dev Arduino project builds them).
3. Run link-command.txt from this folder, with the core paths in it pointing at your build.
   objects/ holds every other input: Volco's own code, nanopb and U8g2, compiled for ESP32.
4. Convert firmware.elf to firmware.bin with esptool (elf2image) and flash it at 0x10000.
"""


def write_bundle(target, source, env):
    build = env.subst("$BUILD_DIR")
    out = os.path.join(build, "relink")
    shutil.rmtree(out, ignore_errors=True)
    os.makedirs(os.path.join(out, "objects"))
    for dirpath, _, files in os.walk(build):
        if dirpath.startswith(out):
            continue
        for f in files:
            if f.endswith((".o", ".a")):
                rel = os.path.relpath(os.path.join(dirpath, f), build)
                dst = os.path.join(out, "objects", rel)
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copyfile(os.path.join(dirpath, f), dst)
    command = env.subst("$LINKCOM", target=[env.File("$BUILD_DIR/${PROGNAME}.elf")],
                        source=env.get("PIOBUILDFILES", []))
    with open(os.path.join(out, "link-command.txt"), "w") as fp:
        fp.write(command.replace(build, "objects") + "\n")
    with open(os.path.join(out, "README-relink.txt"), "w") as fp:
        fp.write(README)
    print(f"Relink bundle: {out}")


env.AddPostAction("buildprog", write_bundle)  # noqa: F821
```

- [x] **Step 2: Wire it and check it.**
  - Add `post:scripts/relink_bundle.py` to `extra_scripts` in `platformio.ini`.
  - Run `pio run -e esp32`.
  - **Expect:** `.pio/build/esp32/relink/` holds `objects/src/main.cpp.o` (and the other `src/` objects), `objects/lib…/libvolco_*.a`, the nanopb and U8g2 libraries, plus `link-command.txt` (one long `xtensa-esp32-elf-g++ … -o …firmware.elf` line) and `README-relink.txt`.
  - If `$LINKCOM` doesn't expand to the real command line, capture the link line from `pio run -e esp32 -v` instead: run it in `release.yml`, pick the line that ends in `firmware.elf`, and write that.
  - Check the bundle's size (expect a few MB).
- [x] **Step 3: Prove a relink works (on this host).** From a copy of `relink/`, run `link-command.txt` with the core paths pointing at the PlatformIO packages already on this machine; it must produce a `firmware.elf`. Then `esptool elf2image` must produce a `firmware.bin` the same size as the shipped one (byte equality isn't expected: the build embeds timestamps). Record the result in the PR.
- [x] **Step 4: Ship it.**
  - In `release.yml`, the "Firmware zip" step also copies `firmware/.pio/build/esp32/relink` into the zip as `relink/` (`cp -r`).
  - In `THIRD-PARTY-NOTICES.txt`, the Arduino core paragraph replaces its source sentence with: *"The firmware's object files and link command are in relink/ in this zip (see README-relink.txt), so you can relink it with a modified core; the core's source is at that tag."*
  - Regenerate nothing else, then run `actionlint`.
- [x] **Step 5: Close the decision.** Spec §14 point 3 becomes *"Resolved 2026-10-06 (the user): option C. The repository stays private; every release's firmware zip carries the relink bundle (`firmware/scripts/relink_bundle.py`)."* Tick the matching "Before merging" item in the hardware checklist. Commit `feat(release): LGPL relink bundle in the firmware zip`.

### Task 11: Close out

- [ ] Run a whole-branch review on a fresh reviewer (Opus) over `origin/main...HEAD`, fix the findings, and push until CI is green with 0 warnings.
- [ ] §14.1 now holds only the install-speed row. Update CLAUDE.md where a task changed behaviour (Tasks 5, 6, 9).
- [ ] Open the PR into `main`. The base is `main` directly, with no stacking.
- [ ] The next real release is the first to run the rewritten `release.yml`. Suggest a test release first (e.g. `v1.1.0-rc1`), checking that it attaches `Volco.exe` and the firmware zip with `relink/`.
