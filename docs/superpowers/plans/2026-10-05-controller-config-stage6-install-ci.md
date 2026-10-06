# Controller Config, Stage 6: Firmware Install and CI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Volco installs and updates the controller's firmware over USB, and every CI build carries the firmware that matches its source.
- A probe tells what is on the port when no Hello comes.
- The update shows on the page, the sidebar and the tray.
- Releases ship the firmware zip with its licences.

**Architecture:**
- **Core** (`Volco.Core/Firmware/`):
  - `FirmwareManifest` (parse + validation)
  - `FirmwareImage` / `FirmwareCatalog` from an `IFirmwareSource`
  - the `IFirmwareFlasher` seam with its records
  - `FirmwareService` (one probe or flash at a time, connection suspension, port write-back, the post-flash Hello wait)
  - `VolcoRuntime` gains `SuspendConnection()`, a `HelloReceived` event, and the catalog (the banner's bundled version)
- **App:**
  - **Controller page:** Install / Update / Check firmware, an inline firmware panel (port, progress, result), and the no-Hello hint
  - **Tray:** the update icon variant, the tooltip line, and the "Update controller firmware…" menu item
  - `PlatformServices` gains the flasher and the firmware source
- **Windows:** `EspDotNetFirmwareFlasher` (EspDotNet 5.0.2), `EmbeddedFirmwareSource`, firmware embedded from `Firmware/esp32/**`, and the `RequireFirmware` build check.
- **Firmware build:**
  - `firmware/scripts/version.py` (pre): defines `VOLCO_FW_VERSION` from the environment when set
  - `firmware/scripts/manifest.py` (post): copies the four parts and writes `dist/firmware-manifest.json`
- **CI:**
  - `build.yml`: the firmware job uploads `firmware-esp32` and outputs the build id; the Windows build embeds it
  - `release.yml`: version → firmware → release, attaching `volco-firmware-esp32-<version>.zip` with `THIRD-PARTY-NOTICES.txt` and `OFL.txt`

**EspDotNet API** (read from the package source at commit 62619b8, which matches 5.0.2):
- **Bootloader and stub:** `new Communicator(serialPort)`, then `new BootloaderTool(comm, config.BootloaderSequence).StartBootloaderAsync(ct)`. This throws `EspProtocolException` when there is no boot message or no sync.
- **Chip detection:** `new ChipTypeDetectTool(bootloader, config).DetectAndGetDeviceConfigAsync(ct)` gives `.ChipType`, a `ChipTypes` value.
- **Stub upload:** `new SoftLoaderTool(comm, new RamUploadTool(bootloader, device)).StartAsync(DefaultFirmwareProviders.GetSoftloaderForDevice(chip), ct)`.
- **Faster baud:** `soft.ChangeBaudAsync(460800, 115200, ct)`, then **the caller** sets `port.BaudRate = 460800`.
- **MD5:** `soft.SpiFlashMd5Async(addr, size, ct)`.
- **Writing:** `new FirmwareUploadTool(new FlashUploadDeflatedTool(soft, device)) { Progress }.UploadFirmwareAsync(new FirmwareProvider(0, segments), ct)`.
- **Reset:** `new ResetDeviceTool(comm, config.ResetSequence).ResetAsync(ct)`.
- **Config:** `ConfigProvider.LoadDefaultConfig()`.

**Spec:** §8.3 (firmware notice, indicators), §8.6, §9.1–§9.4, §12.4–§12.5, §12.7 (items 1, 5, 5a, 5b, 7, 9, 10, 11), §13.

## Global Constraints

- **Repo rules:** `/usr/bin/git`; no local `dotnet`; Write tool for files.
- **`Volco.App` and `Volco.Core` stay platform-free.** EspDotNet is referenced only by `Volco.Windows`; `ArchitectureTests` stay green.
- **Update never writes 0x9000–0xDFFF** (the NVS range comes from the manifest, not a constant). The parts are written separately, never as a merged image.
- **The release workflow is never triggered from here.** `release.yml` changes are checked by review and by `actionlint`, if available. Only a user-requested run proves them.
- **Hardware safety on this host** (the Linux board, for firmware-side checks): `hello_check.py --guard-only` first, every time.

## Review Focus

1. **NVS:** Update can't touch NVS, Install wipes exactly the manifest's NVS range, and the manifest rejects an overlap (script and app).
2. **Suspension:** no `Connect()` while a suspension is held, including the race where `StartConnection` is past its first check; disposing restarts from the current config.
3. **Cancellation** only before the first write; a failure mid-write gives the retry message, and the connection is always resumed.
4. **Build id:** the embedded manifest's `version` equals the CI build id, and `RequireFirmware` fails the build otherwise.

---

### Task 1: The firmware build: version define and manifest (firmware)

**Files:** create `firmware/scripts/version.py` and `firmware/scripts/manifest.py`; modify `firmware/platformio.ini` (`extra_scripts = pre:scripts/version.py, post:scripts/manifest.py`) and `firmware/src/main.cpp` (no change if the define is already guarded by `#ifndef`).

- **`version.py`:** if `VOLCO_FW_VERSION` is set in the environment (12 lowercase hex, or anything ≤ 32 printable ASCII characters), append `('VOLCO_FW_VERSION', '\\"<v>\\"')` to `CPPDEFINES`; otherwise nothing, so the build reports `dev`.
- **`manifest.py`:** an `AddPostAction` on `$BUILD_DIR/${PROGNAME}.bin`, which:
  1. reads `FLASH_EXTRA_IMAGES` (offset, path) and `ESP32_APP_OFFSET` from the env, plus the partition table CSV (`board_build.partitions`, resolved like PlatformIO does) for the `nvs` row
  2. copies each part to `$BUILD_DIR/dist/` with its basename (`bootloader.bin`, `partitions.bin`, `boot_app0.bin`, `firmware.bin`)
  3. writes `firmware-manifest.json` per §9.1 (`chip` "esp32", `version` = `VOLCO_FW_VERSION` or "dev", `displayVersion` = `VOLCO_FW_DISPLAY` or the version, `parts` with offset/size/md5, `nvs` offset/size)
  4. fails the build (`env.Exit(1)`) when a part overlaps NVS
- **`check_firmware_id_paths.py`** still passes: `scripts/` is in the id list, and `extra_scripts` points into it.

- [ ] **Step 1:** Implement. `pio run -e esp32` → `dist/` holds 4 bins and a manifest with `version` "dev"; offsets 0x1000/0x8000/0xE000/0x10000; NVS 0x9000/0x5000.
- [ ] **Step 2:** `VOLCO_FW_VERSION=$(bash firmware/scripts/firmware_id.sh) pio run -e esp32` → the manifest version equals the id.
- [ ] **Step 3:** Prove the overlap check once by temporarily editing a copy of the partitions CSV with `nvs` at 0xE000, then revert. Record it in the ledger.
- [ ] **Step 4:** Commit `feat(firmware): build id define and firmware manifest (spec 9.1, 9.2)`.

### Task 2: CI artifact flow (`build.yml`) and `RequireFirmware` (Windows csproj)

**Files:** `.github/workflows/build.yml`, `src/Volco.Windows/Volco.Windows.csproj`, `src/Volco.Windows/Firmware/README.md` (says the folder is filled by CI and that local builds carry no firmware), `.gitignore` (`src/Volco.Windows/Firmware/esp32/`).

- **`firmware-test`:**
  - a step `id: fwid` that runs `firmware_id.sh` and sets the output `id`
  - the build step gets `env: VOLCO_FW_VERSION: ${{ steps.fwid.outputs.id }}` and `VOLCO_FW_DISPLAY: ci+${GITHUB_SHA::7}`
  - `actions/upload-artifact@v4` uploads `firmware-esp32` from `firmware/.pio/build/esp32/dist/`
  - the job's `outputs: build-id`
- **`build`:**
  - `needs: [test, firmware-test]` (keep any existing needs)
  - `actions/download-artifact@v4` puts `firmware-esp32` into `src/Volco.Windows/Firmware/esp32`
  - publish gets `-p:RequireFirmware=true -p:VolcoFirmwareBuildId=${{ needs.firmware-test.outputs.build-id }}`
- **csproj:**
  - `<EmbeddedResource Include="Firmware\**\*" Exclude="Firmware\README.md" LogicalName="Firmware/%(RecursiveDir)%(Filename)%(Extension)" />`
  - a target `CheckFirmware` `BeforeTargets="BeforeBuild"`, `Condition="'$(RequireFirmware)' == 'true'"`:
    - `<Error>` when `Firmware/esp32/firmware-manifest.json` is missing
    - otherwise it reads the manifest with `ReadLinesFromFile` and checks it contains `"version": "$(VolcoFirmwareBuildId)"` (or uses a small inline task), and errors on a mismatch

- [ ] **Step 1:** Implement. **§12.5 check (manual, once):** dispatch the build job with an intentionally empty build id (a temporary branch commit that skips the download) to see `RequireFirmware` fail, then revert. **Only if the user agrees to that run:** otherwise prove it by review, and say so in the ledger.
- [ ] **Step 2:** Push. CI's test jobs are green, and the firmware job uploads the artifact (the Windows build job runs only on main or when asked). Commit `ci: firmware artifact with its build id, embedded in the Windows build (spec 9.3)`.

### Task 3: Manifest, image and catalog (Core)

**Files:** `src/Volco.Core/Firmware/{FirmwareManifest.cs,FirmwareImage.cs,IFirmwareSource.cs,FirmwareCatalog.cs}`; tests `tests/Volco.Core.Tests/Firmware/FirmwareManifestTests.cs`.

```csharp
public sealed record FirmwarePartInfo(string Name, string File, uint Offset, uint Size, string Md5);
public sealed record FirmwareManifest(string Chip, string Version, string DisplayVersion, IReadOnlyList<FirmwarePartInfo> Parts, uint NvsOffset, uint NvsSize)
{
    public static FirmwareManifest Parse(string json);   // FirmwareManifestException on missing fields, overlap with NVS, overlapping parts, an app part missing
    public FirmwarePartInfo App { get; }                 // the part named "app"
}
public sealed record FirmwarePart(FirmwarePartInfo Info, byte[] Data);
public sealed class FirmwareImage { Manifest; Parts; static FirmwareImage Load(IFirmwareSource source, string chip); }   // size + MD5 of each file checked
public interface IFirmwareSource { Stream? Open(string path); }     // "esp32/firmware-manifest.json", "esp32/firmware.bin"
public sealed class EmptyFirmwareSource : IFirmwareSource
public sealed class FirmwareCatalog { FirmwareImage? Esp32; bool HasFirmware; string? BundledVersion; static FirmwareCatalog Load(IFirmwareSource source); }   // a broken bundle → logged, HasFirmware false
```

- [ ] **Step 1 (RED):**
  - a valid manifest parses
  - **a part overlapping NVS is rejected**
  - overlapping parts are rejected
  - a missing app part is rejected
  - `Load` rejects a part whose MD5 differs
  - `EmptyFirmwareSource` gives `HasFirmware` false
- [ ] **Step 2 (GREEN).** Commit `feat(firmware): manifest, image and catalog (spec 8.6, 9.1)`.

### Task 4: Suspension and `HelloReceived` in the runtime; the catalog wired into the banner (Core)

**Files:** `src/Volco.Core/Runtime/VolcoRuntime.cs`, `VolcoRuntimeOptions.cs`; tests in `VolcoRuntimeTests`.

- `public IDisposable SuspendConnection()`:
  - It increments `_suspensions` (Interlocked), retires the current connection like a replacement (link Detach, `ControllerConfig.OnConnectionReplaced`, test mode off), and returns a handle.
  - Disposing the handle decrements the count (idempotent). At zero, if not disposed, it calls `StartConnection()`.
  - `IsSuspended` is `Volatile.Read(ref _suspensions) > 0`.
- **`StartConnection`:** it returns at the top when suspended. After publishing, it re-checks `IsSuspended || IsDisposed` and retires that connection with CompareExchange, **without** `Connect()`.
- `public event EventHandler<HelloMessage>? HelloReceived`: every Hello from the current connection, version-only ones included, raised after the link and the config service have it.
- `VolcoRuntimeOptions.Firmware` (`FirmwareCatalog`, default empty) is passed to `ControllerConfigService` as `() => catalog.BundledVersion`.

- [ ] **Step 1 (RED):**
  - `SuspendConnection` releases the connection (disposed)
  - **a connection config change and `Reconnect()` while suspended don't open one**
  - **the race:** a factory that blocks until the test starts a suspension → the published connection is retired and `Connect()` is never called
  - dispose restarts from the current config (a new port in the config → the new connection carries it)
  - dispose after the runtime is disposed does nothing
  - `HelloReceived` fires for a version-only Hello too
  - the banner offers Update with a catalog over Serial
- [ ] **Step 2 (GREEN).** Commit `feat(runtime): connection suspension, HelloReceived, bundled firmware for the banner (spec 8.6)`.

### Task 5: The flasher seam and `FirmwareService` (Core)

**Files:** `src/Volco.Core/Firmware/{IFirmwareFlasher.cs,FirmwareService.cs}`; tests `tests/Volco.Core.Tests/Firmware/FirmwareServiceTests.cs` with a `FakeFlasher`.

```csharp
public enum FlashMode { Install, Update }
public enum ProbeKind { NoDevice, UnsupportedChip, VolcoMatches, DifferentFirmware }
public sealed record ProbeResult(ProbeKind Kind, string? ChipName = null);
public enum FlashStage { Connecting, Erasing, Writing, Verifying, Restarting }
public sealed record FlashProgress(FlashStage Stage, double Fraction);
public interface IFirmwareFlasher
{
    Task<ProbeResult> ProbeAsync(string port, FirmwareImage bundled, IProgress<FlashProgress> progress, CancellationToken ct);
    /// <summary>Honours ct only until the first write; throws FirmwareFlashException(reason, written) on failure,
    /// UnsupportedChipException(chipName) before writing anything.</summary>
    Task FlashAsync(string port, FirmwareImage image, FlashMode mode, IProgress<FlashProgress> progress, CancellationToken ct);
}
public sealed class FirmwareService
{
    FirmwareService(VolcoRuntime runtime, IFirmwareFlasher? flasher, FirmwareCatalog catalog, TimeProvider time);
    bool IsAvailable;   // a flasher and HasFirmware
    bool IsBusy; event EventHandler? BusyChanged;
    Task<ProbeOutcome> ProbeAsync(string port, IProgress<FlashProgress>, CancellationToken);
    Task<FlashOutcome> FlashAsync(string port, FlashMode mode, IProgress<FlashProgress>, CancellationToken);
}
public sealed record ProbeOutcome(ProbeResult? Result, string Message, bool OfferInstall);   // the §8.6 messages with the port name
public sealed record FlashOutcome(bool Succeeded, string Message);
```

- **Steps** (both operations): reject unless connection type is Serial, with "Connect the controller with USB and set the connection to Serial." Then:
  1. `using var suspend = runtime.SuspendConnection()`
  2. run the probe or flash
  3. on success with a different port, write `Serial.ComPort` (direct assignment plus `config.Save()`, while still suspended)
  4. dispose the suspension
  5. **after a flash:** wait up to 10 s (TimeProvider) for `HelloReceived` with `FirmwareVersion == catalog.BundledVersion`
- **Outcomes:**
  - success → "Installed. The controller is running firmware <id>." (or "Updated. …")
  - no Hello → "The firmware was installed but the controller didn't answer. Check the connection settings."
  - flasher failure → "Install failed: <reason>. The controller won't work until the install succeeds. Try again." (Update: "Update failed: …")
  - unsupported chip → "This board is an <chip>; this version of Volco only includes firmware for ESP32."
  - cancelled before writing → "Cancelled. Nothing was written."

- [ ] **Step 1 (RED):** one test per §12.3 FirmwareService bullet:
  - suspend and resume around both probe and flash (the fake asserts `runtime.IsSuspended` during its call; after it, a new connection was created and connected)
  - progress forwarded
  - a failure mid-flash → the message, and the connection resumed
  - no Hello after the flash → the message (advance fake time 10 s)
  - the dialog's port is persisted after success while still suspended, and the resumed connection uses it
  - every probe result → its message
  - non-Serial → rejected without suspending
  - a second call while busy → rejected
- [ ] **Step 2 (GREEN).** Commit `feat(firmware): flasher seam and FirmwareService (spec 8.6)`.

### Task 6: The Windows flasher and the embedded source (Windows)

**Files:** `src/Volco.Windows/Firmware/{EspDotNetFirmwareFlasher.cs,EmbeddedFirmwareSource.cs}`, `Program.cs` (`PlatformServices` with flasher and source), `Volco.Windows.csproj` (`EspDotNet` 5.0.2), `src/Volco.App/PlatformServices.cs` (+ `IFirmwareFlasher? Flasher`, `IFirmwareSource Firmware`, both defaulting through a secondary constructor so App tests keep compiling), `src/Volco.App/CompositionRoot.cs` (`FirmwareCatalog.Load(platform.Firmware)` → `VolcoRuntimeOptions.Firmware`; `FirmwareService` built here and handed to the settings VM).

**`EspDotNetFirmwareFlasher`:**
1. **Open the port:** retry up to 1 s on `UnauthorizedAccessException` or `IOException`; still failing → NoDevice / "The port is in use".
2. **Bootloader:** with a 5 s timeout CTS linked to `ct`, `BootloaderTool` then `ChipTypeDetectTool`. An `EspProtocolException`, the timeout or an `IOException` → NoDevice.
3. **Chip check:** a chip that isn't `ChipTypes.ESP32` → `UnsupportedChip` (probe) or `UnsupportedChipException` (flash). The chip name is `chip.ToString().Replace("ESP32", "ESP32-")` (so "ESP32-s3"), or "ESP8266".
4. **Stub and baud:** start the stub, then `ChangeBaudAsync(460800, 115200)` and `port.BaudRate = 460800`. If the switch throws, set the port back to 115200, log it and carry on.
5. **Probe:** `SpiFlashMd5Async(app.Offset, app.Size)` against the app MD5 → VolcoMatches / DifferentFirmware.
6. **Flash:** from here `ct` is ignored, so nothing can cancel after the first write.
   1. **Install:** write 0xFF × `NvsSize` at `NvsOffset` first (FlashStage.Erasing).
   2. Write the parts with `FirmwareUploadTool` (Writing, forwarding the progress).
   3. Check every part's MD5 (Verifying). A mismatch throws `FirmwareFlashException("verification failed for <part>")`.
7. **Reset:** `ResetDeviceTool` (Restarting), then close the port.
- Every call logs its stages and timings to `AppLog`, which records the install time for the < 20 s target.

**`EmbeddedFirmwareSource`:** `typeof(Program).Assembly.GetManifestResourceStream("Firmware/" + path)`.

- [ ] **Step 1:** Implement. CI compiles it; Windows-only code has no Linux tests (as for `AudioEngine`). Commit `feat(windows): EspDotNet flasher and embedded firmware (spec 8.6)`.

### Task 7: UI — the firmware panel, the no-Hello hint, the tray (App)

**Files:** `src/Volco.App/ViewModels/Controller/FirmwarePanelViewModel.cs`, `ControllerViewModel.cs`, `ControllerPage.axaml`, `CompositionRoot.cs` (tray), `src/Volco.App/Assets/volco-update.ico` (generated: `volco.ico` with an accent dot, by a small committed script `tools/make_update_icon.py` that uses Pillow), tests in `ControllerViewModelTests`, `ControllerPageTests`, and `Shell/CompositionRootTests` if the tray is testable there.

**Status section:**
- `FirmwareText`: "Firmware 3f9c2a1b5d07" plus " (from Volco <app version>)" when it equals the bundled id; "Development firmware" for dev.
- Buttons:
  - **Install firmware…** (always, when available)
  - **Update firmware** (when `Banner.FirmwareUpdateAvailable` or `Incompatible`)
  - **Check firmware**
- When `!IsAvailable` or the connection isn't Serial, the buttons are disabled with the hint "Connect the controller with USB and set the connection to Serial." (or "This build of Volco has no firmware." when there is no catalog).

**Firmware panel** (inline, under the status):
- the port combo (`SerialPort.GetPortNames()` through a seam already used by `ConnectionViewModel`; default: the configured port)
- the stage text and a progress bar
- the result message, with an **Install** button when the probe offers one
- **Cancel** while connecting, which ends up as "Cancelled. Nothing was written."
- After a successful flash, "Test your controller now" starts the test.

**No-Hello hint:** the connection is Serial, `ControllerState.AwaitingHello` for 5 s since Connected (a `DispatcherTimer`-free design: the view model stores the time when it saw AwaitingHello and is re-checked by a 1 s `TimeProvider` timer) → "No Volco controller answered on COM3. Check what's connected?" with a **Check** button that runs the probe.

**Tray:**
- `FirmwareUpdateAvailable` (from `SettingsViewModel.ControllerNavDot`'s source, the runtime banner) swaps the tray icon to `volco-update.ico`.
- It adds a tooltip line "Controller firmware update available".
- It inserts the "Update controller firmware…" menu item, which opens Settings on the Controller page.
- All of these are cleared when it turns false.

- [ ] **Step 1 (RED):**
  - Install/Update disabled when the catalog is empty or the connection isn't Serial
  - every probe result → its message, with the Install offer
  - a flash success → the message and "Test your controller now"
  - the no-Hello hint after 5 s of AwaitingHello on Serial; not on WebSocket
  - the tray state follows `FirmwareUpdateAvailable` (icon variant, tooltip, menu item) and clears on a Hello with the bundled id
- [ ] **Step 2 (GREEN).** Commit `feat(app): install, update and check firmware; no-Hello hint; tray indicators (spec 8.3, 8.6)`.

### Task 8: Release workflow, notices, docs

**Files:** `.github/workflows/release.yml`, `THIRD-PARTY-NOTICES.txt` (repo root: nanopb zlib, U8g2 BSD-2, Arduino-ESP32 LGPL-2.1, ESP-IDF Apache-2.0, each with its licence text; Arimo via `firmware/assets/fonts/OFL.txt`), `src/Volco.App/Views/Pages/GeneralPage.axaml` (About: "Third-party notices" expander showing the embedded `THIRD-PARTY-NOTICES.txt` and `OFL.txt`; both embedded as `AvaloniaResource`), `README.md` (install and setup story), `firmware/README.md` (§13 items, "Developing the firmware"), `CLAUDE.md` (CI artifact flow, `RequireFirmware`, the flasher split).

**`release.yml`:**
- **`version`** (ubuntu): resolves `version`/`tag` as today; outputs them.
- **`firmware`** (ubuntu, needs version):
  - PlatformIO 6.1.19, `pio test -e native`
  - the build id from `firmware_id.sh`
  - builds with `VOLCO_FW_VERSION=<id>` and `VOLCO_FW_DISPLAY=<version>`
  - zips `dist/*` + `THIRD-PARTY-NOTICES.txt` + `OFL.txt` as `volco-firmware-esp32-<version>.zip`
  - uploads the artifact `firmware-esp32` (dist) and the zip
  - outputs `build-id`
- **`release`** (windows, needs both): downloads into `src/Volco.Windows/Firmware/esp32`, publishes with `RequireFirmware=true` and the id, and `gh release create … ./publish/Volco.exe volco-firmware-esp32-<version>.zip`.

- [ ] **Step 1:** Implement. Lint with `actionlint` (download its binary to the scratchpad) on both workflows. Push; CI green. Commit `ci(release): firmware job, embedded firmware, firmware zip with notices; docs (spec 9.3, 13)`.

### Task 9: Hardware checks

- [ ] **On this host** (Linux board, guarded): build `dist` with a build id and flash it with esptool's `write-flash` at the manifest offsets. Then `hello_check`/`config_tool hello` reports that id. This proves the define path end to end.
- [ ] **[USER-RUN] Windows, with a build-job exe** (the user asks for the build), the stage 6 checklist items:
  - **1:** Install on a blank ESP32
  - **5:** Update with a second build id; the config survives
  - **5a:** a `dev` flash shows "Development firmware" with no indicators; Install restores the bundled build
  - **5b:** two CI builds with no firmware change have the same id, so no update is offered
  - **7:** UDP from another PC IP is ignored
  - **9:** unplug during a flash, then Install again succeeds
  - **10:** the probe messages for a blank board, another Volco build, and no ESP32
  - **11:** Install over a configured board → "This controller isn't set up" → Apply my saved setup
  - Also the install time, from `volco.log` (target < 20 s).
