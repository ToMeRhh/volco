# Per-Button OLED Screens Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-button SSD1306 OLED screens to Volco: a new wire protocol, a firmware rendering subsystem, device nicknames and icons, and the app UI that drives and previews them. The user's current LED-only controller must keep working.

**Architecture:**
- **The app (`Volco.Core`)** computes, for each controller button, either an LED value or a fixed-template screen description (`ControllerDisplay`). A new `ControllerLink` owns the controller session: Hello handshake, controller state, keepalive, dedup, and one serialized send path with Begin/Commit bursts. It sends the result over a from-scratch binary protocol.
- **The firmware** reports its hardware (Hello), renders layout 1 itself into its own 1-bit canvas (pure C++ in `lib/`, unit-tested natively), animates locally, and protects the panels against burn-in. U8g2 is used only as the SSD1306 driver.
- **The Avalonia app** gets a Devices page (nicknames and icons), screen previews, indicator-kind-aware button cards, and count hints.

**Tech Stack:**
- **App:** C# / .NET 8, Avalonia 12, CommunityToolkit.Mvvm, xUnit 2 (Core tests) and xUnit v3 + Avalonia.Headless (App tests).
- **Firmware:** ESP32 Arduino via PlatformIO, U8g2 (driver only), Unity for the `native` tests.
- **Assets:** Python 3 + Pillow (asset generation).
- **Simulator:** plain browser JS.

**Spec:** `docs/superpowers/specs/2026-10-02-oled-screens-design.md` (revision 2). Read it before starting any task. Section references below (§n) point into it.

## Global Constraints

**Build and verification**
- **No local `dotnet`, `pio` or `gcc`.** This dev machine can't build. After each task, verify by pushing the branch (which triggers CI tests automatically) and watching the run:
  - `/usr/bin/git push origin HEAD:worktree-displays`
  - `gh run list --branch worktree-displays --limit 1`
  - `gh run watch <id> --exit-status`
- **Never trigger the Windows build job** (no `--field run_build=true`).
- **Use `/usr/bin/git`.** Plain `git` is refused in this worktree. Write commit messages to a file with the Write tool and commit with `/usr/bin/git commit -F <file>`; don't use heredocs.
- **Commit trailer.** Every commit message ends with:
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01V4DyfBnELx1M6H6VN7aVQU
  ```
- **Python and Pillow (10.x) are available locally.** Asset generation (`firmware/tools/gen_assets.py`) runs locally, and its outputs are committed.

**Code boundaries**
- **`Volco.App` and `Volco.Core` stay platform-free** (no P/Invoke, no Windows assemblies). `ArchitectureTests` enforces it.
- **The only `src/Volco.Windows/` change** is the form-factor read in Task 3.

**Protocol values (verbatim from spec §3.2)**
- Types: `Hello 0x01`, `Alive 0x02`, `Slider 0x03`, `Button 0x04`, `HelloRequest 0x81`, `Heartbeat 0x82`, `Led 0x83`, `Screen 0x84`, `Begin 0x85`, `Commit 0x86`.
- Protocol version is `1`.
- Indicator kinds: `0` none, `1` LED, `2` LED pair, `3` screen.
- Transitions: `0` none, `1` slide left, `2` slide right.
- Layout is `1`.
- No-icon is `0xFF`.
- Icon flag bit 0 is the muted slash.
- Each text slot is at most **60 UTF-8 bytes**, cut on a code-point boundary after Hebrew combining marks are removed.

**Timing (spec §3.4, §4.2)**
- Keepalive when idle for 1 s, checked every 250 ms.
- Host timeout 3 s.
- HelloRequest retry every 1 s.
- Begin/Commit fallback 500 ms.
- Slide 360 ms.

**Screen layout 1 (spec §2.1)**
- Top: rows 0–15; text 10 px Regular, baseline row 11.
- Dots: 6×6 squares on a 10 px pitch, rows 5–10.
- Icon: 28×28 at (50, 18).
- Main: 14 px Bold, x 2–125, baseline row 60.
- Long text: hold 2000 ms, scroll 30 px/s, end hold 1200 ms.

**Fixed screen words (spec §2.2):** `MUTED`, `LIVE`, `ACTIVE`, `NEXT: <name>`, `Choose a device`, `No device`, `NOT CONNECTED`, `Volco`.

**Implementation choices that refine the spec.** Task 20 updates the spec text to match.
- **Fonts.** `gen_assets.py` rasterises Arimo with Pillow straight into a small Volco bitmap-font format. There is no `otf2bdf`/`bdfconv`, and no U8g2 font format. Drawing happens in Volco's own 1-bit canvas (`lib/volco_render`), and U8g2 only pushes finished frame buffers to the panels. This makes the whole renderer testable on Linux and makes slides and pixel shifts plain buffer copies (U8g2's 8-bit coordinates cannot draw partly off-screen).
- **App screen preview.** It is an Avalonia control: black panel, Arimo `TextBlock`s at 10/14 px, the icon PNGs with nearest-neighbour scaling. It is not thresholded to 1-bit, because headless test rendering has no real rasteriser. It is close to the device rendering, not pixel-identical, as the spec already allows.

## Review Focus

These are the inputs most likely to bite a real user that no task's main tests would otherwise exercise. Each has a test in the owning task.

1. **Rapid preset presses.** Three Next-preset presses within ~300 ms must leave every screen showing the final preset, with no message stuck in a hold. **Tests:** Task 6 `RapidPresetChanges_EachBurstIsCompleteAndInOrder`; Task 15 `test_new_content_mid_slide_snaps`.
2. **The device a screen shows gets unplugged.** The screen keeps the stored display name and icon, with an empty top and LED 0. Nothing throws. **Test:** Task 5 `UnpluggedDevice_ShowsStoredNameAndIcon`.
3. **Volco restarts while the controller keeps running** (WebSocket/UDP, no ESP32 reset). The new session's HelloRequest gets a Hello and a full push, even though the controller never rebooted. **Test:** Task 7 `Reconnect_WithoutControllerReboot_RequestsHelloAndPushesEverything`.
4. **A long mixed Hebrew/Latin name with digits, over 60 bytes.** The app truncates on a character boundary. The firmware keeps `10:30` and `(…)` in the right order. **Tests:** Task 1 `ToWireBytes_LongMixedHebrewName_CutsOnACharacterBoundary`; Task 14 `test_hebrew_with_time_and_brackets`.
5. **The controller reports fewer buttons than there are cards, then more.** Nothing is sent for missing indices, and missing cards show "Not set up". **Tests:** Task 5 `MoreControllerButtonsThanCards_ExtraButtonsAreNotSetUp` and `FewerControllerButtonsThanCards_ExtraCardsIgnored`.

---

## File map

| Area | Files |
|---|---|
| Core protocol | `src/Volco.Core/Communication/ControllerMessage.cs` (rewrite), `ScreenContent.cs` (new), `ScreenIcon.g.cs` (new, generated), `ScreenText.cs` (new), `ProtocolParser.cs`, `ProtocolEncoder.cs` (rewrite), `WebSocketConnection.cs` |
| Core display | `src/Volco.Core/Devices/DeviceDisplay.cs` (new), `src/Volco.Core/Communication/ControllerDisplay.cs` (new, replaces `ButtonFeedback.cs`) |
| Core session | `src/Volco.Core/Runtime/ControllerLink.cs` (new), `ControllerState.cs` (new), `VolcoRuntime.cs`, `VolcoRuntimeOptions.cs` |
| Core config | `src/Volco.Core/Config/AppConfig.cs`, `DeviceSettingsConfig.cs` (new), `ConfigManager.cs`, `ConfigMigrator.cs` |
| Core audio | `src/Volco.Core/Audio/AudioPrimitives.cs`, `DeviceFormFactor.cs` (new), `src/Volco.Core/Input/SliderProcessor.cs` |
| Windows | `src/Volco.Windows/Audio/DeviceRegistry.cs`, `src/Volco.Windows/NativeMethods.txt` |
| App | `src/Volco.App/Controls/ScreenPreview.axaml(.cs)` (new), `ViewModels/DevicesViewModel.cs` + `DeviceItemViewModel.cs` + `DeviceChoice.cs` (new), `Views/Pages/DevicesPage.axaml(.cs)` (new), `ViewModels/ButtonItemViewModel.cs`, `ButtonsViewModel.cs`, `SlidersViewModel.cs`, `SliderItemViewModel.cs`, `ActionSlotViewModel.cs`, `ActionCatalog.cs`, `ConnectionViewModel.cs`, `SettingsViewModel.cs`, `Views/SettingsWindow.axaml.cs`, `Views/Pages/ButtonsPage.axaml`, `SlidersPage.axaml`, `ConnectionPage.axaml`, `Controls/ActionRow.axaml`, `Volco.App.csproj` |
| Firmware libs (pure C++) | `firmware/lib/volco_protocol/`, `firmware/lib/volco_bidi/`, `firmware/lib/volco_render/` (canvas, fonts, icons, layout, compose), `firmware/lib/volco_screen_state/` |
| Firmware app | `firmware/src/config.h`, `hardware.h`, `transport.h/.cpp`, `indicators.h/.cpp`, `screens.h/.cpp`, `main.cpp`, `firmware/platformio.ini` |
| Firmware assets/tools/tests | `firmware/assets/icons/manifest.txt`, `firmware/assets/icons/*.png`, `firmware/assets/fonts/Arimo-*.ttf` + `LICENSE.txt`, `firmware/tools/draw_icons.py`, `firmware/tools/gen_assets.py`, `firmware/test/golden/frames.txt`, `firmware/test/test_*/` |
| Other | `tools/simulator/index.html`, `tools/simulator/icons.js` (generated), `.github/workflows/build.yml`, `firmware/README.md`, `CLAUDE.md`, both specs |

---

### Task 1: Core wire protocol (messages, parser, encoder, screen text, golden frames)

**Files:**
- Rewrite: `src/Volco.Core/Communication/ControllerMessage.cs`
- Create: `src/Volco.Core/Communication/ScreenContent.cs`
- Create: `src/Volco.Core/Communication/ScreenIcon.g.cs`
- Create: `src/Volco.Core/Communication/ScreenText.cs`
- Modify: `src/Volco.Core/Communication/ProtocolParser.cs` (`ParsePayload` only)
- Rewrite: `src/Volco.Core/Communication/ProtocolEncoder.cs`
- Modify: `src/Volco.Core/Communication/ButtonFeedback.cs` (`BuildFrames` calls `EncodeLed`; the file is deleted in Task 5)
- Create: `firmware/assets/icons/manifest.txt`
- Create: `firmware/test/golden/frames.txt`
- Create: `tests/Volco.Core.Tests/RepoPaths.cs`
- Create: `tests/Volco.Core.Tests/Communication/ProtocolGoldenTests.cs`, `ScreenTextTests.cs`, `ScreenIconManifestTests.cs`
- Rewrite: `tests/Volco.Core.Tests/Communication/ProtocolEncoderTests.cs`
- Modify: `tests/Volco.Core.Tests/Communication/ProtocolParserTests.cs`, `WebSocketConnectionTests.cs` (raw type bytes: Slider `0x01`→`0x03`, Button `0x02`→`0x04`)
- Modify: `tests/Volco.Core.Tests/Communication/ButtonFeedbackTests.cs` and `tests/Volco.Core.Tests/Runtime/VolcoRuntimeTests.cs` (`EncodeButtonFeedback(` → `EncodeLed(`)

**Interfaces**
- **Produces (later tasks rely on these names):**
  - `enum MessageType : byte { Hello=0x01, Alive=0x02, Slider=0x03, Button=0x04, HelloRequest=0x81, Heartbeat=0x82, Led=0x83, Screen=0x84, Begin=0x85, Commit=0x86 }`
  - `HelloMessage(byte Version, int SliderCount, IReadOnlyList<IndicatorKind> Buttons)`
  - `AliveMessage()`
  - `SliderMessage` and `ButtonMessage`, unchanged in shape
  - `enum IndicatorKind : byte { None=0, Led=1, LedPair=2, Screen=3 }`, JSON as string
  - `enum ScreenTransition : byte { None=0, SlideLeft=1, SlideRight=2 }`
  - `enum TopKind : byte { Empty=0, Text=1, Dots=2 }`
  - `record ScreenContent(TopKind TopKind, string TopText, byte DotsSelected, byte DotsCount, ScreenIcon? Icon, bool Muted, string Main)`, with `ScreenContent.Blank`, `ScreenContent.Text(...)`, `ScreenContent.Dots(...)`
  - `enum ScreenIcon : byte { Generic=0, Speaker, Speakers, Headphones, Headset, Earbuds, Display, Mic, StudioMic, Webcam, Presets=10 }`, JSON as string
  - `ScreenText.Sanitize(string?)`, `ScreenText.ToWireBytes(string?)`, `ScreenText.MaxBytes = 60`
  - `ProtocolEncoder.EncodeHelloRequest() / EncodeHeartbeat() / EncodeBegin() / EncodeCommit() / EncodeLed(int, byte) / EncodeScreen(int, ScreenTransition, ScreenContent)`
  - controller-side encoders, for tests and the simulator: `EncodeHello(byte, int, IReadOnlyList<IndicatorKind>) / EncodeAlive() / EncodeSlider(int, ushort) / EncodeButton(int, ButtonState)`
  - `ProtocolEncoder.NoIcon = 0xFF`
  - `ProtocolVersion.Current = 1`

- [ ] **Step 1: Write the golden frames file** `firmware/test/golden/frames.txt`. Both the C# and the firmware tests read it. Every line is `<name> <hex bytes>`; `#` starts a comment.

```text
# Golden wire frames shared by tests/Volco.Core.Tests (ProtocolGoldenTests) and
# firmware/test/test_protocol. Format: <name> <frame bytes in hex>. Spec §3.
hello_led_hardware AA 07 01 01 05 03 01 01 02 04
hello_screen_controller AA 08 01 01 05 04 03 03 03 03 01
alive AA 01 02 02
slider_2_512 AA 04 03 02 02 00 03
button_1_pressed AA 03 04 01 01 04
hello_request AA 01 81 81
heartbeat AA 01 82 82
led_2_value_2 AA 03 83 02 02 83
begin AA 01 85 85
commit AA 01 86 86
screen_muted_mic AA 18 84 01 00 01 01 05 4D 55 54 45 44 07 01 0A 4D 69 63 72 6F 70 68 6F 6E 65 E7
screen_preset_dots_slide_left AA 10 84 00 01 01 02 01 03 0A 00 06 47 61 6D 69 6E 67 A3
screen_blank_slide_right AA 08 84 02 02 01 00 FF 00 00 7A
screen_hebrew_main AA 25 84 03 00 01 01 0E 4E 45 58 54 3A 20 53 70 65 61 6B 65 72 73 01 00 0E D7 A8 D7 9E D7 A7 D7 95 D7 9C D7 99 D7 9D F8
```

What each line encodes:
- `hello_led_hardware`: version 1, 5 sliders, buttons LED, LED, LED pair.
- `hello_screen_controller`: version 1, 5 sliders, 4 screens.
- `slider_2_512`: slider 2 = 512.
- `button_1_pressed`: button 1 pressed.
- `led_2_value_2`: button 2, value 2.
- `screen_muted_mic`: button 1, no transition, top text `MUTED`, icon Mic (7), muted, main `Microphone`.
- `screen_preset_dots_slide_left`: button 0, slide left, dots 1 of 3, icon Presets (10), main `Gaming`.
- `screen_blank_slide_right`: button 2, slide right, empty top, no icon, empty main.
- `screen_hebrew_main`: button 3, top `NEXT: Speakers`, icon Speaker (1), main `רמקולים`.

- [ ] **Step 2: Write the icon manifest** `firmware/assets/icons/manifest.txt`. Task 8's generator reads it; the enum test below reads it now.

```text
# id  file-stem   C#-name     offered-on-devices-page
0     generic     Generic     yes
1     speaker     Speaker     yes
2     speakers    Speakers    yes
3     headphones  Headphones  yes
4     headset     Headset     yes
5     earbuds     Earbuds     yes
6     display     Display     yes
7     mic         Mic         yes
8     studiomic   StudioMic   yes
9     webcam      Webcam      yes
10    presets     Presets     no
```

- [ ] **Step 3: Write the failing tests.** First `tests/Volco.Core.Tests/RepoPaths.cs`:

```csharp
namespace Volco.Core.Tests;

/// <summary>Finds files that live outside the test project (the shared golden frames, the icon
/// manifest) by walking up from the test binary to the folder holding Volco.sln.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Combine(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Volco.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Volco.sln not found above " + AppContext.BaseDirectory);
    }
}
```

`tests/Volco.Core.Tests/Communication/ProtocolGoldenTests.cs`:

```csharp
using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

/// <summary>The C# side of the shared golden frames (firmware/test/golden/frames.txt). The
/// firmware's native tests check the same bytes, so the two implementations cannot drift.</summary>
public class ProtocolGoldenTests
{
    private static readonly Dictionary<string, byte[]> Golden = Load();

    private static Dictionary<string, byte[]> Load()
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var raw in File.ReadAllLines(RepoPaths.Combine("firmware", "test", "golden", "frames.txt")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result[parts[0]] = parts.Skip(1).Select(h => Convert.ToByte(h, 16)).ToArray();
        }
        return result;
    }

    private static List<ControllerMessage> Parse(byte[] bytes)
    {
        var parser = new ProtocolParser();
        var messages = new List<ControllerMessage>();
        parser.MessageParsed += (_, m) => messages.Add(m);
        parser.FeedBytes(bytes);
        return messages;
    }

    [Fact]
    public void PcMessages_EncodeToTheGoldenBytes()
    {
        Assert.Equal(Golden["hello_request"], ProtocolEncoder.EncodeHelloRequest());
        Assert.Equal(Golden["heartbeat"], ProtocolEncoder.EncodeHeartbeat());
        Assert.Equal(Golden["begin"], ProtocolEncoder.EncodeBegin());
        Assert.Equal(Golden["commit"], ProtocolEncoder.EncodeCommit());
        Assert.Equal(Golden["led_2_value_2"], ProtocolEncoder.EncodeLed(2, 2));
        Assert.Equal(Golden["screen_muted_mic"], ProtocolEncoder.EncodeScreen(1, ScreenTransition.None,
            ScreenContent.Text("MUTED", ScreenIcon.Mic, muted: true, "Microphone")));
        Assert.Equal(Golden["screen_preset_dots_slide_left"], ProtocolEncoder.EncodeScreen(0, ScreenTransition.SlideLeft,
            ScreenContent.Dots(1, 3, ScreenIcon.Presets, "Gaming")));
        Assert.Equal(Golden["screen_blank_slide_right"], ProtocolEncoder.EncodeScreen(2, ScreenTransition.SlideRight,
            ScreenContent.Blank));
        Assert.Equal(Golden["screen_hebrew_main"], ProtocolEncoder.EncodeScreen(3, ScreenTransition.None,
            ScreenContent.Text("NEXT: Speakers", ScreenIcon.Speaker, muted: false, "רמקולים")));
    }

    [Fact]
    public void ControllerMessages_EncodeToTheGoldenBytes()
    {
        Assert.Equal(Golden["hello_led_hardware"], ProtocolEncoder.EncodeHello(1, 5,
            new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        Assert.Equal(Golden["alive"], ProtocolEncoder.EncodeAlive());
        Assert.Equal(Golden["slider_2_512"], ProtocolEncoder.EncodeSlider(2, 512));
        Assert.Equal(Golden["button_1_pressed"], ProtocolEncoder.EncodeButton(1, ButtonState.Pressed));
    }

    [Fact]
    public void ControllerMessages_ParseFromTheGoldenBytes()
    {
        var hello = Assert.IsType<HelloMessage>(Assert.Single(Parse(Golden["hello_led_hardware"])));
        Assert.Equal(1, hello.Version);
        Assert.Equal(5, hello.SliderCount);
        Assert.Equal(new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }, hello.Buttons);

        var screens = Assert.IsType<HelloMessage>(Assert.Single(Parse(Golden["hello_screen_controller"])));
        Assert.Equal(Enumerable.Repeat(IndicatorKind.Screen, 4), screens.Buttons);

        Assert.IsType<AliveMessage>(Assert.Single(Parse(Golden["alive"])));

        var slider = Assert.IsType<SliderMessage>(Assert.Single(Parse(Golden["slider_2_512"])));
        Assert.Equal((2, 512), (slider.SliderIndex, (int)slider.Value));

        var button = Assert.IsType<ButtonMessage>(Assert.Single(Parse(Golden["button_1_pressed"])));
        Assert.Equal((1, ButtonState.Pressed), (button.ButtonIndex, button.State));
    }

    [Fact]
    public void PcOnlyMessages_AreNotParsedAsControllerMessages()
    {
        Assert.Empty(Parse(Golden["heartbeat"]));
        Assert.Empty(Parse(Golden["screen_muted_mic"]));
    }

    [Fact]
    public void HelloWithTooFewKindBytes_IsDropped()
    {
        // Claims 3 buttons but carries 2 kinds: AA 06 01 01 05 03 01 01 cs
        byte[] body = { 0x01, 0x01, 0x05, 0x03, 0x01, 0x01 };
        byte cs = 0; foreach (var b in body) cs ^= b;
        Assert.Empty(Parse(new byte[] { 0xAA, (byte)body.Length }.Concat(body).Append(cs).ToArray()));
    }
}
```

`tests/Volco.Core.Tests/Communication/ScreenTextTests.cs`:

```csharp
using System.Text;
using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class ScreenTextTests
{
    [Fact]
    public void Sanitize_RemovesHebrewCombiningMarks_KeepsLetters()
        => Assert.Equal("שלום", ScreenText.Sanitize("שָׁלוֹם"));

    [Fact]
    public void Sanitize_KeepsMaqafAndLatin()
        => Assert.Equal("בית־ספר Desk", ScreenText.Sanitize("בית־ספר Desk"));   // U+05BE is punctuation, not a mark

    [Fact]
    public void ToWireBytes_ShortAscii_IsUnchanged()
        => Assert.Equal(Encoding.UTF8.GetBytes("Speakers"), ScreenText.ToWireBytes("Speakers"));

    [Fact]
    public void ToWireBytes_Null_IsEmpty() => Assert.Empty(ScreenText.ToWireBytes(null));

    [Fact]
    public void ToWireBytes_LongAscii_IsCutAt60Bytes()
        => Assert.Equal(60, ScreenText.ToWireBytes(new string('x', 100)).Length);

    [Fact]
    public void ToWireBytes_Hebrew_CutsOnACharacterBoundary()
    {
        var bytes = ScreenText.ToWireBytes(new string('ש', 40));          // 2 bytes each
        Assert.Equal(60, bytes.Length);
        Assert.Equal(new string('ש', 30), Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ToWireBytes_Emoji_NeverSplitsASurrogatePair()
    {
        var text = new string('a', 58) + "🔊";                                  // 58 + 4 bytes
        var bytes = ScreenText.ToWireBytes(text);
        Assert.Equal(58, bytes.Length);
        Assert.Equal(new string('a', 58), Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ToWireBytes_LongMixedHebrewName_CutsOnACharacterBoundary()
    {
        // Review Focus #4.
        var name = "רמקולים בחדר העבודה (Realtek High Definition Audio) 10:30";
        var bytes = ScreenText.ToWireBytes(name);
        Assert.True(bytes.Length <= 60);
        var decoded = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('�', decoded);
        Assert.StartsWith(decoded, name);
    }
}
```

`tests/Volco.Core.Tests/Communication/ScreenIconManifestTests.cs`:

```csharp
using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

/// <summary>ScreenIcon.g.cs is generated from firmware/assets/icons/manifest.txt; this catches a
/// manifest edit that was not regenerated (spec §5.4).</summary>
public class ScreenIconManifestTests
{
    [Fact]
    public void EnumMatchesTheManifest()
    {
        var rows = File.ReadAllLines(RepoPaths.Combine("firmware", "assets", "icons", "manifest.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        Assert.Equal(rows.Count, Enum.GetValues<ScreenIcon>().Length);
        foreach (var row in rows)
            Assert.Equal(byte.Parse(row[0]), (byte)Enum.Parse<ScreenIcon>(row[2]));
    }
}
```

Rewrite `tests/Volco.Core.Tests/Communication/ProtocolEncoderTests.cs`:

```csharp
using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class ProtocolEncoderTests
{
    private static byte Checksum(byte[] frame)
    {
        byte cs = 0;
        for (int i = 2; i < frame.Length - 1; i++) cs ^= frame[i];
        return cs;
    }

    [Fact]
    public void EncodeLed_FrameShape()
        => Assert.Equal(new byte[] { 0xAA, 0x03, 0x83, 0x07, 0x00, 0x84 }, ProtocolEncoder.EncodeLed(7, 0));

    [Fact]
    public void EncodeScreen_TopTextIsTruncatedTo60Bytes()
    {
        var frame = ProtocolEncoder.EncodeScreen(0, ScreenTransition.None,
            ScreenContent.Text(new string('A', 80), ScreenIcon.Generic, muted: false, "x"));
        Assert.Equal(60, frame[2 + 5]);                       // length byte of the top text
        Assert.Equal(Checksum(frame), frame[^1]);
    }

    [Fact]
    public void EncodeScreen_NoIcon_Is0xFF()
    {
        var frame = ProtocolEncoder.EncodeScreen(0, ScreenTransition.None, ScreenContent.Blank);
        // AA len 84 btn trans layout topKind(0) icon flags mainLen cs
        Assert.Equal(ProtocolEncoder.NoIcon, frame[7]);
    }

    [Fact]
    public void EncodeScreen_LargestMessage_FitsTheFrame()
    {
        var c = ScreenContent.Text(new string('a', 80), ScreenIcon.Generic, muted: true, new string('b', 80));
        var frame = ProtocolEncoder.EncodeScreen(255, ScreenTransition.SlideRight, c);
        Assert.Equal(132, frame.Length);                      // spec §3.3: 129 bytes of type+body
        Assert.Equal(129, frame[1]);
    }

    [Fact]
    public void Roundtrip_ControllerFramesParse()
    {
        var parser = new ProtocolParser();
        var got = new List<ControllerMessage>();
        parser.MessageParsed += (_, m) => got.Add(m);
        parser.FeedBytes(ProtocolEncoder.EncodeSlider(4, 4095));
        parser.FeedBytes(ProtocolEncoder.EncodeButton(0, ButtonState.Released));
        Assert.IsType<SliderMessage>(got[0]);
        Assert.IsType<ButtonMessage>(got[1]);
    }
}
```

In `ProtocolParserTests.cs` and `WebSocketConnectionTests.cs`, change every raw payload type byte:
- Slider `0x01` → `0x03`. The slider payloads in `ParsesSliderMessage`, `ParsesMultipleMessagesInOneBuffer`, `ParsesMessageSplitAcrossMultipleFeeds` and the WebSocket tests start with `0x01`.
- Button `0x02` → `0x04`.

`ParsesFrameWithEmbeddedStartMarkerInPayload` changes to:

```csharp
    [Fact]
    public void ParsesFrameWithEmbeddedStartMarkerInPayload()
    {
        // Payload: { messageType=0x04, 0xAA, value=0x01 }; checksum: 0x04 ^ 0xAA ^ 0x01 = 0xAF
        var frame = new byte[] { 0xAA, 0x03, 0x04, 0xAA, 0x01, 0xAF };
        _parser.FeedBytes(frame);

        Assert.Single(_received);
        var msg = Assert.IsType<ButtonMessage>(_received[0]);
        Assert.Equal(0xAA, (int)msg.ButtonIndex);
        Assert.Equal(ButtonState.Pressed, msg.State);
    }
```

Add to `ProtocolParserTests.cs`:

```csharp
    [Fact]
    public void ParsesAlive_WhichHasNoBody()
    {
        _parser.FeedBytes(new byte[] { 0xAA, 0x01, 0x02, 0x02 });
        Assert.IsType<AliveMessage>(Assert.Single(_received));
    }

    [Fact]
    public void DropsABodyTooShortForItsType()
    {
        _parser.FeedBytes(FramePayload(new byte[] { 0x03, 0x01 }));   // slider with no value
        Assert.Empty(_received);
    }
```

In `ButtonFeedbackTests.cs` and `VolcoRuntimeTests.cs`, replace `ProtocolEncoder.EncodeButtonFeedback(` with `ProtocolEncoder.EncodeLed(`. These files are rewritten in Tasks 5 and 7; this only keeps them compiling.

- [ ] **Step 4: Implement.** `src/Volco.Core/Communication/ControllerMessage.cs`:

```csharp
namespace Volco.Core.Communication;

/// <summary>
/// Wire message types (spec §3.2). The high bit is the direction: 0x0_ comes from the controller,
/// 0x8_ from the PC. Paired messages share their low bits (Hello/HelloRequest, Alive/Heartbeat).
/// </summary>
public enum MessageType : byte
{
    Hello = 0x01,
    Alive = 0x02,
    Slider = 0x03,
    Button = 0x04,
    HelloRequest = 0x81,
    Heartbeat = 0x82,
    Led = 0x83,
    Screen = 0x84,
    Begin = 0x85,
    Commit = 0x86,
}

/// <summary>The protocol version this app speaks; Hello carries the firmware's.</summary>
public static class ProtocolVersion
{
    public const byte Current = 1;
}

public enum ButtonState : byte
{
    Released = 0x00,
    Pressed = 0x01
}

public abstract class ControllerMessage
{
    public MessageType Type { get; }
    protected ControllerMessage(MessageType type) => Type = type;
}

public class SliderMessage : ControllerMessage
{
    public int SliderIndex { get; }
    public ushort Value { get; }

    public SliderMessage(int sliderIndex, ushort value) : base(MessageType.Slider)
    {
        SliderIndex = sliderIndex;
        Value = value;
    }
}

public class ButtonMessage : ControllerMessage
{
    public int ButtonIndex { get; }
    public ButtonState State { get; }

    public ButtonMessage(int buttonIndex, ButtonState state) : base(MessageType.Button)
    {
        ButtonIndex = buttonIndex;
        State = state;
    }
}

/// <summary>The controller describing its hardware (spec §3.2): sent at boot, in reply to every
/// HelloRequest, and on the first frame after a host timeout.</summary>
public sealed class HelloMessage : ControllerMessage
{
    public byte Version { get; }
    public int SliderCount { get; }

    /// <summary>One indicator kind per button, by hardware input number.</summary>
    public IReadOnlyList<IndicatorKind> Buttons { get; }

    public HelloMessage(byte version, int sliderCount, IReadOnlyList<IndicatorKind> buttons) : base(MessageType.Hello)
    {
        Version = version;
        SliderCount = sliderCount;
        Buttons = buttons;
    }
}

/// <summary>The controller's keepalive: sent when it has sent nothing else for a second.</summary>
public sealed class AliveMessage : ControllerMessage
{
    public AliveMessage() : base(MessageType.Alive) { }
}
```

`src/Volco.Core/Communication/ScreenContent.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Volco.Core.Communication;

/// <summary>How a button shows its state, as the controller reports it in Hello (spec §3.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IndicatorKind : byte
{
    None = 0,
    Led = 1,
    LedPair = 2,
    Screen = 3,
}

/// <summary>How a screen changes to new content (spec §3.3). Unknown values mean None.</summary>
public enum ScreenTransition : byte
{
    None = 0,
    SlideLeft = 1,
    SlideRight = 2,
}

/// <summary>What layout 1's top band (rows 0–15) holds.</summary>
public enum TopKind : byte
{
    Empty = 0,
    Text = 1,
    Dots = 2,
}

/// <summary>
/// One screen in layout 1 ("C nudged", spec §2.1): top slot, icon (+ muted slash), main text.
/// Text is in normal reading order; <see cref="ProtocolEncoder"/> sanitizes and truncates it.
/// A record, so two contents compare by value (the runtime's dedup relies on it).
/// </summary>
public sealed record ScreenContent(
    TopKind TopKind,
    string TopText,
    byte DotsSelected,
    byte DotsCount,
    ScreenIcon? Icon,
    bool Muted,
    string Main)
{
    /// <summary>The only layout this app sends.</summary>
    public const byte Layout = 1;

    /// <summary>A blank screen ("Not set up").</summary>
    public static ScreenContent Blank { get; } = new(TopKind.Empty, "", 0, 0, null, false, "");

    /// <summary>Top text (empty text means an empty top), icon, main.</summary>
    public static ScreenContent Text(string top, ScreenIcon? icon, bool muted, string main)
        => new(top.Length == 0 ? TopKind.Empty : TopKind.Text, top, 0, 0, icon, muted, main);

    /// <summary>Preset dots on top (selected is 0-based), icon, main.</summary>
    public static ScreenContent Dots(byte selected, byte count, ScreenIcon? icon, string main)
        => new(TopKind.Dots, "", selected, count, icon, false, main);
}
```

`src/Volco.Core/Communication/ScreenIcon.g.cs` (Task 8's generator later rewrites this file with identical content):

```csharp
// <auto-generated>
// Generated by firmware/tools/gen_assets.py from firmware/assets/icons/manifest.txt. Do not edit.
// </auto-generated>
using System.Text.Json.Serialization;

namespace Volco.Core.Communication;

/// <summary>The firmware's closed icon set (spec §4.5). IDs are stable: append, never insert.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenIcon : byte
{
    Generic = 0,
    Speaker = 1,
    Speakers = 2,
    Headphones = 3,
    Headset = 4,
    Earbuds = 5,
    Display = 6,
    Mic = 7,
    StudioMic = 8,
    Webcam = 9,
    Presets = 10,
}
```

`src/Volco.Core/Communication/ScreenText.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace Volco.Core.Communication;

/// <summary>Text rules for screen slots (spec §3.3): Hebrew combining marks removed (the firmware
/// cannot stack them), then at most 60 UTF-8 bytes, cut on a code-point boundary.</summary>
public static class ScreenText
{
    public const int MaxBytes = 60;

    /// <summary>Removes non-spacing marks in U+0591–U+05C7 (niqqud and cantillation). Punctuation
    /// in that block, like maqaf U+05BE, stays.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is >= '֑' and <= 'ׇ'
                && CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>The sanitized text as UTF-8, cut to <see cref="MaxBytes"/> without splitting a
    /// character (a surrogate pair is one rune, so an emoji is never halved).</summary>
    public static byte[] ToWireBytes(string? text)
    {
        var result = new List<byte>(MaxBytes);
        Span<byte> buf = stackalloc byte[4];
        foreach (var rune in Sanitize(text).EnumerateRunes())
        {
            var n = rune.EncodeToUtf8(buf);
            if (result.Count + n > MaxBytes) break;
            for (int i = 0; i < n; i++) result.Add(buf[i]);
        }
        return result.ToArray();
    }
}
```

`src/Volco.Core/Communication/ProtocolEncoder.cs`:

```csharp
namespace Volco.Core.Communication;

/// <summary>
/// Builds wire frames: <c>0xAA | length | type | body | XOR(type, body)</c> (spec §3.1). The PC
/// sends the first group; the controller-side encoders exist for tests, the golden frames and the
/// simulator's expectations.
/// </summary>
public static class ProtocolEncoder
{
    private const byte StartMarker = 0xAA;

    /// <summary>The icon byte for "no icon".</summary>
    public const byte NoIcon = 0xFF;

    // ── PC → controller ─────────────────────────────────────────────────────

    public static byte[] EncodeHelloRequest() => BuildFrame(new[] { (byte)MessageType.HelloRequest });
    public static byte[] EncodeHeartbeat() => BuildFrame(new[] { (byte)MessageType.Heartbeat });
    public static byte[] EncodeBegin() => BuildFrame(new[] { (byte)MessageType.Begin });
    public static byte[] EncodeCommit() => BuildFrame(new[] { (byte)MessageType.Commit });

    /// <summary>LED: lit when value ≠ 0. LED pair: 1 lights A, 2 lights B, anything else neither.</summary>
    public static byte[] EncodeLed(int button, byte value)
        => BuildFrame(new[] { (byte)MessageType.Led, (byte)button, value });

    /// <summary>One screen in layout 1 (spec §3.3).</summary>
    public static byte[] EncodeScreen(int button, ScreenTransition transition, ScreenContent content)
    {
        var body = new List<byte>(132)
        {
            (byte)MessageType.Screen, (byte)button, (byte)transition, ScreenContent.Layout, (byte)content.TopKind,
        };
        switch (content.TopKind)
        {
            case TopKind.Text:
                AppendText(body, content.TopText);
                break;
            case TopKind.Dots:
                body.Add(content.DotsSelected);
                body.Add(content.DotsCount);
                break;
        }
        body.Add(content.Icon is { } icon ? (byte)icon : NoIcon);
        body.Add(content.Muted ? (byte)1 : (byte)0);
        AppendText(body, content.Main);
        return BuildFrame(body.ToArray());
    }

    // ── Controller → PC (tests, golden frames) ─────────────────────────────

    public static byte[] EncodeHello(byte version, int sliders, IReadOnlyList<IndicatorKind> buttons)
    {
        var body = new List<byte> { (byte)MessageType.Hello, version, (byte)sliders, (byte)buttons.Count };
        body.AddRange(buttons.Select(k => (byte)k));
        return BuildFrame(body.ToArray());
    }

    public static byte[] EncodeAlive() => BuildFrame(new[] { (byte)MessageType.Alive });

    public static byte[] EncodeSlider(int index, ushort value)
        => BuildFrame(new[] { (byte)MessageType.Slider, (byte)index, (byte)(value >> 8), (byte)(value & 0xFF) });

    public static byte[] EncodeButton(int index, ButtonState state)
        => BuildFrame(new[] { (byte)MessageType.Button, (byte)index, (byte)state });

    private static void AppendText(List<byte> body, string text)
    {
        var bytes = ScreenText.ToWireBytes(text);
        body.Add((byte)bytes.Length);
        body.AddRange(bytes);
    }

    private static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[payload.Length + 3]; // marker + length + payload + checksum
        frame[0] = StartMarker;
        frame[1] = (byte)payload.Length;
        byte checksum = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            frame[2 + i] = payload[i];
            checksum ^= payload[i];
        }
        frame[^1] = checksum;
        return frame;
    }
}
```

In `ProtocolParser.cs`, replace `ParsePayload` (and nothing else):

```csharp
    private static ControllerMessage? ParsePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return null;

        switch ((MessageType)payload[0])
        {
            case MessageType.Hello when payload.Length >= 4:
            {
                int buttons = payload[3];
                if (payload.Length < 4 + buttons) return null;
                var kinds = new IndicatorKind[buttons];
                for (int i = 0; i < buttons; i++) kinds[i] = (IndicatorKind)payload[4 + i];
                return new HelloMessage(payload[1], payload[2], kinds);
            }
            case MessageType.Alive:
                return new AliveMessage();
            case MessageType.Slider when payload.Length >= 4:
                return new SliderMessage(payload[1], (ushort)((payload[2] << 8) | payload[3]));
            case MessageType.Button when payload.Length >= 3:
                return new ButtonMessage(payload[1], (ButtonState)payload[2]);
            default:
                return null;   // PC → controller types, unknown types, or a body too short for its type
        }
    }
```

In `ButtonFeedback.cs`, change `BuildFrames` to use `ProtocolEncoder.EncodeLed(i, values[i])`.

- [ ] **Step 5: Commit and verify in CI**

```bash
/usr/bin/git add -A src/Volco.Core/Communication tests/Volco.Core.Tests firmware/assets/icons/manifest.txt firmware/test/golden/frames.txt
/usr/bin/git commit -F <msgfile>    # "feat(core): new wire protocol messages, encoder and golden frames"
/usr/bin/git push origin HEAD:worktree-displays
gh run list --branch worktree-displays --limit 1   # then: gh run watch <id> --exit-status
```
Expected: the "Run Tests" job passes. If it fails, fix the cause and push again before moving on.

---

### Task 2: Config schema v4 and the migration guard

**Files:**
- Modify: `src/Volco.Core/Config/AppConfig.cs` (`CurrentSchemaVersion = 4`, `Devices`, `Controller`, `Normalize`)
- Create: `src/Volco.Core/Config/DeviceSettingsConfig.cs`
- Modify: `src/Volco.Core/Config/ConfigMigrator.cs` (`NeedsMigration`)
- Modify: `src/Volco.Core/Config/ConfigManager.cs` (`Load`: the v3 → v4 step)
- Test: `tests/Volco.Core.Tests/Config/ConfigManagerTests.cs`, `ConfigMigratorTests.cs`

**Interfaces**
- **Produces:**
  - `class DeviceSettingsConfig { string Id; AudioFlow Flow; string FriendlyName; string Nickname; ScreenIcon? Icon; bool IsEmpty }`
  - `class ControllerInfoConfig { int ProtocolVersion; int Sliders; List<IndicatorKind> Buttons; bool SameAs(ControllerInfoConfig?) }`
  - `AppConfig.Devices`, `AppConfig.Controller`
  - `AppConfig.FindDevice(string id)`

- [ ] **Step 1: Write the failing tests.** Add to `ConfigManagerTests.cs`, below `V2Json`:

```csharp
    /// <summary>A realistic v3 config.json, as the v3 app wrote it (the user's current hardware).
    /// If the user supplies their real config.json, paste it here instead.</summary>
    internal const string V3Json = """
    {
      "schemaVersion": 3,
      "connection": { "type": "Serial", "serial": { "comPort": "COM3", "baudRate": 115200 }, "udp": { "listenPort": 12345 }, "webSocket": { "port": 8765 } },
      "sliders": [
        { "index": 0, "type": "Device", "processes": [], "device": null, "invert": false, "curve": "Linear", "deadZonePercent": 0, "smoothingFactor": 0 },
        { "index": 1, "type": "Processes", "processes": [ "spotify.exe" ], "device": null, "invert": false, "curve": "Logarithmic", "deadZonePercent": 0.02, "smoothingFactor": 0.2 }
      ],
      "buttons": [
        { "index": 0, "actions": { "default": { "singlePress": { "action": "MuteCurrentInput" }, "doublePress": { "action": "None" }, "longPress": { "action": "NextPreset" } } }, "longPressMs": 600, "doublePressWindowMs": 250 },
        { "index": 1, "actions": { "default": { "singlePress": { "action": "ToggleOutputDevice", "device": { "id": "dev-1", "friendlyName": "Speakers" }, "deviceB": { "id": "dev-2", "friendlyName": "Headphones" } } } }, "longPressMs": 500, "doublePressWindowMs": 300 }
      ],
      "presets": [ { "id": "default", "name": "Default" }, { "id": "preset-2", "name": "Gaming" } ],
      "updates": { "gitHubToken": "ghp_x", "useCiArtifact": false },
      "debugLogging": false
    }
    """;

    [Fact]
    public void Load_V3Config_StampsV4_KeepsEverything_AndAddsEmptyDeviceAndControllerInfo()
    {
        File.WriteAllText(_configPath, V3Json);

        var cm = new ConfigManager(_configPath);
        cm.Load();
        var c = cm.Config;

        Assert.Equal(4, c.SchemaVersion);
        Assert.Empty(c.Devices);
        Assert.Null(c.Controller);
        Assert.Equal(2, c.Presets.Count);
        Assert.Equal(ButtonActionType.NextPreset, c.Buttons[0].Default().LongPress.Action);
        Assert.Equal("dev-2", c.Buttons[1].Default().SinglePress.DeviceB!.Id);
        Assert.Equal(ResponseCurve.Logarithmic, c.Sliders[1].Curve);

        Assert.Contains("\"schemaVersion\": 4", File.ReadAllText(_configPath));
        Assert.Equal(V3Json, File.ReadAllText(Path.Combine(_tempDir, "config.v3.bak")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "config.v1.bak")));
        Assert.False(ConfigMigrator.NeedsMigration(c));
    }

    [Fact]
    public void DevicesAndControllerInfo_RoundTrip()
    {
        var cm = new ConfigManager(_configPath);
        cm.Load();
        cm.Config.Devices.Add(new DeviceSettingsConfig
        {
            Id = "dev-1", Flow = Volco.Core.Audio.AudioFlow.Render, FriendlyName = "Speakers (Realtek)",
            Nickname = "Desk speakers", Icon = Volco.Core.Communication.ScreenIcon.Speakers,
        });
        cm.Config.Controller = new ControllerInfoConfig
        {
            ProtocolVersion = 1, Sliders = 5,
            Buttons = { Volco.Core.Communication.IndicatorKind.Led, Volco.Core.Communication.IndicatorKind.LedPair },
        };
        cm.Save();

        var json = File.ReadAllText(_configPath);
        Assert.Contains("\"icon\": \"Speakers\"", json);
        Assert.Contains("\"LedPair\"", json);
        Assert.Contains("\"flow\": \"Render\"", json);

        var again = new ConfigManager(_configPath);
        again.Load();
        var d = Assert.Single(again.Config.Devices);
        Assert.Equal("Desk speakers", d.Nickname);
        Assert.Equal(Volco.Core.Communication.ScreenIcon.Speakers, d.Icon);
        Assert.True(again.Config.Controller!.SameAs(cm.Config.Controller));
    }
```

Update the two existing v2 tests to expect the current version:
- In `Load_V2Config_MigratesToV3_WithoutLosingAnything`, `Assert.Equal(3, c.SchemaVersion)` → `Assert.Equal(AppConfig.CurrentSchemaVersion, c.SchemaVersion)`.
- In `Load_V2Config_WritesV3ToDisk_AndKeepsAV2Backup`, `"\"schemaVersion\": 3"` → `"\"schemaVersion\": 4"`.

Add to `ConfigMigratorTests.cs`:

```csharp
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void NeedsMigration_OnlyForV1Files(int version, bool expected)
        => Assert.Equal(expected, ConfigMigrator.NeedsMigration(new AppConfig { SchemaVersion = version }));
```

Run `grep -n "NeedsMigration\|CurrentSchemaVersion\|SchemaVersion = 3\|SchemaVersion)" tests/Volco.Core.Tests/Config/ConfigMigratorTests.cs`. Update any existing assertion that a v2 or v3 config "needs migration", or that expects a literal 3, to the new rule and value.

- [ ] **Step 2: Implement.** `src/Volco.Core/Config/DeviceSettingsConfig.cs`:

```csharp
using System.Text.Json.Serialization;
using Volco.Core.Audio;
using Volco.Core.Communication;

namespace Volco.Core.Config;

/// <summary>
/// The user's settings for one audio device (Devices page, spec §5.1). Only devices the user
/// customized have an entry; an entry with no nickname and no icon is removed.
/// </summary>
public class DeviceSettingsConfig
{
    /// <summary>The endpoint id: the device's identity.</summary>
    public string Id { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AudioFlow Flow { get; set; } = AudioFlow.Render;

    /// <summary>The last name Windows reported, so a disconnected device still has a name.</summary>
    public string FriendlyName { get; set; } = "";

    /// <summary>"" = use the automatic short name.</summary>
    public string Nickname { get; set; } = "";

    /// <summary>null = automatic (from the device's form factor).</summary>
    public ScreenIcon? Icon { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Nickname) && Icon is null;
}

/// <summary>The last Hello the controller sent (spec §5.1), so the UI knows the hardware while
/// the controller is unplugged.</summary>
public class ControllerInfoConfig
{
    public int ProtocolVersion { get; set; }
    public int Sliders { get; set; }

    /// <summary>Indicator kind per button, by hardware input number.</summary>
    public List<IndicatorKind> Buttons { get; set; } = new();

    public bool SameAs(ControllerInfoConfig? other)
        => other is not null && ProtocolVersion == other.ProtocolVersion && Sliders == other.Sliders
           && Buttons.SequenceEqual(other.Buttons);
}
```

In `AppConfig.cs`:
- `public const int CurrentSchemaVersion = 4;`
- After `Presets`, add:

```csharp
    /// <summary>Device nicknames and icons (Devices page). Only customized devices.</summary>
    public List<DeviceSettingsConfig> Devices { get; set; } = new();

    /// <summary>The last Hello the controller sent; null until the first one.</summary>
    public ControllerInfoConfig? Controller { get; set; }

    /// <summary>The settings entry for an endpoint id, or null.</summary>
    public DeviceSettingsConfig? FindDevice(string? id)
        => string.IsNullOrEmpty(id) ? null
           : Devices.Find(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
```

- In `Normalize()`, after `Presets ??= new();`, add `Devices ??= new();` and `Devices.RemoveAll(d => d is null || string.IsNullOrEmpty(d.Id));`.

In `ConfigMigrator.cs`, replace `NeedsMigration` and its doc:

```csharp
    /// <summary>True only for a schema v1 file (no "schemaVersion", or 0/1): its device names still
    /// need resolving against live devices. v2 and v3 are upgraded in <see cref="ConfigManager.Load"/>
    /// and must never come through here (that would write config.v1.bak and re-parse them as v1).</summary>
    public static bool NeedsMigration(AppConfig config)
        => config.SchemaVersion < 2;
```

In `ConfigManager.Load`, replace the `if (version < 3) { … }` block with:

```csharp
                    if (version < 3)
                    {
                        // v1 and v2 both get the v3 shape here. v2 is then current; v1 keeps its
                        // number until the runtime resolves its device names (ConfigMigrator.MigrateFile).
                        ConfigMigrator.UpgradeShapeToV3(root);
                        if (version == 2)
                        {
                            BackupBeforeMigration(json, "config.v2.bak");
                            root["schemaVersion"] = AppConfig.CurrentSchemaVersion;
                            migratedFrom = 2;
                        }
                        json = root.ToJsonString();
                    }
                    else if (version == 3)
                    {
                        // v3 → v4 only adds "devices" and "controller", which deserialize to their
                        // defaults when missing: stamp the version and keep a backup.
                        BackupBeforeMigration(json, "config.v3.bak");
                        root["schemaVersion"] = AppConfig.CurrentSchemaVersion;
                        migratedFrom = 3;
                        json = root.ToJsonString();
                    }
```

Rename the local `migratedFromV2` (bool) to `int migratedFrom = 0`. Also:
- in the catch, set `migratedFrom = 0`;
- the write-out block becomes `if (migratedFrom != 0) { AppLog.Info($"Migrated config from schema v{migratedFrom} to v{AppConfig.CurrentSchemaVersion}"); … }`.

- [ ] **Step 3: Commit and verify in CI** (same commands as Task 1). Message: `feat(core): config schema v4 (devices, controller info) and the v1-only migration guard`.

---

### Task 3: Device form factor (Core + the one Windows change)

**Files:**
- Create: `src/Volco.Core/Audio/DeviceFormFactor.cs`
- Modify: `src/Volco.Core/Audio/AudioPrimitives.cs` (`AudioDevice` gets a trailing `FormFactor` parameter)
- Modify: `src/Volco.Windows/Audio/DeviceRegistry.cs` (read `PKEY_AudioEndpoint_FormFactor`)
- Modify: `src/Volco.Windows/NativeMethods.txt` (add `PKEY_AudioEndpoint_FormFactor`)
- Test: `tests/Volco.Core.Tests/Audio/AudioPrimitivesTests.cs`

**Interfaces**
- **Produces:**
  - `enum DeviceFormFactor { Unknown, Speakers, Headphones, Headset, Microphone, Display, Other }`
  - `AudioDevice(string Id, string FriendlyName, AudioFlow Flow, bool IsDefault, DeviceFormFactor FormFactor = DeviceFormFactor.Unknown)`

- [ ] **Step 1: Write the failing test** in `AudioPrimitivesTests.cs`:

```csharp
    [Fact]
    public void AudioDevice_FormFactorDefaultsToUnknown_AndIsPartOfEquality()
    {
        var plain = new AudioDevice("a", "A", AudioFlow.Render, false);
        Assert.Equal(DeviceFormFactor.Unknown, plain.FormFactor);
        Assert.NotEqual(plain, plain with { FormFactor = DeviceFormFactor.Headset });
    }
```

- [ ] **Step 2: Implement.** `src/Volco.Core/Audio/DeviceFormFactor.cs`:

```csharp
namespace Volco.Core.Audio;

/// <summary>What kind of device an endpoint is, as Windows reports it (PKEY_AudioEndpoint_FormFactor),
/// folded to what Volco uses to pick an automatic icon (spec §5.2).</summary>
public enum DeviceFormFactor
{
    Unknown,
    Speakers,
    Headphones,
    Headset,
    Microphone,
    Display,
    Other,
}
```

In `AudioPrimitives.cs`:

```csharp
public sealed record AudioDevice(
    string    Id,
    string    FriendlyName,
    AudioFlow Flow,
    bool      IsDefault,
    DeviceFormFactor FormFactor = DeviceFormFactor.Unknown);
```

In `src/Volco.Windows/NativeMethods.txt`, add a line `PKEY_AudioEndpoint_FormFactor` next to `PKEY_Device_FriendlyName`.

In `DeviceRegistry.cs`:
- Next to `GetFriendlyName`, add a reader with the same property-store pattern. Windows stores the form factor as `VT_UI4` holding the `EndpointFormFactor` enum: `RemoteNetworkDevice=0, Speakers=1, LineLevel=2, Headphones=3, Microphone=4, Headset=5, Handset=6, UnknownDigitalPassthrough=7, SPDIF=8, DigitalAudioDisplayDevice=9, UnknownFormFactor=10`.

```csharp
    private static DeviceFormFactor GetFormFactor(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out IPropertyStore store);
            try
            {
                store.GetValue(in PInvoke.PKEY_AudioEndpoint_FormFactor, out PROPVARIANT value);
                try
                {
                    if (value.Anonymous.Anonymous.vt != VARENUM.VT_UI4) return DeviceFormFactor.Unknown;
                    return value.Anonymous.Anonymous.Anonymous.ulVal switch
                    {
                        1 => DeviceFormFactor.Speakers,
                        3 => DeviceFormFactor.Headphones,
                        4 => DeviceFormFactor.Microphone,
                        5 or 6 => DeviceFormFactor.Headset,
                        9 => DeviceFormFactor.Display,
                        _ => DeviceFormFactor.Other,
                    };
                }
                finally
                {
                    PInvoke.PropVariantClear(ref value);
                }
            }
            finally
            {
                ReleaseIfComObject(store);
            }
        }
        catch (COMException)
        {
            return DeviceFormFactor.Unknown;
        }
    }
```

This uses the file's own `ReleaseIfComObject` helper, as `GetFriendlyName` does. Then:
- Add `public DeviceFormFactor FormFactor { get; init; }` to `DeviceHandle` (line ~46, under `FriendlyName`).
- Where the handle is built (line ~473, `string friendlyName = GetFriendlyName(device);`), add `var formFactor = GetFormFactor(device);`.
- Set `FormFactor = formFactor,` in the initializer next to `FriendlyName = friendlyName,` (line ~510).
- Pass it at line ~789: `return new AudioDevice(handle.Id, handle.FriendlyName, handle.Flow, isDefault, handle.FormFactor);`

`Volco.Windows` only builds on the Windows job, which is not run here. Read the handle type and its construction sites carefully so the change compiles; the Linux CI only covers Core.

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(audio): report each endpoint's form factor`.

---

### Task 4: `DeviceDisplay`, display names and icons for devices

**Files:**
- Create: `src/Volco.Core/Devices/DeviceDisplay.cs`
- Test: `tests/Volco.Core.Tests/Devices/DeviceDisplayTests.cs`

**Interfaces**
- **Consumes:** `AppConfig.FindDevice` (Task 2), `AudioDevice.FormFactor` and `DeviceFormFactor` (Task 3), `ScreenIcon` (Task 1).
- **Produces:**
  - `readonly record struct DeviceLook(string Name, ScreenIcon Icon)`
  - `DeviceDisplay.AutoShortName(string)`
  - `DeviceDisplay.IconFor(DeviceFormFactor)`
  - `DeviceDisplay.For(AppConfig, AudioDevice) → DeviceLook`
  - `DeviceDisplay.For(AppConfig, IAudioEngine, DeviceRefConfig?) → DeviceLook?`
  - `DeviceDisplay.FindLive(IAudioEngine, DeviceRefConfig?) → AudioDevice?`
  - `DeviceDisplay.NameOf(AppConfig, IAudioEngine, DeviceRefConfig?, string fallback) → string`

- [ ] **Step 1: Write the failing tests** `tests/Volco.Core.Tests/Devices/DeviceDisplayTests.cs`:

```csharp
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Devices;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Devices;

public class DeviceDisplayTests
{
    private static readonly AudioDevice Speakers =
        new("spk", "Speakers (Realtek High Definition Audio)", AudioFlow.Render, true, DeviceFormFactor.Speakers);
    private static readonly AudioDevice Headset =
        new("hs", "Headset Earphone (Arctis Nova 7)", AudioFlow.Render, false, DeviceFormFactor.Headset);
    private static readonly AudioDevice Mic =
        new("mic", "Microphone (Yeti Stereo Microphone)", AudioFlow.Capture, true, DeviceFormFactor.Microphone);

    private readonly FakeAudioEngine _audio = new() { Devices = { Speakers, Headset, Mic } };

    [Theory]
    [InlineData("Speakers (Realtek High Definition Audio)", "Speakers")]
    [InlineData("Speakers (Realtek(R) Audio)", "Speakers")]
    [InlineData("רמקולים (USB Audio Device)", "רמקולים")]
    [InlineData("(Only parentheses)", "(Only parentheses)")]
    [InlineData("LG HDR 4K", "LG HDR 4K")]
    [InlineData("  Padded (x)  ", "Padded")]
    public void AutoShortName_DropsATrailingParenthesisedPart(string windows, string expected)
        => Assert.Equal(expected, DeviceDisplay.AutoShortName(windows));

    [Fact]
    public void LiveDevice_WithoutSettings_UsesShortNameAndFormFactorIcon()
    {
        var look = DeviceDisplay.For(AppConfig.CreateDefault(), Headset);
        Assert.Equal(new DeviceLook("Headset Earphone", ScreenIcon.Headset), look);
    }

    [Fact]
    public void Nickname_AndChosenIcon_Win()
    {
        var c = AppConfig.CreateDefault();
        c.Devices.Add(new DeviceSettingsConfig { Id = "SPK", Nickname = " Desk speakers ", Icon = ScreenIcon.Speakers });
        Assert.Equal(new DeviceLook("Desk speakers", ScreenIcon.Speakers), DeviceDisplay.For(c, Speakers));
    }

    [Fact]
    public void UnknownFormFactor_IsGeneric()
        => Assert.Equal(ScreenIcon.Generic, DeviceDisplay.IconFor(DeviceFormFactor.Other));

    [Fact]
    public void Reference_ResolvesTheLiveDeviceById_ThenByName()
    {
        var c = AppConfig.CreateDefault();
        Assert.Equal("Speakers", DeviceDisplay.For(c, _audio, new DeviceRefConfig { Id = "spk", FriendlyName = "old" })!.Value.Name);
        Assert.Equal("Microphone", DeviceDisplay.For(c, _audio,
            new DeviceRefConfig { Id = "", FriendlyName = "Microphone (Yeti Stereo Microphone)" })!.Value.Name);
    }

    [Fact]
    public void DisconnectedReference_UsesStoredSettings_ThenTheReferenceName()
    {
        var c = AppConfig.CreateDefault();
        c.Devices.Add(new DeviceSettingsConfig { Id = "sony", FriendlyName = "Headphones (WH-1000XM4)", Icon = ScreenIcon.Headphones });
        Assert.Equal(new DeviceLook("Headphones", ScreenIcon.Headphones),
            DeviceDisplay.For(c, _audio, new DeviceRefConfig { Id = "sony", FriendlyName = "ignored" }));
        Assert.Equal(new DeviceLook("Gone", ScreenIcon.Generic),
            DeviceDisplay.For(c, _audio, new DeviceRefConfig { Id = "x", FriendlyName = "Gone (USB)" }));
    }

    [Fact]
    public void EmptyReference_IsNull()
    {
        var c = AppConfig.CreateDefault();
        Assert.Null(DeviceDisplay.For(c, _audio, null));
        Assert.Null(DeviceDisplay.For(c, _audio, new DeviceRefConfig()));
    }

    [Fact]
    public void NameOf_FallsBackWhenTheReferenceIsEmpty()
        => Assert.Equal("Output", DeviceDisplay.NameOf(AppConfig.CreateDefault(), _audio, null, "Output"));
}
```

- [ ] **Step 2: Implement** `src/Volco.Core/Devices/DeviceDisplay.cs`:

```csharp
using System.Text.RegularExpressions;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;

namespace Volco.Core.Devices;

/// <summary>How a device appears everywhere Volco names it: screens, the OSD, the settings UI.</summary>
public readonly record struct DeviceLook(string Name, ScreenIcon Icon);

/// <summary>
/// Display names and icons for devices (spec §2.2, §5.3). Name: the nickname, else the automatic
/// short name (the Windows name minus a trailing parenthesised part). Icon: the chosen icon, else
/// the one for the device's form factor, else <see cref="ScreenIcon.Generic"/>.
/// </summary>
public static partial class DeviceDisplay
{
    [GeneratedRegex(@"^(.*?)\s*\(.*\)\s*$")]
    private static partial Regex TrailingParenthesis();

    /// <summary>"Speakers (Realtek High Definition Audio)" → "Speakers". A name that would be
    /// empty without its parentheses is kept whole.</summary>
    public static string AutoShortName(string? friendlyName)
    {
        var name = (friendlyName ?? "").Trim();
        var m = TrailingParenthesis().Match(name);
        return m.Success && m.Groups[1].Value.Trim().Length > 0 ? m.Groups[1].Value.Trim() : name;
    }

    public static ScreenIcon IconFor(DeviceFormFactor formFactor) => formFactor switch
    {
        DeviceFormFactor.Speakers => ScreenIcon.Speaker,
        DeviceFormFactor.Headphones => ScreenIcon.Headphones,
        DeviceFormFactor.Headset => ScreenIcon.Headset,
        DeviceFormFactor.Microphone => ScreenIcon.Mic,
        DeviceFormFactor.Display => ScreenIcon.Display,
        _ => ScreenIcon.Generic,
    };

    /// <summary>A device Windows reports right now.</summary>
    public static DeviceLook For(AppConfig config, AudioDevice device)
    {
        var settings = config.FindDevice(device.Id);
        return new DeviceLook(NameFrom(settings, device.FriendlyName), settings?.Icon ?? IconFor(device.FormFactor));
    }

    /// <summary>A configured reference, which may be disconnected. Null when the reference names
    /// nothing (no id and no name).</summary>
    public static DeviceLook? For(AppConfig config, IAudioEngine audio, DeviceRefConfig? reference)
    {
        if (reference is null || (string.IsNullOrEmpty(reference.Id) && string.IsNullOrEmpty(reference.FriendlyName)))
            return null;
        if (FindLive(audio, reference) is { } live) return For(config, live);
        var settings = config.FindDevice(reference.Id);
        var friendly = settings?.FriendlyName is { Length: > 0 } stored ? stored : reference.FriendlyName;
        return new DeviceLook(NameFrom(settings, friendly), settings?.Icon ?? ScreenIcon.Generic);
    }

    /// <summary>The display name of a reference, or <paramref name="fallback"/> when it names nothing.</summary>
    public static string NameOf(AppConfig config, IAudioEngine audio, DeviceRefConfig? reference, string fallback)
        => For(config, audio, reference)?.Name ?? fallback;

    /// <summary>The live device a reference points at (by id, else by friendly name), either flow.</summary>
    public static AudioDevice? FindLive(IAudioEngine audio, DeviceRefConfig? reference)
    {
        if (reference is null) return null;
        var all = audio.GetDevices(AudioFlow.Render).Concat(audio.GetDevices(AudioFlow.Capture)).ToList();
        if (!string.IsNullOrEmpty(reference.Id))
            return all.Find(d => string.Equals(d.Id, reference.Id, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(reference.FriendlyName))
            return all.Find(d => string.Equals(d.FriendlyName, reference.FriendlyName, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static string NameFrom(DeviceSettingsConfig? settings, string friendlyName)
        => !string.IsNullOrWhiteSpace(settings?.Nickname) ? settings!.Nickname.Trim() : AutoShortName(friendlyName);
}
```

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(core): device display names and icons`.

---

### Task 5: `ControllerDisplay`, what each controller button shows

**Files:**
- Create: `src/Volco.Core/Communication/ControllerDisplay.cs`
- Test: `tests/Volco.Core.Tests/Communication/ControllerDisplayTests.cs`

`ButtonFeedback.cs` and its tests stay until Task 7, which deletes them once nothing uses them.

**Interfaces**
- **Consumes:** `DeviceDisplay` (Task 4), `ScreenContent` and `IndicatorKind` (Task 1), `AppConfig.Controller` (Task 2). It also uses the internals `ActionFactory.Resolve` and `ToggleOutputDeviceAction.DeviceMatches`.
- **Produces:**
  - `abstract record ButtonDisplay`, with `sealed record LedDisplay(byte Value)` and `sealed record ScreenDisplay(ScreenContent Content)`
  - `ControllerDisplay.Compute(AppConfig, string presetId, IAudioEngine) → IReadOnlyList<ButtonDisplay?>`: one entry per **controller** button; null for kind None or an unknown kind; empty without controller info
  - `ControllerDisplay.For(AppConfig, int button, IndicatorKind, string presetId, IAudioEngine) → ButtonDisplay?`
  - `ControllerDisplay.PrimaryAction(AppConfig, int button, string presetId) → ButtonActionConfig?`
  - `ControllerDisplay.LedValue(ButtonActionConfig?, AppConfig, string presetId, IAudioEngine) → byte`
  - `ControllerDisplay.LedValueFor(ButtonBindingConfig, AppConfig, string presetId, IAudioEngine) → byte`
  - `ControllerDisplay.Screen(ButtonActionConfig?, AppConfig, string presetId, IAudioEngine) → ScreenContent`
  - Constants `ControllerDisplay.MaxDots = 12`, `Muted = "MUTED"`, `Live = "LIVE"`, `Active = "ACTIVE"`, `ChooseDevice = "Choose a device"`, `NoDevice = "No device"`, `ChoosePreset = "Choose a preset"`.

- [ ] **Step 1: Write the failing tests** `tests/Volco.Core.Tests/Communication/ControllerDisplayTests.cs`:

```csharp
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class ControllerDisplayTests
{
    private static readonly AudioDevice Speakers   = new("spk", "Speakers (Realtek)", AudioFlow.Render, true, DeviceFormFactor.Speakers);
    private static readonly AudioDevice Headphones = new("hp",  "Headphones",         AudioFlow.Render, false, DeviceFormFactor.Headphones);
    private static readonly AudioDevice Monitor    = new("mon", "Monitor",            AudioFlow.Render, false, DeviceFormFactor.Display);
    private static readonly AudioDevice Mic        = new("mic", "Microphone (Yeti)",  AudioFlow.Capture, true, DeviceFormFactor.Microphone);

    private readonly FakeAudioEngine _audio = new()
    {
        Devices = { Speakers, Headphones, Monitor, Mic }, DefaultRender = Speakers, DefaultCapture = Mic,
    };

    private static DeviceRefConfig Ref(AudioDevice d) => new() { Id = d.Id, FriendlyName = d.FriendlyName };
    private static ButtonActionConfig Act(ButtonActionType t) => new() { Action = t };

    /// <summary>One card per single-press action; the controller reports the given kinds
    /// (default: as many screens as cards).</summary>
    private static AppConfig Config(IndicatorKind[]? kinds, params ButtonActionConfig[] singlePresses)
    {
        var c = AppConfig.CreateDefault();
        c.Buttons.Clear();
        foreach (var a in singlePresses)
        {
            var b = new ButtonConfig();
            b.Default().SinglePress = a;
            c.Buttons.Add(b);
        }
        c.Controller = new ControllerInfoConfig
        {
            ProtocolVersion = 1, Sliders = 5,
            Buttons = (kinds ?? Enumerable.Repeat(IndicatorKind.Screen, singlePresses.Length)).ToList(),
        };
        return c;
    }

    private ScreenContent ScreenOf(AppConfig c, int button = 0, string preset = AppConfig.DefaultPresetId)
        => Assert.IsType<ScreenDisplay>(ControllerDisplay.Compute(c, preset, _audio)[button]).Content;

    private byte LedOf(AppConfig c, int button = 0, string preset = AppConfig.DefaultPresetId)
        => Assert.IsType<LedDisplay>(ControllerDisplay.Compute(c, preset, _audio)[button]).Value;

    private static IndicatorKind[] Leds(int n) => Enumerable.Repeat(IndicatorKind.Led, n).ToArray();

    // ── Screens (spec §2.2) ───────────────────────────────────────────────────

    [Fact]
    public void MuteCurrentOutput_ShowsMutedState_DefaultOutputsIconAndName()
    {
        var c = Config(null, Act(ButtonActionType.MuteCurrentOutput));
        _audio.DeviceStates["spk"] = (0.5f, false);
        Assert.Equal(ScreenContent.Text("LIVE", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c));
        _audio.DeviceStates["spk"] = (0.5f, true);
        Assert.Equal(ScreenContent.Text("MUTED", ScreenIcon.Speaker, true, "Speakers"), ScreenOf(c));
    }

    [Fact]
    public void MuteCurrentInput_UsesTheDefaultInput()
    {
        var c = Config(null, Act(ButtonActionType.MuteCurrentInput));
        _audio.DeviceStates["mic"] = (1f, true);
        Assert.Equal(ScreenContent.Text("MUTED", ScreenIcon.Mic, true, "Microphone"), ScreenOf(c));
    }

    [Fact]
    public void MuteCurrentOutput_NoDefaultDevice_ShowsNoDevice()
    {
        _audio.DefaultRender = null;
        var c = Config(Leds(1).Concat(new[] { IndicatorKind.Screen }).ToArray(),
                       Act(ButtonActionType.MuteCurrentOutput), Act(ButtonActionType.MuteCurrentOutput));
        Assert.Equal(0, LedOf(c, 0));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Generic, false, "No device"), ScreenOf(c, 1));
    }

    [Fact]
    public void Nickname_IsUsedOnTheScreen()
    {
        var c = Config(null, Act(ButtonActionType.MuteCurrentOutput));
        c.Devices.Add(new DeviceSettingsConfig { Id = "spk", Nickname = "Desk speakers", Icon = ScreenIcon.Speakers });
        Assert.Equal(ScreenContent.Text("LIVE", ScreenIcon.Speakers, false, "Desk speakers"), ScreenOf(c));
    }

    [Fact]
    public void MuteSpecificDevice_FollowsThatDevice_AndAsksForOneWhenUnset()
    {
        var c = Config(null,
            new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = Ref(Headphones) },
            Act(ButtonActionType.MuteSpecificDevice));
        _audio.DeviceStates["hp"] = (1f, true);
        Assert.Equal(ScreenContent.Text("MUTED", ScreenIcon.Headphones, true, "Headphones"), ScreenOf(c, 0));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Generic, false, "Choose a device"), ScreenOf(c, 1));
    }

    [Fact]
    public void SetOutputDevice_ActiveOnlyWhileItIsTheOutput()
    {
        var c = Config(null,
            new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = Ref(Speakers) },
            new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = Ref(Monitor) });
        Assert.Equal(ScreenContent.Text("ACTIVE", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c, 0));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Display, false, "Monitor"), ScreenOf(c, 1));
    }

    [Fact]
    public void Toggle_ShowsTheActiveOutput_AndNamesTheOtherOnTop()
    {
        var c = Config(null, new ButtonActionConfig
        {
            Action = ButtonActionType.ToggleOutputDevice, Device = Ref(Speakers), DeviceB = Ref(Headphones),
        });
        Assert.Equal(ScreenContent.Text("NEXT: Headphones", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c));
        _audio.DefaultRender = Headphones;
        Assert.Equal(ScreenContent.Text("NEXT: Speakers", ScreenIcon.Headphones, false, "Headphones"), ScreenOf(c));
        _audio.DefaultRender = Monitor;   // neither: show A
        Assert.Equal(ScreenContent.Text("NEXT: Headphones", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c));
    }

    [Fact]
    public void Toggle_OneSideSet_UsesItForBoth_NoSidesAsksForADevice()
    {
        var c = Config(new[] { IndicatorKind.Screen, IndicatorKind.LedPair, IndicatorKind.Screen },
            new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice, DeviceB = Ref(Speakers) },
            new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice, DeviceB = Ref(Speakers) },
            Act(ButtonActionType.ToggleOutputDevice));
        Assert.Equal(ScreenContent.Text("NEXT: Speakers", ScreenIcon.Speaker, false, "Speakers"), ScreenOf(c, 0));
        Assert.Equal(1, LedOf(c, 1));
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Generic, false, "Choose a device"), ScreenOf(c, 2));
    }

    [Fact]
    public void UnpluggedDevice_ShowsStoredNameAndIcon()
    {
        // Review Focus #2.
        var gone = new DeviceRefConfig { Id = "sony", FriendlyName = "Headphones (WH-1000XM4)" };
        var c = Config(new[] { IndicatorKind.Screen, IndicatorKind.Led },
            new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = gone },
            new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = gone });
        c.Devices.Add(new DeviceSettingsConfig { Id = "sony", Nickname = "Sony", Icon = ScreenIcon.Headphones });
        Assert.Equal(ScreenContent.Text("", ScreenIcon.Headphones, false, "Sony"), ScreenOf(c, 0));
        Assert.Equal(0, LedOf(c, 1));
    }

    [Fact]
    public void NextPreset_ShowsDotsAndTheLivePresetsName()
    {
        var c = Config(null, Act(ButtonActionType.NextPreset));
        c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        c.Buttons[0].GetOrAddBinding("gaming").SinglePress = Act(ButtonActionType.NextPreset);
        Assert.Equal(ScreenContent.Dots(0, 2, ScreenIcon.Presets, "Default"), ScreenOf(c));
        Assert.Equal(ScreenContent.Dots(1, 2, ScreenIcon.Presets, "Gaming"), ScreenOf(c, 0, "gaming"));
    }

    [Fact]
    public void GoToPreset_ShowsTheTargetsName()
    {
        var c = Config(null, new ButtonActionConfig { Action = ButtonActionType.GoToPreset, PresetId = "gaming" });
        c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        Assert.Equal(ScreenContent.Dots(0, 2, ScreenIcon.Presets, "Gaming"), ScreenOf(c));
    }

    [Fact]
    public void MoreThanTwelvePresets_TopShowsNOfN()
    {
        var c = Config(null, Act(ButtonActionType.NextPreset));
        for (int i = 2; i <= 13; i++) c.Presets.Add(new PresetConfig { Id = $"p{i}", Name = $"P{i}" });
        Assert.Equal(ScreenContent.Text("1/13", ScreenIcon.Presets, false, "Default"), ScreenOf(c));
    }

    [Fact]
    public void NotSetUp_IsABlankScreenAndAnLedOff()
    {
        var c = Config(new[] { IndicatorKind.Screen, IndicatorKind.Led }, Act(ButtonActionType.None), Act(ButtonActionType.None));
        Assert.Equal(ScreenContent.Blank, ScreenOf(c, 0));
        Assert.Equal(0, LedOf(c, 1));
    }

    [Fact]
    public void FirstActionThatIsNotNothing_Wins()
    {
        var c = Config(null, Act(ButtonActionType.None));
        c.Buttons[0].Default().DoublePress = Act(ButtonActionType.MuteCurrentInput);
        c.Buttons[0].Default().LongPress = Act(ButtonActionType.MuteCurrentOutput);
        Assert.Equal("Microphone", ScreenOf(c).Main);
    }

    // ── Controller vs config (spec §2.2) ──────────────────────────────────────

    [Fact]
    public void MoreControllerButtonsThanCards_ExtraButtonsAreNotSetUp()
    {
        // Review Focus #5.
        var c = Config(new[] { IndicatorKind.Screen, IndicatorKind.Screen, IndicatorKind.Led }, Act(ButtonActionType.MuteCurrentOutput));
        var all = ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio);
        Assert.Equal(3, all.Count);
        Assert.Equal(new ScreenDisplay(ScreenContent.Blank), all[1]);
        Assert.Equal(new LedDisplay(0), all[2]);
    }

    [Fact]
    public void FewerControllerButtonsThanCards_ExtraCardsIgnored()
    {
        var c = Config(new[] { IndicatorKind.Led }, Act(ButtonActionType.None), Act(ButtonActionType.MuteCurrentOutput));
        Assert.Single(ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio));
    }

    [Fact]
    public void KindNoneOrUnknown_IsNull_NoControllerInfo_IsEmpty()
    {
        var c = Config(new[] { IndicatorKind.None, (IndicatorKind)9 }, Act(ButtonActionType.MuteCurrentOutput), Act(ButtonActionType.MuteCurrentOutput));
        Assert.All(ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio), Assert.Null);
        c.Controller = null;
        Assert.Empty(ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio));
    }

    // ── LED values (the rules ButtonFeedback had) ─────────────────────────────

    [Fact]
    public void LedValues_FollowTheLivePreset()
    {
        var c = Config(Leds(4),
            Act(ButtonActionType.MuteCurrentOutput),
            new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = Ref(Headphones) },
            new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice, Device = Ref(Speakers), DeviceB = Ref(Headphones) },
            Act(ButtonActionType.NextPreset));
        c.Presets.Add(new PresetConfig { Id = "gaming", Name = "Gaming" });
        c.Buttons[3].GetOrAddBinding("gaming").SinglePress = Act(ButtonActionType.NextPreset);
        _audio.DeviceStates["spk"] = (1f, true);

        Assert.Equal(new byte[] { 1, 0, 1, 1 },
            ControllerDisplay.Compute(c, AppConfig.DefaultPresetId, _audio).Select(d => ((LedDisplay)d!).Value));
        _audio.DefaultRender = Headphones;
        Assert.Equal(1, LedOf(c, 1));
        Assert.Equal(2, LedOf(c, 2));
        Assert.Equal(0, LedOf(c, 0, "gaming"));   // nothing in Gaming: the light goes off
        Assert.Equal(2, LedOf(c, 3, "gaming"));
    }

    [Fact]
    public void ToggleOutputDevice_MatchesAFriendlyNameOnlyDevice()
    {
        var c = Config(Leds(1), new ButtonActionConfig
        {
            Action = ButtonActionType.ToggleOutputDevice,
            Device = new DeviceRefConfig { Id = "", FriendlyName = "Speakers (Realtek)" },
            DeviceB = Ref(Headphones),
        });
        Assert.Equal(1, LedOf(c));
    }
}
```

`b.Default()` is the existing test extension in `tests/Volco.Core.Tests/TestConfigExtensions.cs`.

- [ ] **Step 2: Implement** `src/Volco.Core/Communication/ControllerDisplay.cs`:

```csharp
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Devices;
using Volco.Core.Input.Actions;

namespace Volco.Core.Communication;

/// <summary>What one controller button shows: an LED value, or a screen.</summary>
public abstract record ButtonDisplay;
public sealed record LedDisplay(byte Value) : ButtonDisplay;
public sealed record ScreenDisplay(ScreenContent Content) : ButtonDisplay;

/// <summary>
/// Computes what every controller button shows (spec §2.2, §5.3). Pure: only read-only engine
/// queries. One entry per button the controller reported in Hello (not per config card): a
/// button with no card is "Not set up", and cards beyond the controller's count are ignored.
/// A button shows its first action that is not "Do nothing", in the order press → double → hold.
/// </summary>
public static class ControllerDisplay
{
    public const int MaxDots = 12;
    public const string Muted = "MUTED";
    public const string Live = "LIVE";
    public const string Active = "ACTIVE";
    public const string ChooseDevice = "Choose a device";
    public const string NoDevice = "No device";
    public const string ChoosePreset = "Choose a preset";

    /// <summary>One entry per controller button; null where the kind is None or unknown. Empty
    /// until the controller has said hello.</summary>
    public static IReadOnlyList<ButtonDisplay?> Compute(AppConfig config, string presetId, IAudioEngine audio)
    {
        var info = config.Controller;
        if (info is null) return Array.Empty<ButtonDisplay?>();
        var result = new ButtonDisplay?[info.Buttons.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = For(config, i, info.Buttons[i], presetId, audio);
        return result;
    }

    public static ButtonDisplay? For(AppConfig config, int button, IndicatorKind kind, string presetId, IAudioEngine audio)
    {
        var action = PrimaryAction(config, button, presetId);
        return kind switch
        {
            IndicatorKind.Led or IndicatorKind.LedPair => new LedDisplay(LedValue(action, config, presetId, audio)),
            IndicatorKind.Screen => new ScreenDisplay(Screen(action, config, presetId, audio)),
            _ => null,
        };
    }

    /// <summary>The action a button shows, or null when it has no card or does nothing.</summary>
    public static ButtonActionConfig? PrimaryAction(AppConfig config, int button, string presetId)
        => button >= 0 && button < config.Buttons.Count
            ? config.Buttons[button].BindingFor(presetId).All().FirstOrDefault(a => a.Action != ButtonActionType.None)
            : null;

    /// <summary>The LED value for a whole binding (used by the settings UI's light preview).</summary>
    public static byte LedValueFor(ButtonBindingConfig binding, AppConfig config, string presetId, IAudioEngine audio)
        => LedValue(binding.All().FirstOrDefault(a => a.Action != ButtonActionType.None), config, presetId, audio);

    /// <summary>0 = off; mute: 1 while muted; switch: 1 while active; toggle: 1 = A, 2 = B;
    /// presets: 1 + the preset's position.</summary>
    public static byte LedValue(ButtonActionConfig? action, AppConfig config, string presetId, IAudioEngine audio)
    {
        switch (action?.Action)
        {
            case ButtonActionType.MuteCurrentOutput:
                return Bit(audio.GetDefaultDevice(AudioFlow.Render) is not null && audio.IsMuted(DeviceRef.DefaultOutput()));
            case ButtonActionType.MuteCurrentInput:
                return Bit(audio.GetDefaultDevice(AudioFlow.Capture) is not null && audio.IsMuted(DeviceRef.DefaultInput()));
            case ButtonActionType.MuteSpecificDevice:
                return Bit(DeviceDisplay.FindLive(audio, action.Device) is not null
                           && ActionFactory.Resolve(action.Device) is { } r && audio.IsMuted(r));
            case ButtonActionType.SetOutputDevice:
                return Bit(IsCurrentOutput(audio, action.Device));
            case ButtonActionType.ToggleOutputDevice:
            {
                var (a, b) = ToggleSides(action);
                if (a is null) return 0;
                if (IsCurrentOutput(audio, a)) return 1;
                return IsCurrentOutput(audio, b) ? (byte)2 : (byte)0;
            }
            case ButtonActionType.NextPreset:
            case ButtonActionType.GoToPreset:
            {
                var index = config.Presets.FindIndex(p => p.Id == presetId);
                return index < 0 ? (byte)0 : (byte)Math.Min(255, index + 1);
            }
            default:
                return 0;
        }
    }

    /// <summary>Layout 1 content for an action (spec §2.2's table).</summary>
    public static ScreenContent Screen(ButtonActionConfig? action, AppConfig config, string presetId, IAudioEngine audio)
    {
        switch (action?.Action)
        {
            case ButtonActionType.MuteCurrentOutput:
                return MuteScreen(config, audio, audio.GetDefaultDevice(AudioFlow.Render), DeviceRef.DefaultOutput());
            case ButtonActionType.MuteCurrentInput:
                return MuteScreen(config, audio, audio.GetDefaultDevice(AudioFlow.Capture), DeviceRef.DefaultInput());
            case ButtonActionType.MuteSpecificDevice:
            {
                if (DeviceDisplay.For(config, audio, action.Device) is not { } look) return AskForDevice();
                var live = DeviceDisplay.FindLive(audio, action.Device);
                if (live is null) return ScreenContent.Text("", look.Icon, false, look.Name);
                var muted = audio.IsMuted(DeviceRef.ById(live.Id, live.Flow));
                return ScreenContent.Text(muted ? Muted : Live, look.Icon, muted, look.Name);
            }
            case ButtonActionType.SetOutputDevice:
            {
                if (DeviceDisplay.For(config, audio, action.Device) is not { } look) return AskForDevice();
                return ScreenContent.Text(IsCurrentOutput(audio, action.Device) ? Active : "", look.Icon, false, look.Name);
            }
            case ButtonActionType.ToggleOutputDevice:
            {
                var (a, b) = ToggleSides(action);
                if (DeviceDisplay.For(config, audio, a) is not { } lookA) return AskForDevice();
                var lookB = DeviceDisplay.For(config, audio, b) ?? lookA;
                var onB = !IsCurrentOutput(audio, a) && IsCurrentOutput(audio, b);
                var (active, activeRef, other) = onB ? (lookB, b, lookA) : (lookA, a, lookB);
                if (DeviceDisplay.FindLive(audio, activeRef) is null)
                    return ScreenContent.Text("", active.Icon, false, active.Name);
                return ScreenContent.Text($"NEXT: {other.Name}", active.Icon, false, active.Name);
            }
            case ButtonActionType.NextPreset:
                return PresetScreen(config, presetId, config.Presets.Find(p => p.Id == presetId)?.Name ?? "");
            case ButtonActionType.GoToPreset:
                return PresetScreen(config, presetId, config.Presets.Find(p => p.Id == action.PresetId)?.Name ?? ChoosePreset);
            default:
                return ScreenContent.Blank;
        }
    }

    private static ScreenContent MuteScreen(AppConfig config, IAudioEngine audio, AudioDevice? device, DeviceRef reference)
    {
        if (device is null) return ScreenContent.Text("", ScreenIcon.Generic, false, NoDevice);
        var look = DeviceDisplay.For(config, device);
        var muted = audio.IsMuted(reference);
        return ScreenContent.Text(muted ? Muted : Live, look.Icon, muted, look.Name);
    }

    private static ScreenContent PresetScreen(AppConfig config, string presetId, string main)
    {
        var count = config.Presets.Count;
        var index = Math.Max(0, config.Presets.FindIndex(p => p.Id == presetId));
        return count <= MaxDots
            ? ScreenContent.Dots((byte)index, (byte)count, ScreenIcon.Presets, main)
            : ScreenContent.Text($"{index + 1}/{count}", ScreenIcon.Presets, false, main);
    }

    private static ScreenContent AskForDevice() => ScreenContent.Text("", ScreenIcon.Generic, false, ChooseDevice);

    /// <summary>A toggle with one side set uses it as both A and B.</summary>
    private static (DeviceRefConfig? A, DeviceRefConfig? B) ToggleSides(ButtonActionConfig action)
    {
        var a = IsSet(action.Device) ? action.Device : IsSet(action.DeviceB) ? action.DeviceB : null;
        var b = IsSet(action.DeviceB) ? action.DeviceB : a;
        return (a, b);
    }

    private static bool IsSet(DeviceRefConfig? d)
        => d is not null && (!string.IsNullOrEmpty(d.Id) || !string.IsNullOrEmpty(d.FriendlyName));

    private static bool IsCurrentOutput(IAudioEngine audio, DeviceRefConfig? device)
        => audio.GetDefaultDevice(AudioFlow.Render) is { } current && ToggleOutputDeviceAction.DeviceMatches(current, device);

    private static byte Bit(bool on) => on ? (byte)1 : (byte)0;
}
```

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(core): ControllerDisplay computes LED values and screens per controller button`.

---

### Task 6: `ControllerLink`, the controller session

**Files:**
- Create: `src/Volco.Core/Runtime/ControllerState.cs`
- Create: `src/Volco.Core/Runtime/ControllerLink.cs`
- Test: `tests/Volco.Core.Tests/Runtime/ControllerLinkTests.cs`

**Interfaces**
- **Consumes:** `ProtocolEncoder`, `HelloMessage`, `ProtocolVersion` (Task 1); `ButtonDisplay`, `LedDisplay`, `ScreenDisplay` (Task 5); `IControllerConnection`.
- **Produces:**
  - `enum ControllerState { NoController, AwaitingHello, Ready, VersionMismatch }`
  - `sealed record ControllerLinkOptions(TimeSpan HelloRetryInterval, TimeSpan HeartbeatInterval, TimeSpan TickInterval)` with `Default` = (1 s, 1 s, 250 ms)
  - `ControllerLink(Func<IReadOnlyList<ButtonDisplay?>> compute, ControllerLinkOptions? options = null)`
  - Properties and events: `State`, `event StateChanged`, `event HelloReceived` (raised before the full push, and only for the right version)
  - Methods: `Attach(IControllerConnection)`, `Detach()`, `OnMessage(ControllerMessage)`, `Push()`, `PushAll()`, `PushPresetChange(ScreenTransition, bool synchronized)`, `Dispose()`

**Behaviour (spec §3.4).**
- **One lock (`_gate`) serializes everything.** It is held across compute → dedup → send for every push, burst and keepalive, so a push computed from older state can never follow a newer one and a Begin … Commit burst is never interleaved. Today's runtime sends outside its lock; the link deliberately does not. `Monitor` is re-entrant, so a transport that raises `StatusChanged` synchronously from `Send` on the same thread can't deadlock.
- **Events are raised outside the lock.** `StateChanged` and `HelloReceived` fire after it is released.
- **Nothing but HelloRequest/Heartbeat goes out before a Hello** with the right version on the current attachment.

- [ ] **Step 1: Write the failing tests** `tests/Volco.Core.Tests/Runtime/ControllerLinkTests.cs`:

```csharp
using System.Diagnostics;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Xunit;

namespace Volco.Core.Tests.Runtime;

public sealed class ControllerLinkTests : IDisposable
{
    private static readonly ControllerLinkOptions Fast = new(
        HelloRetryInterval: TimeSpan.FromMilliseconds(40),
        HeartbeatInterval: TimeSpan.FromMilliseconds(40),
        TickInterval: TimeSpan.FromMilliseconds(10));

    /// <summary>Never fires on its own within a test.</summary>
    private static readonly ControllerLinkOptions Quiet = new(
        TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1));

    private readonly FakeControllerConnection _conn = new(new ConnectionConfig());
    private readonly List<ButtonDisplay?> _displays = new()
    {
        new LedDisplay(0),
        new ScreenDisplay(ScreenContent.Text("LIVE", ScreenIcon.Mic, false, "Mic")),
        null,                                                    // indicator kind None
    };
    private ControllerLink? _link;

    private ControllerLink NewLink(ControllerLinkOptions? options = null)
    {
        _link = new ControllerLink(() => _displays.ToList(), options ?? Quiet);
        return _link;
    }

    public void Dispose() => _link?.Dispose();

    private static readonly HelloMessage GoodHello = new(ProtocolVersion.Current, 5,
        new[] { IndicatorKind.Led, IndicatorKind.Screen, IndicatorKind.None });

    private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    private int Count(byte[] frame) => _conn.SentFrames.Count(f => Same(f, frame));
    private static byte[] Screen1(ScreenTransition t = ScreenTransition.None, string top = "LIVE")
        => ProtocolEncoder.EncodeScreen(1, t, ScreenContent.Text(top, ScreenIcon.Mic, false, "Mic"));

    private static bool WaitUntil(Func<bool> condition, int ms = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition()) { if (sw.ElapsedMilliseconds > ms) return condition(); Thread.Sleep(5); }
        return true;
    }

    private ControllerLink Ready()
    {
        var link = NewLink();
        link.Attach(_conn);
        link.OnMessage(GoodHello);
        return link;
    }

    [Fact]
    public void Attach_SendsHelloRequest_AndNoStateUntilHello()
    {
        var link = NewLink();
        var states = new List<ControllerState>();
        link.StateChanged += (_, s) => states.Add(s);

        link.Attach(_conn);
        link.Push();

        Assert.Equal(new[] { ProtocolEncoder.EncodeHelloRequest() }, _conn.SentFrames);
        Assert.Equal(ControllerState.AwaitingHello, link.State);
        Assert.Equal(new[] { ControllerState.AwaitingHello }, states);
    }

    [Fact]
    public void HelloRequest_RetriesUntilHello_ThenStops()
    {
        var link = NewLink(Fast);
        link.Attach(_conn);
        Assert.True(WaitUntil(() => Count(ProtocolEncoder.EncodeHelloRequest()) >= 3));

        link.OnMessage(GoodHello);
        var after = Count(ProtocolEncoder.EncodeHelloRequest());
        Thread.Sleep(150);
        Assert.Equal(after, Count(ProtocolEncoder.EncodeHelloRequest()));
    }

    [Fact]
    public void Hello_RaisesHelloReceivedBeforeThePush_AndPushesEveryButtonWithAKind()
    {
        var link = NewLink();
        var sentBeforeHandler = -1;
        link.HelloReceived += (_, _) => sentBeforeHandler = _conn.SentCount;
        link.Attach(_conn);

        link.OnMessage(GoodHello);

        Assert.Equal(1, sentBeforeHandler);                     // only the HelloRequest so far
        Assert.Equal(ControllerState.Ready, link.State);
        Assert.Equal(1, Count(ProtocolEncoder.EncodeLed(0, 0)));
        Assert.Equal(1, Count(Screen1()));
        Assert.Equal(3, _conn.SentCount);                        // HelloRequest, Led 0, Screen 1
    }

    [Fact]
    public void Hello_WrongVersion_IsMismatch_SendsNothing_NoHeartbeat()
    {
        var link = NewLink(Fast);
        var helloSeen = false;
        link.HelloReceived += (_, _) => helloSeen = true;
        link.Attach(_conn);

        link.OnMessage(new HelloMessage(99, 5, new[] { IndicatorKind.Led }));
        var after = _conn.SentCount;
        link.Push();
        Thread.Sleep(150);

        Assert.False(helloSeen);
        Assert.Equal(ControllerState.VersionMismatch, link.State);
        Assert.Equal(after, _conn.SentCount);
    }

    [Fact]
    public void Push_SendsOnlyWhatChanged()
    {
        var link = Ready();
        var baseline = _conn.SentCount;

        link.Push();
        Assert.Equal(baseline, _conn.SentCount);

        _displays[0] = new LedDisplay(1);
        link.Push();
        Assert.Equal(baseline + 1, _conn.SentCount);
        Assert.True(Same(ProtocolEncoder.EncodeLed(0, 1), _conn.SentFrames[^1]));
    }

    [Fact]
    public void PresetChange_Synchronized_IsBeginScreensLedsCommit_EvenWhenUnchanged()
    {
        var link = Ready();
        var baseline = _conn.SentCount;

        link.PushPresetChange(ScreenTransition.SlideLeft, synchronized: true);

        var burst = _conn.SentFrames.Skip(baseline).ToList();
        Assert.Equal(4, burst.Count);
        Assert.True(Same(ProtocolEncoder.EncodeBegin(), burst[0]));
        Assert.True(Same(Screen1(ScreenTransition.SlideLeft), burst[1]));
        Assert.True(Same(ProtocolEncoder.EncodeLed(0, 0), burst[2]));
        Assert.True(Same(ProtocolEncoder.EncodeCommit(), burst[3]));
    }

    [Fact]
    public void PresetChange_Unsynchronized_SameMessagesWithoutBeginCommit()
    {
        var link = Ready();
        var baseline = _conn.SentCount;

        link.PushPresetChange(ScreenTransition.SlideRight, synchronized: false);

        var burst = _conn.SentFrames.Skip(baseline).ToList();
        Assert.Equal(2, burst.Count);
        Assert.True(Same(Screen1(ScreenTransition.SlideRight), burst[0]));
        Assert.True(Same(ProtocolEncoder.EncodeLed(0, 0), burst[1]));
    }

    [Fact]
    public void PushAll_ResendsEverythingWithoutATransition()
    {
        var link = Ready();
        var baseline = _conn.SentCount;
        link.PushAll();
        Assert.Equal(baseline + 2, _conn.SentCount);
        Assert.Equal(2, Count(Screen1()));
    }

    [Fact]
    public void SecondHello_ClearsDedupAndPushesAgain()
    {
        var link = Ready();
        var baseline = _conn.SentCount;
        link.OnMessage(GoodHello);
        Assert.Equal(baseline + 2, _conn.SentCount);
    }

    [Fact]
    public void Heartbeat_OnlyWhenIdle()
    {
        var link = NewLink(Fast);
        link.Attach(_conn);
        link.OnMessage(GoodHello);
        Assert.True(WaitUntil(() => Count(ProtocolEncoder.EncodeHeartbeat()) >= 2));

        // Busy: a frame every 10 ms keeps the link from needing a heartbeat.
        var before = Count(ProtocolEncoder.EncodeHeartbeat());
        var sw = Stopwatch.StartNew();
        byte v = 0;
        while (sw.ElapsedMilliseconds < 200)
        {
            _displays[0] = new LedDisplay(v ^= 1);
            link.Push();
            Thread.Sleep(10);
        }
        Assert.InRange(Count(ProtocolEncoder.EncodeHeartbeat()) - before, 0, 1);
    }

    [Fact]
    public void Detach_StopsEverything()
    {
        var link = NewLink(Fast);
        link.Attach(_conn);
        link.OnMessage(GoodHello);
        link.Detach();
        var after = _conn.SentCount;
        Thread.Sleep(150);
        link.Push();
        Assert.Equal(after, _conn.SentCount);
        Assert.Equal(ControllerState.NoController, link.State);
    }

    [Fact]
    public void RapidPresetChanges_EachBurstIsCompleteAndInOrder()
    {
        // Review Focus #1: presses from several threads never interleave bursts.
        var link = Ready();
        var baseline = _conn.SentCount;
        var threads = Enumerable.Range(0, 6).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < 20; i++) link.PushPresetChange(ScreenTransition.SlideLeft, synchronized: true);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var frames = _conn.SentFrames.Skip(baseline).ToList();
        Assert.Equal(120 * 4, frames.Count);
        for (int i = 0; i < frames.Count; i += 4)
        {
            Assert.True(Same(ProtocolEncoder.EncodeBegin(), frames[i]));
            Assert.True(Same(ProtocolEncoder.EncodeCommit(), frames[i + 3]));
        }
    }

    [Fact]
    public void ConcurrentPushes_NeverSendOlderStateAfterNewer()
    {
        var link = Ready();
        int counter = 0;
        var linkWithCounter = new ControllerLink(
            () => new ButtonDisplay?[] { new LedDisplay((byte)Volatile.Read(ref counter)) }, Quiet);
        try
        {
            var conn = new FakeControllerConnection(new ConnectionConfig());
            linkWithCounter.Attach(conn);
            linkWithCounter.OnMessage(new HelloMessage(ProtocolVersion.Current, 0, new[] { IndicatorKind.Led }));
            var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
            {
                for (int i = 0; i < 50; i++) { Interlocked.Increment(ref counter); linkWithCounter.Push(); }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            var values = conn.SentFrames.Where(f => f[2] == (byte)MessageType.Led).Select(f => (int)f[4]).ToList();
            for (int i = 1; i < values.Count; i++) Assert.True(values[i] > values[i - 1], "an older value was sent after a newer one");
        }
        finally { linkWithCounter.Dispose(); }
    }
}
```

- [ ] **Step 2: Implement** `src/Volco.Core/Runtime/ControllerState.cs`:

```csharp
namespace Volco.Core.Runtime;

/// <summary>Where the controller session stands (spec §5.5), separate from the transport's
/// <see cref="Communication.ConnectionStatus"/>.</summary>
public enum ControllerState
{
    /// <summary>The transport is not connected.</summary>
    NoController,
    /// <summary>Connected; HelloRequest sent, no Hello yet. Nothing else is sent.</summary>
    AwaitingHello,
    /// <summary>A Hello with this app's protocol version arrived.</summary>
    Ready,
    /// <summary>A Hello with another protocol version arrived: the firmware needs reflashing.</summary>
    VersionMismatch,
}
```

`src/Volco.Core/Runtime/ControllerLink.cs`:

```csharp
using Volco.Core.Communication;
using Volco.Core.Diagnostics;

namespace Volco.Core.Runtime;

public sealed record ControllerLinkOptions(TimeSpan HelloRetryInterval, TimeSpan HeartbeatInterval, TimeSpan TickInterval)
{
    public static ControllerLinkOptions Default { get; } =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// One controller session (spec §3.4, §5.5): HelloRequest until Hello, the controller state,
/// keepalive, per-button dedup, and the only path frames take to the controller. A single lock
/// is held across compute → dedup → send, so frames always leave in the order their state was
/// computed and a Begin … Commit burst is never interleaved. Events are raised outside it.
/// </summary>
public sealed class ControllerLink : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<ButtonDisplay?>> _compute;
    private readonly ControllerLinkOptions _options;
    private readonly Timer _timer;

    // Guarded by _gate.
    private IControllerConnection? _connection;
    private ControllerState _state = ControllerState.NoController;
    private ButtonDisplay?[] _sent = Array.Empty<ButtonDisplay?>();
    private long _lastSendMs;
    private long _lastHelloRequestMs;
    private bool _disposed;

    /// <param name="compute">What every controller button should show now (ControllerDisplay).
    /// Called with the lock held; must only read.</param>
    public ControllerLink(Func<IReadOnlyList<ButtonDisplay?>> compute, ControllerLinkOptions? options = null)
    {
        _compute = compute;
        _options = options ?? ControllerLinkOptions.Default;
        _timer = new Timer(_ => Tick(), null, _options.TickInterval, _options.TickInterval);
    }

    public ControllerState State { get { lock (_gate) return _state; } }

    /// <summary>The controller state changed. Any thread.</summary>
    public event EventHandler<ControllerState>? StateChanged;

    /// <summary>A Hello with this app's version arrived. Raised before the full push it triggers,
    /// so a handler can store the controller info the push is computed from. Any thread.</summary>
    public event EventHandler<HelloMessage>? HelloReceived;

    /// <summary>The transport just connected: start a session on it.</summary>
    public void Attach(IControllerConnection connection)
    {
        ControllerState? changed;
        lock (_gate)
        {
            if (_disposed) return;
            _connection = connection;
            _sent = Array.Empty<ButtonDisplay?>();
            changed = SetState(ControllerState.AwaitingHello);
            SendHelloRequest();
        }
        Raise(changed);
    }

    /// <summary>The transport went away.</summary>
    public void Detach()
    {
        ControllerState? changed;
        lock (_gate)
        {
            _connection = null;
            _sent = Array.Empty<ButtonDisplay?>();
            changed = SetState(ControllerState.NoController);
        }
        Raise(changed);
    }

    /// <summary>Handles the session messages (Hello; Alive needs nothing beyond arriving).</summary>
    public void OnMessage(ControllerMessage message)
    {
        if (message is not HelloMessage hello) return;
        var ok = hello.Version == ProtocolVersion.Current;
        if (!ok) AppLog.Error($"Controller speaks protocol v{hello.Version}; this app speaks v{ProtocolVersion.Current}");
        else HelloReceived?.Invoke(this, hello);

        ControllerState? changed;
        lock (_gate)
        {
            if (_connection is null || _disposed) return;
            _sent = Array.Empty<ButtonDisplay?>();
            changed = SetState(ok ? ControllerState.Ready : ControllerState.VersionMismatch);
            if (ok) PushLocked(ScreenTransition.None, everything: true);
        }
        Raise(changed);
    }

    /// <summary>Sends every button whose display changed, with no transition.</summary>
    public void Push()
    {
        lock (_gate)
        {
            if (_state == ControllerState.Ready) PushLocked(ScreenTransition.None, everything: false);
        }
    }

    /// <summary>Re-sends every button with no transition (e.g. the live preset was deleted).</summary>
    public void PushAll()
    {
        lock (_gate)
        {
            if (_state == ControllerState.Ready) PushLocked(ScreenTransition.None, everything: true);
        }
    }

    /// <summary>A preset change: every screen slides (even unchanged ones) and every LED is
    /// re-sent; wrapped in Begin/Commit when <paramref name="synchronized"/>.</summary>
    public void PushPresetChange(ScreenTransition transition, bool synchronized)
    {
        lock (_gate)
        {
            if (_state != ControllerState.Ready) return;
            if (synchronized) Send(ProtocolEncoder.EncodeBegin());
            var displays = _compute();
            EnsureCapacity(displays.Count);
            for (int i = 0; i < displays.Count; i++)
                if (displays[i] is ScreenDisplay s) { Send(ProtocolEncoder.EncodeScreen(i, transition, s.Content)); _sent[i] = s; }
            for (int i = 0; i < displays.Count; i++)
                if (displays[i] is LedDisplay l) { Send(ProtocolEncoder.EncodeLed(i, l.Value)); _sent[i] = l; }
            if (synchronized) Send(ProtocolEncoder.EncodeCommit());
        }
    }

    private void PushLocked(ScreenTransition transition, bool everything)
    {
        var displays = _compute();
        EnsureCapacity(displays.Count);
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            if (d is null || (!everything && Equals(_sent[i], d))) continue;
            Send(d switch
            {
                LedDisplay l => ProtocolEncoder.EncodeLed(i, l.Value),
                ScreenDisplay s => ProtocolEncoder.EncodeScreen(i, transition, s.Content),
                _ => throw new InvalidOperationException(d.GetType().Name),
            });
            _sent[i] = d;
        }
    }

    private void EnsureCapacity(int count)
    {
        if (_sent.Length < count) Array.Resize(ref _sent, count);
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_connection is null || _disposed) return;
            var now = Environment.TickCount64;
            if (_state == ControllerState.AwaitingHello
                && now - _lastHelloRequestMs >= (long)_options.HelloRetryInterval.TotalMilliseconds)
                SendHelloRequest();
            else if (_state == ControllerState.Ready
                     && now - _lastSendMs >= (long)_options.HeartbeatInterval.TotalMilliseconds)
                Send(ProtocolEncoder.EncodeHeartbeat());
        }
    }

    private void SendHelloRequest()
    {
        Send(ProtocolEncoder.EncodeHelloRequest());
        _lastHelloRequestMs = Environment.TickCount64;
    }

    /// <summary>Caller holds _gate.</summary>
    private void Send(byte[] frame)
    {
        _connection?.Send(frame);
        _lastSendMs = Environment.TickCount64;
    }

    /// <summary>Caller holds _gate. Returns the new state when it changed.</summary>
    private ControllerState? SetState(ControllerState state)
    {
        if (_state == state) return null;
        _state = state;
        return state;
    }

    private void Raise(ControllerState? changed)
    {
        if (changed is { } s) StateChanged?.Invoke(this, s);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _connection = null;
        }
        _timer.Dispose();
    }
}
```

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(core): ControllerLink owns the controller session (Hello, keepalive, dedup, bursts)`.

---

### Task 7: Wire `ControllerLink` into `VolcoRuntime`; nicknames in OSD labels; sequential WebSocket sends

**Files:**
- Modify: `src/Volco.Core/Runtime/VolcoRuntime.cs`
- Rewrite: `src/Volco.Core/Runtime/VolcoRuntimeOptions.cs`
- Modify: `src/Volco.Core/Input/SliderProcessor.cs` (Device-slider OSD label uses display names)
- Modify: `src/Volco.Core/Communication/WebSocketConnection.cs` (`Send`)
- Delete: `src/Volco.Core/Communication/ButtonFeedback.cs`, `tests/Volco.Core.Tests/Communication/ButtonFeedbackTests.cs`
- Modify: `src/Volco.App/ViewModels/ButtonItemViewModel.cs` (one line: `ButtonFeedback.ValueFor(live, …) ?? 0` → `ControllerDisplay.LedValueFor(live, …)`)
- Modify: `src/Volco.App/ViewModels/ActionCatalog.cs` (the doc `cref` to `ButtonFeedback` → `ControllerDisplay`)
- Modify: `tests/Volco.Core.Tests/Runtime/VolcoRuntimeTests.cs`, `tests/Volco.Core.Tests/Communication/WebSocketConnectionTests.cs`, `tests/Volco.App.Tests/TestRig.cs` (only if it passes options)

**Interfaces**
- **Consumes:** `ControllerLink` and `ControllerState` (Task 6), `ControllerDisplay` (Task 5), `DeviceDisplay` (Task 4), `ControllerInfoConfig` (Task 2).
- **Produces** (used by the App tasks):
  - `VolcoRuntime.ControllerState`, `event EventHandler<ControllerState>? ControllerStateChanged`, `event EventHandler? ControllerInfoChanged`
  - `VolcoRuntimeOptions` as a property record:
    ```csharp
    public sealed record VolcoRuntimeOptions
    {
        public static VolcoRuntimeOptions Default { get; } = new();
        public TimeSpan PendingSwitchTimeout { get; init; } = TimeSpan.FromSeconds(3);
        public bool SynchronizedPresetTransitions { get; init; } = true;
        public ControllerLinkOptions Link { get; init; } = ControllerLinkOptions.Default;
    }
    ```

- [ ] **Step 1: Rewrite the runtime's controller-push tests.** In `VolcoRuntimeTests.cs`:
  - Replace `NoTimedRepushes` with a quiet options value:

    ```csharp
    /// <summary>No HelloRequest retries or heartbeats inside a test unless it asks for them.</summary>
    private static readonly VolcoRuntimeOptions Quiet = new()
    {
        Link = new ControllerLinkOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
    };
    ```

    Use `Quiet` wherever `NoTimedRepushes` was used.
  - For each `new VolcoRuntimeOptions(Array.Empty<TimeSpan>()) { PendingSwitchTimeout = X }`, write `Quiet with { PendingSwitchTimeout = X }`.
  - Delete `Connected_RepushesAfterEachConfiguredDelay` and `FirstMessageAfterConnect_ForcesOneRepush`.
  - At line ~965 (`Dispose_…`), replace `new VolcoRuntimeOptions(new[] { TimeSpan.FromMilliseconds(40) })` with `Quiet`, and remove any assertion about re-push timers there.

Add a helper and an LED-hardware Hello:

```csharp
    private static readonly HelloMessage LedHello = new(ProtocolVersion.Current, 5,
        new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair });

    /// <summary>Transport connected and the controller said hello.</summary>
    private static void ConnectAndGreet(FakeControllerConnection connection, HelloMessage? hello = null)
    {
        connection.SetStatus(ConnectionStatus.Connected);
        connection.RaiseMessage(hello ?? LedHello);
    }
```

Replace the bodies of `Connected_PushesStateImmediately`, `OwnVolumeChanges_DoNotResendIdenticalFrames`, `MuteChange_ResendsFrames` and `PresetChange_PushesTheNewPresetsFeedback` so that they call `ConnectAndGreet(connection)` instead of `SetStatus(Connected)`. Rename `Connected_PushesStateImmediately` to `Hello_PushesStateForEveryButton`, and add this assertion to it:

```csharp
        Assert.True(ContainsFrame(frames, ProtocolEncoder.EncodeHelloRequest()), "no HelloRequest on connect");
```

In `PresetChange_PushesTheNewPresetsFeedback`, expect the burst framing:

```csharp
        Assert.True(ContainsFrame(newFrames, ProtocolEncoder.EncodeBegin()), "no Begin");
        Assert.True(ContainsFrame(newFrames, ProtocolEncoder.EncodeCommit()), "no Commit");
```

Then add these tests:

```csharp
    [Fact]
    public void Connected_SendsOnlyHelloRequest_UntilHello()
    {
        StartRuntime();
        var connection = Connection(0);
        connection.SetStatus(ConnectionStatus.Connected);
        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: false);

        Assert.Equal(new[] { ProtocolEncoder.EncodeHelloRequest() }, connection.SentFrames);
    }

    [Fact]
    public void Hello_StoresControllerInfo_AndRaisesTheEvents()
    {
        var config = NewConfig();
        var runtime = StartRuntime(config);
        var states = new Recorder<ControllerState>();
        var infos = 0;
        runtime.ControllerStateChanged += states.Handler;
        runtime.ControllerInfoChanged += (_, _) => Interlocked.Increment(ref infos);

        ConnectAndGreet(Connection(0));

        Assert.Equal(ControllerState.Ready, runtime.ControllerState);
        Assert.Equal(new[] { ControllerState.AwaitingHello, ControllerState.Ready }, states.Items);
        Assert.Equal(new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }, config.Config.Controller!.Buttons);
        Assert.Equal(1, infos);

        Connection(0).RaiseMessage(LedHello);         // same hardware again: no new info event
        Assert.Equal(1, infos);
    }

    [Fact]
    public void Hello_WrongVersion_IsAMismatch_AndInputStillWorks()
    {
        var runtime = StartRuntime();
        var connection = Connection(0);
        ConnectAndGreet(connection, new HelloMessage(42, 5, new[] { IndicatorKind.Led }));
        var after = connection.SentCount;

        Press(connection, 0);

        Assert.Equal(ControllerState.VersionMismatch, runtime.ControllerState);
        Assert.True(WaitUntil(() => _engine.ToggleMuteCalls.Count > 0), "button input ignored during a mismatch");
        Assert.Equal(after, connection.SentCount);
    }

    [Fact]
    public void Reconnect_WithoutControllerReboot_RequestsHelloAndPushesEverything()
    {
        // Review Focus #3: a new session on a controller that never rebooted.
        var runtime = StartRuntime();
        ConnectAndGreet(Connection(0));
        runtime.Reconnect();
        var fresh = Connection(1);

        fresh.SetStatus(ConnectionStatus.Connected);
        Assert.Equal(new[] { ProtocolEncoder.EncodeHelloRequest() }, fresh.SentFrames);
        fresh.RaiseMessage(LedHello);

        Assert.True(ContainsFrame(fresh.SentFrames, ProtocolEncoder.EncodeLed(0, 0)));
        Assert.True(ContainsFrame(fresh.SentFrames, ProtocolEncoder.EncodeLed(2, 0)));
    }

    [Fact]
    public void NextPreset_SlidesLeft_EvenWhenWrapping()
    {
        var config = TwoPresetConfig();
        config.Config.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = 5, Buttons = { IndicatorKind.Screen } };
        var runtime = StartRuntime(config);
        var connection = Connection(0);
        ConnectAndGreet(connection, new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Screen }));

        runtime.SetActivePreset("gaming");
        var baseline = connection.SentCount;
        runtime.NextPreset();                                     // gaming → default wraps

        var screen = connection.SentFrames.Skip(baseline).First(f => f[2] == (byte)MessageType.Screen);
        Assert.Equal((byte)ScreenTransition.SlideLeft, screen[4]);
    }

    [Fact]
    public void GoToAnEarlierPreset_SlidesRight()
    {
        var config = TwoPresetConfig();
        var runtime = StartRuntime(config);
        var connection = Connection(0);
        ConnectAndGreet(connection, new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Screen }));
        runtime.SetActivePreset("gaming");
        var baseline = connection.SentCount;

        runtime.SetActivePreset(AppConfig.DefaultPresetId);

        var screen = connection.SentFrames.Skip(baseline).First(f => f[2] == (byte)MessageType.Screen);
        Assert.Equal((byte)ScreenTransition.SlideRight, screen[4]);
    }

    [Fact]
    public void Unsynchronized_PresetChange_HasNoBeginOrCommit()
    {
        var runtime = StartRuntime(TwoPresetConfig(), options: Quiet with { SynchronizedPresetTransitions = false });
        var connection = Connection(0);
        ConnectAndGreet(connection);
        var baseline = connection.SentCount;

        runtime.SetActivePreset("gaming");

        var burst = connection.SentFrames.Skip(baseline).ToList();
        Assert.NotEmpty(burst);
        Assert.DoesNotContain(burst, f => f[2] is (byte)MessageType.Begin or (byte)MessageType.Commit);
    }

    [Fact]
    public void DeletingTheLivePreset_PushesInstantly()
    {
        var config = TwoPresetConfig();
        var runtime = StartRuntime(config);
        var connection = Connection(0);
        ConnectAndGreet(connection, new HelloMessage(ProtocolVersion.Current, 5, new[] { IndicatorKind.Screen }));
        runtime.SetActivePreset("gaming");
        var baseline = connection.SentCount;

        config.Config.Presets.RemoveAll(p => p.Id == "gaming");
        runtime.SetActivePreset(AppConfig.DefaultPresetId);       // what ButtonsViewModel does

        var screen = connection.SentFrames.Skip(baseline).First(f => f[2] == (byte)MessageType.Screen);
        Assert.Equal((byte)ScreenTransition.None, screen[4]);
        Assert.DoesNotContain(connection.SentFrames.Skip(baseline), f => f[2] == (byte)MessageType.Begin);
    }

    [Fact]
    public void ConfigChanged_PushesActionEdits()
    {
        var config = NewConfig();
        StartRuntime(config);
        var connection = Connection(0);
        ConnectAndGreet(connection);
        _engine.DeviceStates[Speakers] = (0.5f, true);            // muted, but button 2 does nothing yet
        var baseline = connection.SentCount;

        config.Config.Buttons[2].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
        config.Save();                                            // raises ConfigChanged

        Assert.True(WaitUntil(() => ContainsFrame(connection.SentFrames.Skip(baseline), ProtocolEncoder.EncodeLed(2, 1))));
    }

    [Fact]
    public void DevicesChanged_RefreshesAStoredFriendlyName()
    {
        var config = NewConfig(c => c.Devices.Add(new DeviceSettingsConfig { Id = Speakers, FriendlyName = "Old name", Nickname = "Desk" }));
        StartRuntime(config);
        var renamed = new AudioDevice(Speakers, "Speakers (new driver)", AudioFlow.Render, true);
        _engine.Devices[0] = renamed;

        _engine.RaiseDeviceChanged(DeviceChangeKind.StateChanged, renamed);

        Assert.Equal("Speakers (new driver)", config.Config.FindDevice(Speakers)!.FriendlyName);
    }

    [Fact]
    public void MuteOsd_UsesTheNickname()
    {
        var config = NewConfig(c => c.Devices.Add(new DeviceSettingsConfig { Id = Speakers, Nickname = "Desk speakers" }));
        var runtime = StartRuntime(config);
        var volumes = new Recorder<VolumeChangeNotification>();
        runtime.VolumeChanged += volumes.Handler;

        Press(Connection(0), 0);                                  // button 0 mutes the default output
        _engine.DeviceStates[Speakers] = (0.5f, true);
        _engine.RaiseDeviceVolumeChanged(Speakers, 0.5f, muted: true, isOwnChange: true);

        Assert.True(WaitUntil(() => volumes.Count > 0));
        Assert.Equal("Desk speakers", volumes.Items[0].Label);
    }
```

The file already has `TwoPresetConfig()`. Confirm its button 2 has a preset action so the preset test sees a screen frame; if not, give it a `NextPreset` single press in both presets. Check that `VolumeChangeNotification`'s label property is called `Label` (see `src/Volco.Core/Input/VolumeChangeNotification.cs`) and adjust the assertion if it isn't.

Run `grep -n "\"Microphone\"\|Label)" tests/Volco.Core.Tests/Runtime/VolcoRuntimeTests.cs`. Any test that expected the hard-coded `"Microphone"` mute-input label now gets the default input's display name, `"Mic"` in this fixture.

Add to `WebSocketConnectionTests.cs`:

```csharp
    [Fact]
    public async Task Send_ManyFramesQuickly_AllArriveInOrder()
    {
        _connection.Connect();
        using var client = new ClientWebSocket();
        await ConnectWithRetryAsync(client, new Uri($"ws://localhost:{_port}/"));
        await Task.Delay(50);

        for (byte i = 0; i < 100; i++) _connection.Send(new[] { i });

        var received = new List<byte>();
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (received.Count < 100)
        {
            var r = await client.ReceiveAsync(buffer, cts.Token);
            received.AddRange(buffer.Take(r.Count));
        }
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)i), received);
    }
```

- [ ] **Step 2: Implement.** Rewrite `VolcoRuntimeOptions.cs`:

```csharp
namespace Volco.Core.Runtime;

/// <summary>Tunables for <see cref="VolcoRuntime"/>. Exists so tests can shorten delays; the app
/// always uses <see cref="Default"/>.</summary>
public sealed record VolcoRuntimeOptions
{
    public static VolcoRuntimeOptions Default { get; } = new();

    /// <summary>How long a device-switch press waits for its DefaultChanged before the pending
    /// OSD is discarded (a switch that never lands must not claim a later, unrelated change).</summary>
    public TimeSpan PendingSwitchTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Wrap a preset change in Begin/Commit so every screen slides on the same loop pass
    /// (spec §3.4). Off: the same frames go out unwrapped, so each screen slides as its message
    /// arrives; a test switch for judging the delay.</summary>
    public bool SynchronizedPresetTransitions { get; init; } = true;

    /// <summary>HelloRequest retry, heartbeat and tick intervals.</summary>
    public ControllerLinkOptions Link { get; init; } = ControllerLinkOptions.Default;
}
```

`VolcoRuntime.cs` changes, in order:

1. **Class doc.** Replace "controller-state (LED) pushes with change detection, and the post-connect re-push mitigations" with "and the controller session (<see cref=\"ControllerLink\"/>: Hello, keepalive, LED and screen pushes)". In `<remarks>`, replace "or the post-connect re-push timers" with "or the controller link's keepalive timer".
2. **Delete:** `_pushStateLock`, `_lastPushedFrames`, `_postConnectLock`, `_postConnectTimers`, `_awaitingFirstMessageAfterConnect` and their doc comments; the methods `ArmFirstMessagePush`, `ScheduleTimedRepushes`, `DisposePostConnectTimers` and `FramesEqual`; the mitigation-(b) block at the top of `OnMessageReceived`; and the timer disposal in `Dispose()`.
3. **Fields and constructor.**
   - Add the field `private readonly ControllerLink _link;`.
   - In the constructor, after `_options = …`:

     ```csharp
             _link = new ControllerLink(
                 () => ControllerDisplay.Compute(Config.Config, ActivePresetId, Audio), _options.Link);
             _link.StateChanged += (_, state) => { if (!IsDisposed) ControllerStateChanged?.Invoke(this, state); };
             _link.HelloReceived += OnHello;
     ```

4. **Public surface** (next to `ConnectionStatus`):

```csharp
    /// <summary>Where the controller session stands (spec §5.5).</summary>
    public ControllerState ControllerState => _link.State;

    /// <summary>The controller session's state changed. Any thread.</summary>
    public event EventHandler<ControllerState>? ControllerStateChanged;

    /// <summary>A Hello reported hardware different from the stored <see cref="AppConfig.Controller"/>
    /// (which has already been updated). Any thread.</summary>
    public event EventHandler? ControllerInfoChanged;
```

5. **Presets.** Replace `SetActivePreset` and `NextPreset`:

```csharp
    /// <inheritdoc/>
    public void SetActivePreset(string presetId) => SetActivePreset(presetId, transition: null);

    /// <inheritdoc/>
    public void NextPreset()
    {
        var presets = Config.Config.Presets;
        if (presets.Count == 0) return;
        var index = presets.FindIndex(p => p.Id == ActivePresetId);
        // Next always slides left, including the wrap from the last preset to the first (spec §2.3).
        SetActivePreset(presets[(index + 1) % presets.Count].Id, ScreenTransition.SlideLeft);
    }

    private void SetActivePreset(string presetId, ScreenTransition? transition)
    {
        if (IsDisposed) return;
        var presets = Config.Config.Presets;
        if (!presets.Exists(p => p.Id == presetId)) return;
        var stored = Volatile.Read(ref _activePresetId);
        var previous = ActivePresetId;
        Volatile.Write(ref _activePresetId, presetId);
        if (previous == presetId)
        {
            // The stored live preset was deleted and ActivePresetId already fell back to this one:
            // nothing slides, but the controller must catch up now (spec §2.3).
            if (stored is not null && !presets.Exists(p => p.Id == stored)) _link.PushAll();
            return;
        }

        AppLog.Info($"Active preset: {presetId}");
        ActivePresetChanged?.Invoke(this, presetId);

        var from = presets.FindIndex(p => p.Id == previous);
        var to = presets.FindIndex(p => p.Id == presetId);
        var slide = transition ?? (to > from ? ScreenTransition.SlideLeft : ScreenTransition.SlideRight);
        _link.PushPresetChange(slide, _options.SynchronizedPresetTransitions);
    }
```

6. **`Start()`.** After `Config.ConnectionConfigChanged += OnConfigChanged;`, add `Config.ConfigChanged += OnSettingsChanged;`, and add the handler:

```csharp
    /// <summary>Any non-connection save (actions, presets, nicknames, icons): the controller
    /// follows. A deleted live preset falls back to the first one and is pushed instantly.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        var stored = Volatile.Read(ref _activePresetId);
        if (stored is not null && !Config.Config.Presets.Exists(p => p.Id == stored))
        {
            Volatile.Write(ref _activePresetId, null);
            ActivePresetChanged?.Invoke(this, ActivePresetId);
            _link.PushAll();
            return;
        }
        _link.Push();
    }
```

7. **Status changes.** In `OnConnectionStatusChanged`, replace the whole `if (status == ConnectionStatus.Connected) { … }` block with:

```csharp
        if (status == ConnectionStatus.Connected) _link.Attach((IControllerConnection)sender!);
        else _link.Detach();
```

8. **Inbound messages.** In `OnMessageReceived`, add cases before `SliderMessage`:

```csharp
            case HelloMessage or AliveMessage:
                _link.OnMessage(msg);
                break;
```

9. **Hello handler:**

```csharp
    /// <summary>Stores the controller's hardware before the link pushes (spec §3.4); saves only
    /// when it changed.</summary>
    private void OnHello(object? sender, HelloMessage hello)
    {
        var info = new ControllerInfoConfig
        {
            ProtocolVersion = hello.Version, Sliders = hello.SliderCount, Buttons = hello.Buttons.ToList(),
        };
        if (info.SameAs(Config.Config.Controller)) return;
        Config.Config.Controller = info;
        Config.RequestSave();
        if (!IsDisposed) ControllerInfoChanged?.Invoke(this, EventArgs.Empty);
    }
```

10. **`PushControllerState`.** Replace the method and its doc with:

```csharp
    /// <summary>Sends whatever changed on the controller (the link dedups per button).</summary>
    private void PushControllerState()
    {
        if (!IsDisposed) _link.Push();
    }
```

11. **Device changes.** In `OnDevicesChangedForControllerState`:
    - After `DevicesChanged?.Invoke(this, e);`, add:

      ```csharp
              if (RefreshStoredDeviceNames()) Config.RequestSave();
      ```

    - Remove the `PushControllerState()` inside the `DefaultChanged` block, and call `PushControllerState();` once at the end of the method. Added and removed devices change screens too (an unplugged device), and dedup keeps this cheap.
    - Add:

      ```csharp
          /// <summary>Keeps each customized device's stored Windows name current (spec §5.1).</summary>
          private bool RefreshStoredDeviceNames()
          {
              var changed = false;
              var live = Audio.GetDevices(AudioFlow.Render).Concat(Audio.GetDevices(AudioFlow.Capture)).ToList();
              foreach (var entry in Config.Config.Devices)
              {
                  var device = live.Find(d => string.Equals(d.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                  if (device is null || device.FriendlyName == entry.FriendlyName) continue;
                  entry.FriendlyName = device.FriendlyName;
                  changed = true;
              }
              return changed;
          }
      ```

12. **OSD labels.** In `ShowMuteOsd`, the three label lines become:

```csharp
            case ButtonActionType.MuteCurrentOutput:
                label = Audio.GetDefaultDevice(AudioFlow.Render) is { } output
                    ? DeviceDisplay.For(Config.Config, output).Name : "Output";
                …
            case ButtonActionType.MuteSpecificDevice:
                …
                label = DeviceDisplay.NameOf(Config.Config, Audio, actionConfig.Device, "Output");
                …
            case ButtonActionType.MuteCurrentInput:
                label = Audio.GetDefaultDevice(AudioFlow.Capture) is { } input
                    ? DeviceDisplay.For(Config.Config, input).Name : "Microphone";
```

In `ShowDeviceOsd`:

```csharp
        var device = Audio.GetDevices(AudioFlow.Render)
            .FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        var label = device is not null ? DeviceDisplay.For(Config.Config, device).Name
                  : string.IsNullOrEmpty(friendlyName) ? "Output" : friendlyName;
```

13. **`Dispose()`.**
    - Add `Config.ConfigChanged -= OnSettingsChanged;`.
    - After the transport is retired, add `_link.Dispose();`.
    - Remove the `_postConnectLock` block.
14. **Usings:** `using Volco.Core.Devices;` (`Volco.Core.Communication` is already imported).

**`SliderProcessor`.** Give `BuildSliderLabel` an optional resolved name, so existing callers and tests keep working:

```csharp
    public static string BuildSliderLabel(SliderConfig slider, string? currentOutputName, string? configuredDeviceName = null)
    {
        switch (slider.Type)
        {
            case SliderMappingType.Device:
                if (!string.IsNullOrEmpty(configuredDeviceName))
                    return configuredDeviceName;
                if (!string.IsNullOrEmpty(slider.DeviceName))
                    return slider.DeviceName;
                return string.IsNullOrEmpty(currentOutputName) ? "Output" : currentOutputName;
            // … other cases unchanged
```

At the call site (line ~264), resolve both names through `DeviceDisplay`:

```csharp
                var config = _configManager.Config;
                string? currentOutput = null, configured = null;
                if (sliderConfig.Type == SliderMappingType.Device)
                {
                    if (string.IsNullOrEmpty(sliderConfig.DeviceName))
                        currentOutput = _audio.GetDefaultDevice(AudioFlow.Render) is { } d ? DeviceDisplay.For(config, d).Name : null;
                    else
                        configured = DeviceDisplay.NameOf(config, _audio, sliderConfig.Device, sliderConfig.DeviceName);
                }
                var label = BuildSliderLabel(sliderConfig, currentOutput, configured);
```

Check the field name holding the `ConfigManager` in `SliderProcessor` (`grep -n "ConfigManager" src/Volco.Core/Input/SliderProcessor.cs`) and use it. Add `using Volco.Core.Devices;`.

**`WebSocketConnection.Send`** becomes sequential:

```csharp
    private readonly object _sendLock = new();
    private Task _sendTail = Task.CompletedTask;

    /// <summary>Queues a frame. .NET allows one outstanding SendAsync per socket, so frames are
    /// chained: each send starts after the previous one finished, in call order (spec §3.5).</summary>
    public void Send(byte[] data)
    {
        var ws = _activeClient;
        if (ws?.State != WebSocketState.Open) return;
        lock (_sendLock)
        {
            _sendTail = _sendTail.ContinueWith(async _ =>
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
                {
                    AppLog.Debug($"WebSocket send dropped: {ex.Message}");
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }
```

Finally:
- delete `ButtonFeedback.cs` and `ButtonFeedbackTests.cs`;
- change `ButtonItemViewModel.Refresh`'s `LightValue = ButtonFeedback.ValueFor(live, …) ?? 0;` to `LightValue = ControllerDisplay.LedValueFor(live, _owner.AppConfig, _owner.LivePresetId, _owner.Audio);`;
- fix the `cref` in `ActionCatalog.LightSource`'s doc to `Volco.Core.Communication.ControllerDisplay`;
- run `grep -rn "ButtonFeedback\|PostConnectRepushDelays" src tests`; it must print nothing.

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(runtime): controller session via ControllerLink; preset slides; nicknames in OSD; sequential WebSocket sends`.

---

### Task 8: Icon and font assets, and the generator

**Files:**
- Create: `firmware/tools/draw_icons.py` (draws the 28×28 icon PNGs once; the PNGs are the source of truth after that)
- Create: `firmware/assets/icons/{generic,speaker,speakers,headphones,headset,earbuds,display,mic,studiomic,webcam,presets}.png` (output of `draw_icons.py`)
- Create: `firmware/assets/fonts/Arimo-Regular.ttf`, `Arimo-Bold.ttf`, `OFL.txt`
- Create: `firmware/tools/gen_assets.py`
- Create (generated): `firmware/lib/volco_render/src/volco_icons.h`, `volco_icons.cpp`, `volco_fonts.h`, `volco_fonts.cpp`
- Create (generated): `tools/simulator/icons.js`
- Regenerate: `src/Volco.Core/Communication/ScreenIcon.g.cs` (must come out identical to Task 1's)

**Interfaces**
- **Produces** (C++, used by Tasks 14–16):

```cpp
namespace volco {
constexpr uint8_t kIconCount = 11;      // manifest rows
constexpr uint8_t kIconSize = 28;
constexpr uint8_t kIconRowBytes = 4;    // ceil(28 / 8)
// Row-major, LSB = leftmost pixel of each byte (XBM order).
extern const uint8_t kIconBits[kIconCount][kIconSize * kIconRowBytes];

struct Glyph { uint16_t codepoint; uint8_t width, height; int8_t xOffset, yOffset; uint8_t advance; uint16_t offset; };
struct Font { const Glyph* glyphs; uint16_t glyphCount; const uint8_t* bitmaps; uint8_t ascent, descent; };
extern const Font kFontTop;   // Arimo Regular, 10 px
extern const Font kFontMain;  // Arimo Bold, 14 px
}
```

Notes on the font format:
- **Glyph positions.** `yOffset` is the glyph's top row relative to the baseline (negative = above it). `xOffset` is relative to the pen position.
- **Glyph bitmaps** are row-major, `(width+7)/8` bytes per row, LSB-first.
- **Order.** Glyphs are sorted by codepoint, for a binary search. Every codepoint used fits in 16 bits.

**Simulator output:** `window.VOLCO_ICONS = [[ "0011…" × 28 rows ], …]`, indexed by icon ID.

- [ ] **Step 1: Fetch the fonts and their licence.** Arimo is under the SIL Open Font License 1.1; Task 18 corrects the spec, which says Apache.

```bash
mkdir -p firmware/assets/fonts
curl -sSL -o firmware/assets/fonts/Arimo-Regular.ttf https://raw.githubusercontent.com/googlefonts/Arimo/main/fonts/ttf/Arimo-Regular.ttf
curl -sSL -o firmware/assets/fonts/Arimo-Bold.ttf    https://raw.githubusercontent.com/googlefonts/Arimo/main/fonts/ttf/Arimo-Bold.ttf
curl -sSL -o firmware/assets/fonts/OFL.txt           https://raw.githubusercontent.com/googlefonts/Arimo/main/OFL.txt
python3 -c "from PIL import ImageFont; f=ImageFont.truetype('firmware/assets/fonts/Arimo-Bold.ttf',14); print(f.getmetrics())"
```
Expected output: `(13, 3)`.

- [ ] **Step 2: Write `firmware/tools/draw_icons.py`.** It draws each icon from the mockup's 24-unit vector drawings at 8× supersampling, downsamples to 28×28 and thresholds:

```python
#!/usr/bin/env python3
"""Draws Volco's 28x28 1-bit screen icons from vector descriptions (the mockup's drawings, on a
24-unit grid). Run once, inspect, commit the PNGs: after that the PNGs are the source of truth and
can be hand-edited. Usage: python3 firmware/tools/draw_icons.py [--preview out.png]"""
import math, sys
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "assets" / "icons"
SIZE, SS = 28, 8                      # final size, supersampling factor
K = SIZE / 24 * SS                    # 24-unit grid -> supersampled pixels
W = 2.3 * K                           # stroke width

def P(x, y): return (x * K, y * K)

class Pen:
    def __init__(self):
        self.img = Image.new("L", (SIZE * SS, SIZE * SS), 0)
        self.d = ImageDraw.Draw(self.img)
    def dot(self, x, y, r):            # filled circle, r in grid units
        cx, cy = P(x, y); rr = r * K
        self.d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=255)
    def line(self, *pts):              # polyline with round caps and joins
        xy = [P(*p) for p in pts]
        self.d.line(xy, fill=255, width=round(W), joint="curve")
        for (x, y) in xy:
            self.d.ellipse([x - W / 2, y - W / 2, x + W / 2, y + W / 2], fill=255)
    def poly(self, *pts):
        self.d.polygon([P(*p) for p in pts], fill=255)
    def arc(self, cx, cy, r, a0, a1):  # degrees, clockwise from 3 o'clock (y down), stroke centred on r
        x, y = P(cx, cy); R = r * K + W / 2
        self.d.arc([x - R, y - R, x + R, y + R], a0, a1, fill=255, width=round(W))
    def ring(self, cx, cy, r):
        x, y = P(cx, cy); R = r * K + W / 2
        self.d.ellipse([x - R, y - R, x + R, y + R], outline=255, width=round(W))
    def rbox(self, x, y, w, h, r, fill):
        x0, y0 = P(x, y); x1, y1 = P(x + w, y + h)
        if fill:
            self.d.rounded_rectangle([x0, y0, x1, y1], r * K, fill=255)
        else:
            g = W / 2
            self.d.rounded_rectangle([x0 - g, y0 - g, x1 + g, y1 + g], r * K + g, outline=255, width=round(W))
    def curve(self, p0, c, p1, steps=20):  # quadratic Bezier as a polyline
        pts = [((1-t)**2*p0[0] + 2*(1-t)*t*c[0] + t*t*p1[0], (1-t)**2*p0[1] + 2*(1-t)*t*c[1] + t*t*p1[1])
               for t in (i / steps for i in range(steps + 1))]
        self.line(*pts)
    def done(self):
        small = self.img.resize((SIZE, SIZE), Image.LANCZOS)
        return small.point(lambda v: 255 if v >= 110 else 0).convert("1")

def generic(p):
    for x, a, b in ((4, 10, 14), (8, 6, 18), (12, 3, 21), (16, 7, 17), (20, 10, 14)): p.line((x, a), (x, b))
def speaker(p):
    p.poly((3, 9), (7, 9), (12, 4), (12, 20), (7, 15), (3, 15))
    p.arc(13, 12, 4, -52, 52); p.arc(13, 12, 8, -52, 52)
def speakers(p):
    for x in (1.5, 13):
        p.rbox(x, 2.5, 9.5, 19, 2, fill=False); p.ring(x + 4.75, 15, 2.6); p.dot(x + 4.75, 7.5, 1.3)
def headphones(p):
    p.arc(12, 13, 8.5, 180, 360); p.rbox(2.5, 13, 5, 8.5, 1.5, True); p.rbox(16.5, 13, 5, 8.5, 1.5, True)
def headset(p):
    p.arc(12, 12, 8.5, 180, 360); p.rbox(2.5, 11, 5, 8, 1.5, True); p.rbox(16.5, 11, 5, 8, 1.5, True)
    p.curve((5, 19), (6, 22.5), (11, 22)); p.dot(12.5, 22, 1.6)
def earbuds(p):
    p.dot(7, 8, 4); p.line((8.5, 11), (8.5, 21)); p.dot(17, 8, 4); p.line((15.5, 11), (15.5, 21))
def display(p):
    p.rbox(2, 3, 20, 13, 1.5, False); p.line((12, 16.5), (12, 20)); p.line((7, 21), (17, 21))
def mic(p):
    p.rbox(8, 2, 8, 13, 4, True); p.arc(12, 11, 6.5, 0, 180); p.line((12, 17.5), (12, 21.5)); p.line((8, 21.5), (16, 21.5))
def studiomic(p):
    p.rbox(7.5, 1.5, 9, 13, 4.5, False); p.line((7.5, 6), (16.5, 6)); p.line((7.5, 10), (16.5, 10))
    p.arc(12, 11, 7.5, 9, 171); p.line((12, 18.5), (12, 21.5)); p.line((6, 21.5), (18, 21.5))
def webcam(p):
    p.ring(12, 10, 7); p.dot(12, 10, 2.6); p.line((12, 17), (12, 20.5)); p.line((6, 21.5), (18, 21.5))
def presets(p):
    p.poly((12, 3), (21, 8), (12, 13), (3, 8)); p.line((3, 12.5), (12, 17.5), (21, 12.5)); p.line((3, 17), (12, 22), (21, 17))

ICONS = [("generic", generic), ("speaker", speaker), ("speakers", speakers), ("headphones", headphones),
         ("headset", headset), ("earbuds", earbuds), ("display", display), ("mic", mic),
         ("studiomic", studiomic), ("webcam", webcam), ("presets", presets)]

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    images = []
    for stem, draw in ICONS:
        pen = Pen(); draw(pen); img = pen.done()
        img.save(OUT / f"{stem}.png"); images.append(img)
    if "--preview" in sys.argv:
        out = Path(sys.argv[sys.argv.index("--preview") + 1])
        sheet = Image.new("1", (len(images) * (SIZE + 4), SIZE), 0)
        for i, img in enumerate(images): sheet.paste(img, (i * (SIZE + 4), 0))
        sheet.resize((sheet.width * 8, sheet.height * 8), Image.NEAREST).save(out)
    print(f"wrote {len(images)} icons to {OUT}")

if __name__ == "__main__":
    main()
```

Run `python3 firmware/tools/draw_icons.py --preview /tmp/claude-0/-root-dev-volco/e4b9ebc0-3e85-4074-9113-ad7a2dc0800e/scratchpad/icons.png` and open the preview with the Read tool. Every icon should be recognisable and match the Devices-page mockup. If a stroke disappears at 28 px, raise the threshold's input by lowering `110` to `90` and re-run.

- [ ] **Step 3: Write `firmware/tools/gen_assets.py`:**

```python
#!/usr/bin/env python3
"""Generates every derived asset from firmware/assets (spec §4.4, §4.5, §5.4, §7):
  firmware/lib/volco_render/src/volco_icons.{h,cpp}   icon bitmaps (XBM row order)
  firmware/lib/volco_render/src/volco_fonts.{h,cpp}   Arimo 10 px Regular + 14 px Bold, Volco font format
  src/Volco.Core/Communication/ScreenIcon.g.cs        the C# icon enum
  tools/simulator/icons.js                            icon bitmaps for the web simulator
Needs Python 3 and Pillow >= 10. Outputs are committed; a normal build never runs this.
Usage: python3 firmware/tools/gen_assets.py"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

FW = Path(__file__).resolve().parent.parent
ROOT = FW.parent
ASSETS = FW / "assets"
RENDER = FW / "lib" / "volco_render" / "src"
HEADER = "// Generated by firmware/tools/gen_assets.py. Do not edit.\n"

# Glyphs every screen font carries: ASCII, Latin-1, Hebrew letters, a few punctuation marks.
CODEPOINTS = (list(range(0x20, 0x7F)) + list(range(0xA0, 0x100)) + list(range(0x5D0, 0x5EB))
              + list(range(0x5F0, 0x5F5)) + [0x2013, 0x2014, 0x2022, 0x2026])
FONTS = [("kFontTop", "Arimo-Regular.ttf", 10), ("kFontMain", "Arimo-Bold.ttf", 14)]

def manifest():
    rows = []
    for line in (ASSETS / "icons" / "manifest.txt").read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#"): continue
        id_, stem, cs, offered = line.split()
        rows.append((int(id_), stem, cs, offered == "yes"))
    assert [r[0] for r in rows] == list(range(len(rows))), "manifest ids must be 0..n-1 in order"
    return rows

def icon_rows(stem):
    img = Image.open(ASSETS / "icons" / f"{stem}.png").convert("1")
    assert img.size == (28, 28), f"{stem}.png must be 28x28"
    return [[1 if img.getpixel((x, y)) else 0 for x in range(28)] for y in range(28)]

def pack(bits_row):
    out = []
    for b in range(0, len(bits_row), 8):
        byte = 0
        for i, bit in enumerate(bits_row[b:b + 8]):
            if bit: byte |= 1 << i
        out.append(byte)
    return out

def write_icons(rows):
    h = [HEADER, "#pragma once\n#include <stdint.h>\n\nnamespace volco {\n",
         f"constexpr uint8_t kIconCount = {len(rows)};\n",
         "constexpr uint8_t kIconSize = 28;\nconstexpr uint8_t kIconRowBytes = 4;\n",
         "// Row-major, LSB = leftmost pixel of each byte (XBM order).\n",
         "extern const uint8_t kIconBits[kIconCount][kIconSize * kIconRowBytes];\n"]
    for id_, stem, _, _ in rows: h.append(f"constexpr uint8_t kIcon_{stem} = {id_};\n")
    h.append("}  // namespace volco\n")
    (RENDER / "volco_icons.h").write_text("".join(h))
    c = [HEADER, '#include "volco_icons.h"\n\nnamespace volco {\nconst uint8_t kIconBits[kIconCount][kIconSize * kIconRowBytes] = {\n']
    for id_, stem, _, _ in rows:
        data = [b for row in icon_rows(stem) for b in pack(row)]
        c.append(f"  {{  // {id_} {stem}\n    " + ", ".join(f"0x{b:02X}" for b in data) + "},\n")
    c.append("};\n}  // namespace volco\n")
    (RENDER / "volco_icons.cpp").write_text("".join(c))

def render_glyph(font, ch, size):
    pad, base = size, size * 2
    img = Image.new("L", (size * 4, size * 3), 0)
    ImageDraw.Draw(img).text((pad, base), ch, font=font, fill=255, anchor="ls")
    mono = img.point(lambda v: 255 if v >= 110 else 0)
    return mono, mono.getbbox(), pad, base

def write_fonts():
    h = [HEADER, "#pragma once\n#include <stdint.h>\n\nnamespace volco {\n",
         "struct Glyph { uint16_t codepoint; uint8_t width, height; int8_t xOffset, yOffset; uint8_t advance; uint16_t offset; };\n",
         "struct Font { const Glyph* glyphs; uint16_t glyphCount; const uint8_t* bitmaps; uint8_t ascent, descent; };\n"]
    c = [HEADER, '#include "volco_fonts.h"\n\nnamespace volco {\n']
    for name, file, size in FONTS:
        font = ImageFont.truetype(str(ASSETS / "fonts" / file), size)
        notdef, _, _, _ = render_glyph(font, "￿", size)
        glyphs, bitmap = [], []
        for cp in CODEPOINTS:
            ch = chr(cp)
            img, bbox, pad, base = render_glyph(font, ch, size)
            if cp != 0x20 and img.tobytes() == notdef.tobytes():
                continue                                   # not in the font
            advance = round(font.getlength(ch))
            if bbox is None:
                glyphs.append((cp, 0, 0, 0, 0, advance, len(bitmap))); continue
            x0, y0, x1, y1 = bbox
            offset = len(bitmap)
            for y in range(y0, y1):
                bitmap += pack([1 if img.getpixel((x, y)) else 0 for x in range(x0, x1)])
            glyphs.append((cp, x1 - x0, y1 - y0, x0 - pad, y0 - base, advance, offset))
        ascent, descent = font.getmetrics()
        h.append(f"extern const Font {name};  // {file} {size} px, {len(glyphs)} glyphs\n")
        c.append(f"static const Glyph {name}Glyphs[] = {{\n")
        for g in glyphs: c.append("  {0x%04X, %d, %d, %d, %d, %d, %d},\n" % g)
        c.append(f"}};\nstatic const uint8_t {name}Bitmaps[] = {{\n  " + ", ".join(f"0x{b:02X}" for b in bitmap) + "\n};\n")
        c.append(f"const Font {name} = {{{name}Glyphs, {len(glyphs)}, {name}Bitmaps, {ascent}, {descent}}};\n\n")
    h.append("extern const Font kFontTop;\nextern const Font kFontMain;\n}  // namespace volco\n")
    c.append("}  // namespace volco\n")
    (RENDER / "volco_fonts.h").write_text("".join(h))
    (RENDER / "volco_fonts.cpp").write_text("".join(c))

def write_csharp(rows):
    lines = ["// <auto-generated>",
             "// Generated by firmware/tools/gen_assets.py from firmware/assets/icons/manifest.txt. Do not edit.",
             "// </auto-generated>", "using System.Text.Json.Serialization;", "",
             "namespace Volco.Core.Communication;", "",
             "/// <summary>The firmware's closed icon set (spec §4.5). IDs are stable: append, never insert.</summary>",
             "[JsonConverter(typeof(JsonStringEnumConverter))]", "public enum ScreenIcon : byte", "{"]
    lines += [f"    {cs} = {id_}," for id_, _, cs, _ in rows]
    lines += ["}", ""]
    (ROOT / "src" / "Volco.Core" / "Communication" / "ScreenIcon.g.cs").write_text("\n".join(lines))

def write_simulator(rows):
    js = ["// Generated by firmware/tools/gen_assets.py. Do not edit.", "window.VOLCO_ICONS = ["]
    for id_, stem, _, _ in rows:
        js.append(f"  [ // {id_} {stem}")
        js += [f'    "{"".join(map(str, r))}",' for r in icon_rows(stem)]
        js.append("  ],")
    js += ["];", ""]
    (ROOT / "tools" / "simulator" / "icons.js").write_text("\n".join(js))

if __name__ == "__main__":
    RENDER.mkdir(parents=True, exist_ok=True)
    rows = manifest()
    write_icons(rows); write_fonts(); write_csharp(rows); write_simulator(rows)
    print("assets generated")
```

- [ ] **Step 4: Run the generator and check its outputs.**

```bash
python3 firmware/tools/gen_assets.py
/usr/bin/git diff --stat src/Volco.Core/Communication/ScreenIcon.g.cs   # expected: no change
grep -c "0x05D" firmware/lib/volco_render/src/volco_fonts.cpp            # Hebrew present: >= 2
grep -n "kIconCount = 11" firmware/lib/volco_render/src/volco_icons.h
```

If `ScreenIcon.g.cs` differs, make the two match (whitespace only) and re-run until the diff is empty, so `ScreenIconManifestTests` stays green.

- [ ] **Step 5: Commit.** No CI run is needed here, because nothing builds these files yet; Task 14 compiles and tests them.

```bash
/usr/bin/git add firmware/tools firmware/assets firmware/lib/volco_render/src tools/simulator/icons.js src/Volco.Core/Communication/ScreenIcon.g.cs
/usr/bin/git commit -F <msgfile>    # "feat(firmware): screen icons, Arimo fonts and the asset generator"
```

---

### Task 9: App screen preview control

**Files:**
- Modify: `src/Volco.App/Volco.App.csproj` (link the icon PNGs and fonts as Avalonia resources)
- Create: `src/Volco.App/Controls/ScreenIcons.cs` (bitmap cache)
- Create: `src/Volco.App/Controls/ScreenPreview.axaml`, `ScreenPreview.axaml.cs`
- Create: `src/Volco.App/Converters/ScreenIconToBitmapConverter.cs`
- Test: `tests/Volco.App.Tests/Controls/ScreenPreviewTests.cs`

**Interfaces**
- **Consumes:** `ScreenContent`, `ScreenIcon` (Task 1); the PNGs and fonts (Task 8).
- **Produces:**
  - `ScreenPreview` control with styled properties `Screen` (`ScreenContent?`) and `Scale` (`double`, default 1)
  - named parts: `TopText`, `TopDots`, `IconImage`, `Slash`, `MainText`, `Scaler`
  - `ScreenIcons.Bitmap(ScreenIcon) → Bitmap`
  - `ScreenIconToBitmapConverter` (XAML key `ScreenIconToBitmap`, registered in `VolcoApplication.axaml` resources)

- [ ] **Step 1: Write the failing test** `tests/Volco.App.Tests/Controls/ScreenPreviewTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Volco.App.Controls;
using Volco.Core.Communication;
using Xunit;

namespace Volco.App.Tests.Controls;

public class ScreenPreviewTests
{
    private static ScreenPreview Show(ScreenContent? content, double scale = 1)
    {
        var preview = new ScreenPreview { Screen = content, Scale = scale };
        new Window { Content = preview, Width = 400, Height = 300 }.Show();
        Dispatcher.UIThread.RunJobs();
        return preview;
    }

    private static T Part<T>(ScreenPreview p, string name) where T : Control => p.FindControl<T>(name)!;

    [AvaloniaFact]
    public void TextTop_IconWithSlash_AndMain()
    {
        var p = Show(ScreenContent.Text("MUTED", ScreenIcon.Mic, muted: true, "Microphone"));
        Assert.Equal("MUTED", Part<TextBlock>(p, "TopText").Text);
        Assert.True(Part<TextBlock>(p, "TopText").IsVisible);
        Assert.False(Part<ItemsControl>(p, "TopDots").IsVisible);
        Assert.NotNull(Part<Image>(p, "IconImage").Source);
        Assert.True(Part<Panel>(p, "Slash").IsVisible);
        Assert.Equal("Microphone", Part<TextBlock>(p, "MainText").Text);
    }

    [AvaloniaFact]
    public void Dots_OneFilledPerSelectedPreset()
    {
        var p = Show(ScreenContent.Dots(1, 3, ScreenIcon.Presets, "Gaming"));
        var dots = Part<ItemsControl>(p, "TopDots");
        Assert.True(dots.IsVisible);
        Assert.Equal(new[] { false, true, false }, dots.ItemsSource!.Cast<bool>());
        Assert.False(Part<Panel>(p, "Slash").IsVisible);
    }

    [AvaloniaFact]
    public void Blank_HidesTheIcon_AndEmptyTexts()
    {
        var p = Show(ScreenContent.Blank);
        Assert.False(Part<Image>(p, "IconImage").IsVisible);
        Assert.Equal("", Part<TextBlock>(p, "MainText").Text);
        Assert.False(Part<TextBlock>(p, "TopText").IsVisible);
    }

    [AvaloniaFact]
    public void Scale_IsALayoutTransform()
    {
        var p = Show(ScreenContent.Blank, scale: 2);
        var scale = Assert.IsType<ScaleTransform>(Part<LayoutTransformControl>(p, "Scaler").LayoutTransform);
        Assert.Equal(2, scale.ScaleX);
    }

    [AvaloniaFact]
    public void EveryIcon_LoadsItsBitmap()
    {
        foreach (var icon in Enum.GetValues<ScreenIcon>())
            Assert.NotNull(ScreenIcons.Bitmap(icon));
    }
}
```

- [ ] **Step 2: Implement.** In `Volco.App.csproj`, add to the `AvaloniaResource` item group:

```xml
    <!-- The controller's own icons and font (firmware/assets is their single source), for the
         screen previews. -->
    <AvaloniaResource Include="..\..\firmware\assets\icons\*.png" Link="Assets\Icons\%(Filename)%(Extension)" />
    <AvaloniaResource Include="..\..\firmware\assets\fonts\*.ttf" Link="Assets\Fonts\%(Filename)%(Extension)" />
```

`src/Volco.App/Controls/ScreenIcons.cs`:

```csharp
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Volco.Core.Communication;

namespace Volco.App.Controls;

/// <summary>The controller's icon PNGs (linked from firmware/assets/icons) and screen font.</summary>
internal static class ScreenIcons
{
    private static readonly Dictionary<ScreenIcon, Bitmap> Cache = new();

    /// <summary>Arimo, the face the firmware's fonts are generated from.</summary>
    public static FontFamily Font { get; } = new("avares://Volco.App/Assets/Fonts#Arimo");

    public static Bitmap Bitmap(ScreenIcon icon)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(icon, out var bitmap))
            {
                // File stems are the lower-case enum names (manifest.txt: StudioMic -> studiomic).
                var uri = new Uri($"avares://Volco.App/Assets/Icons/{icon.ToString().ToLowerInvariant()}.png");
                Cache[icon] = bitmap = new Bitmap(AssetLoader.Open(uri));
            }
            return bitmap;
        }
    }
}
```

`src/Volco.App/Converters/ScreenIconToBitmapConverter.cs`:

```csharp
using System.Globalization;
using Avalonia.Data.Converters;
using Volco.App.Controls;
using Volco.Core.Communication;

namespace Volco.App.Converters;

/// <summary>ScreenIcon → its 28×28 bitmap (for icon tiles and the icon picker).</summary>
public sealed class ScreenIconToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ScreenIcon icon ? ScreenIcons.Bitmap(icon) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Register it in `VolcoApplication.axaml`'s `<Application.Resources>`, as `<conv:ScreenIconToBitmapConverter x:Key="ScreenIconToBitmap"/>` with `xmlns:conv="using:Volco.App.Converters"`. Look at how other app-wide resources are declared there and follow that.

`src/Volco.App/Controls/ScreenPreview.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:sys="using:System"
             xmlns:local="using:Volco.App.Controls"
             x:Class="Volco.App.Controls.ScreenPreview">
  <!-- Layout 1 ("C nudged", spec §2.1) at the panel's own 128×64 pixels, scaled as a whole.
       Close to the firmware's rendering, not pixel-identical. -->
  <Border Background="#05070A" CornerRadius="3" Padding="4" HorizontalAlignment="Left">
    <LayoutTransformControl Name="Scaler">
      <Canvas Width="128" Height="64" ClipToBounds="True">
        <TextBlock Name="TopText" Canvas.Left="2" Canvas.Top="0" Width="124" TextAlignment="Center"
                   FontSize="10" Foreground="#E8F0FF" TextWrapping="NoWrap"/>
        <ItemsControl Name="TopDots" Canvas.Top="5" Canvas.Left="0" Width="128">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
              <StackPanel Orientation="Horizontal" Spacing="4" HorizontalAlignment="Center"/>
            </ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="sys:Boolean">
              <Border Width="6" Height="6" BorderBrush="#E8F0FF" BorderThickness="1"
                      Background="{Binding Converter={x:Static local:ScreenDotFill.Instance}}"/>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
        <Image Name="IconImage" Canvas.Left="50" Canvas.Top="18" Width="28" Height="28"
               RenderOptions.BitmapInterpolationMode="None"/>
        <Panel Name="Slash" Canvas.Left="50" Canvas.Top="18" Width="28" Height="28">
          <Line StartPoint="2,2" EndPoint="26,26" Stroke="#05070A" StrokeThickness="5.5"/>
          <Line StartPoint="2,2" EndPoint="26,26" Stroke="#E8F0FF" StrokeThickness="2.3"/>
        </Panel>
        <TextBlock Name="MainText" Canvas.Left="2" Canvas.Top="45" Width="124" TextAlignment="Center"
                   FontSize="14" FontWeight="Bold" Foreground="#E8F0FF" TextWrapping="NoWrap"
                   TextTrimming="None" ClipToBounds="True"/>
      </Canvas>
    </LayoutTransformControl>
  </Border>
</UserControl>
```

`src/Volco.App/Controls/ScreenPreview.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Volco.Core.Communication;

namespace Volco.App.Controls;

/// <summary>Fills a preset dot: lit for the selected preset.</summary>
public static class ScreenDotFill
{
    public static readonly IValueConverter Instance = new FuncValueConverter<bool, IBrush>(
        on => on ? new SolidColorBrush(Color.Parse("#E8F0FF")) : Brushes.Transparent);
}

/// <summary>What one controller screen shows (spec §6.3): a preview of layout 1.</summary>
public partial class ScreenPreview : UserControl
{
    public static readonly StyledProperty<ScreenContent?> ScreenProperty =
        AvaloniaProperty.Register<ScreenPreview, ScreenContent?>(nameof(Screen));

    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ScreenPreview, double>(nameof(Scale), 1.0);

    private readonly TextBlock _top, _main;
    private readonly ItemsControl _dots;
    private readonly Image _icon;
    private readonly Panel _slash;
    private readonly LayoutTransformControl _scaler;

    public ScreenPreview()
    {
        AvaloniaXamlLoader.Load(this);
        _top = this.FindControl<TextBlock>("TopText")!;
        _main = this.FindControl<TextBlock>("MainText")!;
        _dots = this.FindControl<ItemsControl>("TopDots")!;
        _icon = this.FindControl<Image>("IconImage")!;
        _slash = this.FindControl<Panel>("Slash")!;
        _scaler = this.FindControl<LayoutTransformControl>("Scaler")!;
        _top.FontFamily = _main.FontFamily = ScreenIcons.Font;
        Apply();
    }

    public ScreenContent? Screen
    {
        get => GetValue(ScreenProperty);
        set => SetValue(ScreenProperty, value);
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScreenProperty || change.Property == ScaleProperty) Apply();
    }

    private void Apply()
    {
        if (_scaler is null) return;   // during XAML load
        _scaler.LayoutTransform = new ScaleTransform(Scale, Scale);
        var c = Screen ?? ScreenContent.Blank;
        _top.Text = c.TopText;
        _top.IsVisible = c.TopKind == TopKind.Text;
        _dots.IsVisible = c.TopKind == TopKind.Dots;
        _dots.ItemsSource = Enumerable.Range(0, c.DotsCount).Select(i => i == c.DotsSelected).ToList();
        _icon.IsVisible = c.Icon is not null;
        _icon.Source = c.Icon is { } icon ? ScreenIcons.Bitmap(icon) : null;
        _slash.IsVisible = c.Icon is not null && c.Muted;
        _main.Text = c.Main;
    }
}
```

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(app): screen preview control with the controller's icons and font`.

---

### Task 10: Devices page

**Files:**
- Create: `src/Volco.App/ViewModels/DevicesViewModel.cs`, `DeviceItemViewModel.cs`
- Create: `src/Volco.App/Views/Pages/DevicesPage.axaml`, `DevicesPage.axaml.cs`
- Modify: `src/Volco.App/ViewModels/SettingsViewModel.cs` (`Devices` property; refresh on device changes)
- Modify: `src/Volco.App/Views/SettingsWindow.axaml.cs` (nav entry between Buttons and Connection)
- Test: `tests/Volco.App.Tests/Pages/DevicesPageTests.cs`; extend `tests/Volco.App.Tests/SettingsCoverageTests.cs`

**Interfaces**
- **Consumes:** `DeviceDisplay` (Task 4), `ScreenPreview` and `ScreenIconToBitmap` (Task 9), `DeviceSettingsConfig` (Task 2).
- **Produces:**
  - `DevicesViewModel(ConfigManager, IAudioEngine)`, with `Outputs`, `Inputs` and `Refresh()`
  - `DeviceItemViewModel` with:
    - identity: `Id`, `Flow`, `WindowsName`, `AutoName`, `IsConnected`, `IsDefault`
    - editable: `Nickname`, `Icon`, `IconChoices`
    - derived: `DisplayName`, `IsAutoName`, `StatusText`, `NicknameHint`, `NicknameFlowDirection`, `Preview`, `UsedBy`, `CanForget`, `FooterText`
    - commands: `ToggleExpandedCommand`, `ForgetCommand`; state `IsExpanded`
  - `IconChoiceViewModel` (`Icon`, `Label`, `IsSelected`, `SelectCommand`)
  - Named controls: `DeviceCard`, `DeviceTitle`, `DeviceStatus`, `NicknameBox`, `IconChoice`, `DevicePreview`, `ForgetButton`, `UsedByText`

- [ ] **Step 1: Write the failing tests** `tests/Volco.App.Tests/Pages/DevicesPageTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

public class DevicesPageTests
{
    private static FakeAudioEngine Engine()
    {
        var speakers = new AudioDevice("spk", "Speakers (Realtek High Definition Audio)", AudioFlow.Render, true, DeviceFormFactor.Speakers);
        var mic = new AudioDevice("mic", "Microphone (Yeti Stereo Microphone)", AudioFlow.Capture, true, DeviceFormFactor.Microphone);
        return new FakeAudioEngine
        {
            Devices = { speakers, new AudioDevice("hs", "Headset Earphone (Arctis Nova 7)", AudioFlow.Render, false, DeviceFormFactor.Headset), mic },
            DefaultRender = speakers, DefaultCapture = mic,
        };
    }

    private static (DevicesPage Page, TestRig Rig, SettingsViewModel Vm) Show(Action<AppConfig>? configure = null)
    {
        var rig = new TestRig(Engine());
        configure?.Invoke(rig.Config.Config);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new DevicesPage(vm.Devices);
        new Window { Content = page, Width = 900, Height = 1000 }.Show();
        Dispatcher.UIThread.RunJobs();
        return (page, rig, vm);
    }

    private static List<T> All<T>(Control root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

    [AvaloniaFact]
    public void ListsEnabledDevices_DefaultFirst_AndGroupsByFlow()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Assert.Equal(new[] { "Speakers", "Headset Earphone" }, vm.Devices.Outputs.Select(d => d.DisplayName));
        Assert.Equal(new[] { "Microphone" }, vm.Devices.Inputs.Select(d => d.DisplayName));
        Assert.Equal("Default output", vm.Devices.Outputs[0].StatusText);
        Assert.Equal(3, All<Border>(page, "DeviceCard").Count);
    }

    [AvaloniaFact]
    public void DisconnectedDevices_AppearWhenCustomizedOrReferenced()
    {
        var (_, rig, vm) = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "sony", FriendlyName = "Headphones (WH-1000XM4)", Nickname = "Sony" });
            c.Sliders[0].Type = SliderMappingType.Device;
            c.Sliders[0].Device = new DeviceRefConfig { Id = "tv", FriendlyName = "LG TV (NVIDIA)" };
        });
        using var _ = rig; using var __ = vm;
        var sony = vm.Devices.Outputs.Single(d => d.Id == "sony");
        var tv = vm.Devices.Outputs.Single(d => d.Id == "tv");
        Assert.Equal(("Sony", "Not connected", true), (sony.DisplayName, sony.StatusText, sony.CanForget));
        Assert.Equal(("LG TV", false, "Used by Slider 0"), (tv.DisplayName, tv.CanForget, tv.UsedBy));
    }

    [AvaloniaFact]
    public void Nickname_SavesAndUpdatesTitleAndPreview()
    {
        var (_, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var spk = vm.Devices.Outputs[0];

        spk.Nickname = "Desk speakers";

        var entry = rig.Config.Config.FindDevice("spk")!;
        Assert.Equal("Desk speakers", entry.Nickname);
        Assert.Equal("Speakers (Realtek High Definition Audio)", entry.FriendlyName);
        Assert.Equal("Desk speakers", spk.DisplayName);
        Assert.Equal("Desk speakers", spk.Preview.Main);
        Assert.Equal("ACTIVE", spk.Preview.TopText);
    }

    [AvaloniaFact]
    public void Icon_PickedFromTheTen_IsStored_ClearingBothRemovesTheEntry()
    {
        var (_, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var spk = vm.Devices.Outputs[0];
        Assert.Equal(10, spk.IconChoices.Count);
        Assert.DoesNotContain(spk.IconChoices, c => c.Icon == ScreenIcon.Presets);
        Assert.Equal(ScreenIcon.Speaker, spk.Icon);                       // automatic, from the form factor

        spk.IconChoices.Single(c => c.Icon == ScreenIcon.Speakers).SelectCommand.Execute(null);
        Assert.Equal(ScreenIcon.Speakers, rig.Config.Config.FindDevice("spk")!.Icon);
        Assert.True(spk.IconChoices.Single(c => c.Icon == ScreenIcon.Speakers).IsSelected);

        spk.Nickname = "x";
        spk.Nickname = "";
        spk.ResetIconCommand.Execute(null);
        Assert.Null(rig.Config.Config.FindDevice("spk"));
    }

    [AvaloniaFact]
    public void Forget_RemovesADisconnectedDevice()
    {
        var (_, rig, vm) = Show(c => c.Devices.Add(new DeviceSettingsConfig { Id = "sony", FriendlyName = "Sony", Nickname = "S" }));
        using var _ = rig; using var __ = vm;
        vm.Devices.Outputs.Single(d => d.Id == "sony").ForgetCommand.Execute(null);
        Assert.Null(rig.Config.Config.FindDevice("sony"));
        Assert.DoesNotContain(vm.Devices.Outputs, d => d.Id == "sony");
    }

    [AvaloniaFact]
    public void HebrewNickname_TypesRightToLeft()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var spk = vm.Devices.Outputs[0];
        spk.IsExpanded = true;
        spk.Nickname = "רמקולים";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FlowDirection.RightToLeft, All<TextBox>(page, "NicknameBox")[0].FlowDirection);
    }

    [AvaloniaFact]
    public void DeviceChanges_RefreshTheLists()
    {
        var (_, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        var dock = new AudioDevice("dock", "Dock Speakers", AudioFlow.Render, false);
        rig.Engine.Devices.Add(dock);
        rig.Engine.RaiseDeviceChanged(DeviceChangeKind.Added, dock);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.Devices.Outputs, d => d.Id == "dock");
    }
}
```

Add to `SettingsCoverageTests.cs`:

```csharp
    [AvaloniaFact]
    public void Devices_NicknameAndIcon()
    {
        using var rig = new TestRig(Engine());
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new DevicesPage(vm.Devices);
        Host(page);
        var item = vm.Devices.Outputs.Single(d => d.Id == "dev-1");
        item.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        var card = page.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DeviceCard" && b.DataContext == item);

        Named<TextBox>(card, "NicknameBox").Text = "Desk";                                      // Devices[].Nickname
        Named<Button>(card, "IconChoice", (int)ScreenIcon.Headphones).Command!.Execute(null);   // Devices[].Icon

        var d = SaveAndReload(rig).FindDevice("dev-1")!;
        Assert.Equal("Desk", d.Nickname);
        Assert.Equal(ScreenIcon.Headphones, d.Icon);
    }
```

The test needs `using Volco.Core.Communication;`. The icon buttons inside one card are in manifest order, so index `(int)ScreenIcon.Headphones` is that card's Headphones button. The view model also gets a `ResetIconCommand` (back to automatic), used by `DevicesPageTests`.

- [ ] **Step 2: Implement** `src/Volco.App/ViewModels/DevicesViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Devices;

namespace Volco.App.ViewModels;

/// <summary>
/// The Devices page (spec §6.1): every enabled device, plus disconnected ones that have settings
/// or are used by a slider or button action, in Outputs and Inputs; default device first, then by
/// display name. Nicknames and icons save as they are edited.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;

    public ObservableCollection<DeviceItemViewModel> Outputs { get; } = new();
    public ObservableCollection<DeviceItemViewModel> Inputs { get; } = new();

    public DevicesViewModel(ConfigManager config, IAudioEngine audio)
    {
        _config = config;
        _audio = audio;
        Refresh();
    }

    internal AppConfig Config => _config.Config;

    internal void Save() => _config.RequestSave();

    /// <summary>Rebuilds both lists (device plugged in or out, default changed); keeps open cards open.</summary>
    public void Refresh()
    {
        var open = Outputs.Concat(Inputs).Where(i => i.IsExpanded).Select(i => i.Id)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Fill(Outputs, AudioFlow.Render, open);
        Fill(Inputs, AudioFlow.Capture, open);
    }

    private void Fill(ObservableCollection<DeviceItemViewModel> list, AudioFlow flow, HashSet<string> open)
    {
        var live = _audio.GetDevices(flow);
        var defaultId = _audio.GetDefaultDevice(flow)?.Id;
        var items = new List<DeviceItemViewModel>();
        bool Known(string id) => items.Exists(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

        foreach (var d in live)
            items.Add(new DeviceItemViewModel(this, d.Id, flow, d.FriendlyName, DeviceDisplay.IconFor(d.FormFactor),
                isConnected: true, isDefault: string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase)));
        foreach (var s in Config.Devices.Where(s => s.Flow == flow && !Known(s.Id)))
            items.Add(new DeviceItemViewModel(this, s.Id, flow, s.FriendlyName, ScreenIconDefault, false, false));
        foreach (var r in References(flow).Where(r => !string.IsNullOrEmpty(r.Id) && !Known(r.Id)))
            items.Add(new DeviceItemViewModel(this, r.Id, flow, r.FriendlyName, ScreenIconDefault, false, false));

        list.Clear();
        foreach (var item in items.OrderByDescending(i => i.IsDefault)
                                  .ThenBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            item.IsExpanded = open.Contains(item.Id);
            list.Add(item);
        }
    }

    private const Volco.Core.Communication.ScreenIcon ScreenIconDefault = Volco.Core.Communication.ScreenIcon.Generic;

    /// <summary>Device references in sliders and button actions (all are outputs today).</summary>
    private IEnumerable<DeviceRefConfig> References(AudioFlow flow)
    {
        if (flow != AudioFlow.Render) yield break;
        foreach (var s in Config.Sliders)
            if (s.Device is { } d) yield return d;
        foreach (var a in Config.Buttons.SelectMany(b => b.Actions.Values).Where(b => b is not null).SelectMany(b => b.All()))
        {
            if (a.Device is { } d1) yield return d1;
            if (a.DeviceB is { } d2) yield return d2;
        }
    }

    /// <summary>"Used by Slider 0, Button 2" (numbers as on the cards), or "".</summary>
    internal string UsedBy(string id)
    {
        bool Is(DeviceRefConfig? r) => r is not null && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase);
        var users = new List<string>();
        for (int i = 0; i < Config.Sliders.Count; i++)
            if (Is(Config.Sliders[i].Device)) users.Add($"Slider {i}");
        for (int i = 0; i < Config.Buttons.Count; i++)
            if (Config.Buttons[i].Actions.Values.Where(b => b is not null).SelectMany(b => b.All()).Any(a => Is(a.Device) || Is(a.DeviceB)))
                users.Add($"Button {i}");
        return users.Count == 0 ? "" : "Used by " + string.Join(", ", users);
    }

    internal void Forget(DeviceItemViewModel item)
    {
        Config.Devices.RemoveAll(d => string.Equals(d.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        Save();
        Refresh();
    }
}
```

`src/Volco.App/ViewModels/DeviceItemViewModel.cs`:

```csharp
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Devices;

namespace Volco.App.ViewModels;

/// <summary>One device card on the Devices page.</summary>
public sealed partial class DeviceItemViewModel : ObservableObject
{
    private readonly DevicesViewModel _owner;
    private readonly ScreenIcon _automaticIcon;
    private bool _loading;

    public string Id { get; }
    public AudioFlow Flow { get; }
    public string WindowsName { get; }
    public string AutoName { get; }
    public bool IsConnected { get; }
    public bool IsDefault { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(IsAutoName), nameof(Preview), nameof(NicknameFlowDirection))]
    private string _nickname = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private ScreenIcon _icon;

    [ObservableProperty]
    private bool _isExpanded;

    public IReadOnlyList<IconChoiceViewModel> IconChoices { get; }

    internal DeviceItemViewModel(DevicesViewModel owner, string id, AudioFlow flow, string windowsName,
                                 ScreenIcon automaticIcon, bool isConnected, bool isDefault)
    {
        _owner = owner;
        Id = id;
        Flow = flow;
        WindowsName = windowsName;
        AutoName = DeviceDisplay.AutoShortName(windowsName);
        IsConnected = isConnected;
        IsDefault = isDefault;
        _automaticIcon = automaticIcon;

        var settings = owner.Config.FindDevice(id);
        _loading = true;
        Nickname = settings?.Nickname ?? "";
        Icon = settings?.Icon ?? automaticIcon;
        _loading = false;

        IconChoices = Enum.GetValues<ScreenIcon>().Where(i => i != ScreenIcon.Presets)
            .Select(i => new IconChoiceViewModel(i, choice => Icon = choice)).ToList();
        UpdateIconSelection();
    }

    public string DisplayName => Nickname.Trim().Length > 0 ? Nickname.Trim() : AutoName;
    public bool IsAutoName => Nickname.Trim().Length == 0;
    public string NicknameHint => $"Leave empty to use “{AutoName}”.";

    /// <summary>A Hebrew (or other right-to-left) nickname is typed right to left (spec §6.1).</summary>
    public FlowDirection NicknameFlowDirection
        => Nickname.FirstOrDefault(char.IsLetter) is var c && c >= '֐' && c <= 'ࣿ'
            ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string StatusText => IsDefault ? (Flow == AudioFlow.Render ? "Default output" : "Default input")
                              : !IsConnected ? "Not connected" : "";
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>The device as a "Switch output" button's screen would show it.</summary>
    public ScreenContent Preview
        => ScreenContent.Text(IsDefault && Flow == AudioFlow.Render ? ControllerDisplay.Active : "", Icon, false, DisplayName);

    public string UsedBy => _owner.UsedBy(Id);
    public bool CanForget => !IsConnected && UsedBy.Length == 0;
    public bool ShowUsedBy => !IsConnected && UsedBy.Length > 0;
    public string FooterText => $"Windows name: {WindowsName} · {(Flow == AudioFlow.Render ? "Output" : "Input")}";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Forget() => _owner.Forget(this);

    /// <summary>Back to the automatic icon.</summary>
    [RelayCommand]
    private void ResetIcon()
    {
        Icon = _automaticIcon;
        Store(iconChosen: false);
    }

    partial void OnNicknameChanged(string value)
    {
        if (!_loading) Store(iconChosen: _owner.Config.FindDevice(Id)?.Icon is not null);
    }

    partial void OnIconChanged(ScreenIcon value)
    {
        UpdateIconSelection();
        if (!_loading) Store(iconChosen: true);
    }

    private void UpdateIconSelection()
    {
        if (IconChoices is null) return;
        foreach (var c in IconChoices) c.IsSelected = c.Icon == Icon;
    }

    /// <summary>Writes this device's entry; removes it once it holds nothing (spec §5.1).</summary>
    private void Store(bool iconChosen)
    {
        var devices = _owner.Config.Devices;
        var entry = _owner.Config.FindDevice(Id);
        if (entry is null)
        {
            entry = new DeviceSettingsConfig { Id = Id, Flow = Flow, FriendlyName = WindowsName };
            devices.Add(entry);
        }
        entry.Nickname = Nickname.Trim();
        entry.Icon = iconChosen ? Icon : null;
        if (entry.IsEmpty) devices.Remove(entry);
        _owner.Save();
    }
}

/// <summary>One icon in a device's icon picker.</summary>
public sealed partial class IconChoiceViewModel : ObservableObject
{
    private readonly Action<ScreenIcon> _select;

    public IconChoiceViewModel(ScreenIcon icon, Action<ScreenIcon> select)
    {
        Icon = icon;
        _select = select;
    }

    public ScreenIcon Icon { get; }
    public string Label => Icon switch
    {
        ScreenIcon.Speakers => "Speaker pair",
        ScreenIcon.Display => "Monitor / TV",
        ScreenIcon.Mic => "Microphone",
        ScreenIcon.StudioMic => "Studio mic",
        _ => Icon.ToString(),
    };

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => _select(Icon);
}
```

`src/Volco.App/Views/Pages/DevicesPage.axaml`. Follow `ButtonsPage.axaml`'s card structure and classes (`Card`, `CardHeader`, `CardBody`, `Title`, `Secondary`, `Caption`):

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Volco.App.ViewModels"
             xmlns:controls="using:Volco.App.Controls"
             x:Class="Volco.App.Views.Pages.DevicesPage"
             x:DataType="vm:DevicesViewModel">
  <UserControl.Resources>
    <DataTemplate x:Key="DeviceCardTemplate" x:DataType="vm:DeviceItemViewModel">
      <Border Name="DeviceCard" Classes="Card">
        <StackPanel>
          <Button Name="DeviceHeader" Classes="CardHeader" Margin="4" Command="{Binding ToggleExpandedCommand}"
                  HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
            <Grid ColumnDefinitions="Auto,*,Auto,Auto">
              <Border Width="40" Height="40" CornerRadius="6" Background="#05070A" Margin="0,0,12,0">
                <Image Width="28" Height="28" RenderOptions.BitmapInterpolationMode="None"
                       Source="{Binding Icon, Converter={StaticResource ScreenIconToBitmap}}"/>
              </Border>
              <StackPanel Grid.Column="1" VerticalAlignment="Center">
                <TextBlock Name="DeviceTitle" Classes="Subtitle" Text="{Binding DisplayName}"
                           Classes.auto="{Binding IsAutoName}" TextTrimming="CharacterEllipsis"/>
                <TextBlock Classes="Caption" Text="{Binding WindowsName}" TextTrimming="CharacterEllipsis"/>
              </StackPanel>
              <Border Grid.Column="2" Classes="Chip" IsVisible="{Binding HasStatus}" VerticalAlignment="Center" Margin="8,0">
                <TextBlock Name="DeviceStatus" Classes="Caption" Text="{Binding StatusText}"/>
              </Border>
              <PathIcon Grid.Column="3" Width="12" Height="12" VerticalAlignment="Center"
                        Data="{StaticResource Icon.ChevronDown}"/>
            </Grid>
          </Button>
          <Border Classes="CardBody" IsVisible="{Binding IsExpanded}">
            <Grid ColumnDefinitions="*,Auto" RowDefinitions="Auto,Auto">
              <StackPanel Spacing="16">
                <StackPanel Spacing="6">
                  <TextBlock Text="Nickname" FontWeight="SemiBold"/>
                  <TextBox Name="NicknameBox" MaxWidth="320" HorizontalAlignment="Left"
                           Watermark="{Binding AutoName}" Text="{Binding Nickname, Mode=TwoWay}"
                           FlowDirection="{Binding NicknameFlowDirection}"/>
                  <TextBlock Classes="Caption" Text="{Binding NicknameHint}"/>
                </StackPanel>
                <StackPanel Spacing="6">
                  <TextBlock Text="Icon" FontWeight="SemiBold"/>
                  <ItemsControl ItemsSource="{Binding IconChoices}">
                    <ItemsControl.ItemsPanel>
                      <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                    <ItemsControl.ItemTemplate>
                      <DataTemplate x:DataType="vm:IconChoiceViewModel">
                        <Button Name="IconChoice" Classes="IconChoice" Classes.selected="{Binding IsSelected}"
                                Width="44" Height="44" Margin="0,0,6,6" Padding="0" Background="#05070A"
                                ToolTip.Tip="{Binding Label}" Command="{Binding SelectCommand}">
                          <Image Width="28" Height="28" RenderOptions.BitmapInterpolationMode="None"
                                 Source="{Binding Icon, Converter={StaticResource ScreenIconToBitmap}}"/>
                        </Button>
                      </DataTemplate>
                    </ItemsControl.ItemTemplate>
                  </ItemsControl>
                </StackPanel>
              </StackPanel>
              <controls:ScreenPreview Name="DevicePreview" Grid.Column="1" Margin="16,0,0,0" Scale="2"
                                      VerticalAlignment="Top" Screen="{Binding Preview}"/>
              <StackPanel Grid.Row="1" Grid.ColumnSpan="2" Orientation="Horizontal" Spacing="16" Margin="0,12,0,0">
                <TextBlock Classes="Caption" Text="{Binding FooterText}"/>
                <Button Name="ForgetButton" Classes="Ghost Danger" Content="Forget this device"
                        IsVisible="{Binding CanForget}" Command="{Binding ForgetCommand}"/>
                <TextBlock Name="UsedByText" Classes="Caption" Text="{Binding UsedBy}" IsVisible="{Binding ShowUsedBy}"/>
              </StackPanel>
            </Grid>
          </Border>
        </StackPanel>
      </Border>
    </DataTemplate>
  </UserControl.Resources>

  <ScrollViewer VerticalScrollBarVisibility="Auto">
    <StackPanel Margin="{StaticResource PagePadding}" Spacing="{StaticResource SpaceL}" MaxWidth="{StaticResource PageMaxWidth}">
      <StackPanel Spacing="2" Margin="0,8,0,0">
        <TextBlock Text="Devices" Classes="Title"/>
        <TextBlock Classes="Secondary" TextWrapping="Wrap"
                   Text="Give your audio devices short names and icons. Volco uses them on your controller's screens, in the volume pop-up and in its own device lists."/>
      </StackPanel>
      <StackPanel Spacing="8">
        <TextBlock Text="OUTPUTS" Classes="Caption" FontWeight="SemiBold"/>
        <ItemsControl Name="OutputList" ItemsSource="{Binding Outputs}" ItemTemplate="{StaticResource DeviceCardTemplate}">
          <ItemsControl.ItemsPanel><ItemsPanelTemplate><StackPanel Spacing="8"/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
        </ItemsControl>
      </StackPanel>
      <StackPanel Spacing="8">
        <TextBlock Text="INPUTS" Classes="Caption" FontWeight="SemiBold"/>
        <ItemsControl Name="InputList" ItemsSource="{Binding Inputs}" ItemTemplate="{StaticResource DeviceCardTemplate}">
          <ItemsControl.ItemsPanel><ItemsPanelTemplate><StackPanel Spacing="8"/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
        </ItemsControl>
      </StackPanel>
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

Where a referenced class doesn't exist in `Styles/VolcoStyles.axaml` (e.g. `Chip`, `IconChoice`, `selected`, `auto`), add it there:
- `Border.Chip`: 1 px `BorderBrush`, 10 corner radius, 8,1 padding;
- `Button.IconChoice.selected`: 2 px `AccentBrush` border;
- `TextBlock.auto`: `TextSecondaryBrush`, normal weight.

`DevicesPage.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

/// <summary>Device nicknames and icons (spec §6.1). Bindings do all the work.</summary>
public partial class DevicesPage : UserControl
{
    public DevicesPage() => AvaloniaXamlLoader.Load(this);

    public DevicesPage(DevicesViewModel viewModel) : this() => DataContext = viewModel;
}
```

In `SettingsViewModel.cs`:
- add `private DevicesViewModel? _devicesViewModel;`;
- add `public DevicesViewModel Devices => _devicesViewModel ??= new DevicesViewModel(_config, _audio);`;
- in `OnDevicesChanged`'s posted lambda, add `_devicesViewModel?.Refresh();`.

In `SettingsWindow.axaml.cs`, insert into the nav array between Buttons and Connection:

```csharp
            Nav("Devices", "devices", "Icon.Speaker", () => new DevicesPage(vm.Devices)),
```

Update `ShowPage`'s doc comment to include "devices". Check `tests/Volco.App.Tests/Shell/SettingsWindowTests.cs` and `Pages/TabOrderTests.cs` for assertions on the nav item count or order, and add Devices there.

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(app): Devices page for nicknames and icons`.

---

### Task 11: Button cards follow the controller's hardware; count hints; Connection page status

**Files:**
- Modify: `src/Volco.App/ViewModels/ButtonItemViewModel.cs`, `ButtonsViewModel.cs`, `SlidersViewModel.cs`, `ActionCatalog.cs`, `ConnectionViewModel.cs`, `SettingsViewModel.cs`
- Create: `src/Volco.App/ViewModels/CountHints.cs`
- Modify: `src/Volco.App/Views/Pages/ButtonsPage.axaml`, `SlidersPage.axaml`, `ConnectionPage.axaml`
- Test: `tests/Volco.App.Tests/Pages/ButtonsPageTests.cs`, `SlidersPageTests.cs`, `ConnectionPageTests.cs`, `tests/Volco.App.Tests/ViewModels/CountHintsTests.cs`

**Interfaces**
- **Consumes:** `ControllerDisplay` (Task 5), `ControllerState`, `ControllerStateChanged` and `ControllerInfoChanged` (Task 7), `ScreenPreview` (Task 9).
- **Produces:**
  - `ButtonItemViewModel`:
    - `Indicator` (`IndicatorKind?`, null = unknown) and its flags `IsLed`, `IsLedPair`, `IsScreen`, `IsIndicatorUnknown`
    - text: `IndicatorText`
    - preview: `Screen` (`ScreenContent`, for the edited preset)
    - removed: `LightKind`, `PresetDots`, `IsOnOffLight`, `IsPairLight`, `IsPresetLight`, `HasNoLight`
  - `IButtonCardOwner.IndicatorFor(int)`
  - `ButtonsViewModel` and `SlidersViewModel` each get:
    - `CountHint`, `HasCountHint`, `CanAddMissing`, `AddMissingText`
    - `AddMissingCommand`
    - `RefreshController()`
  - `CountHints.Text(string noun, int? controllerCount, int cards)`
  - `ConnectionViewModel`: `HardwareSummary`, `HasHardwareSummary`, `ControllerError`, `HasControllerError`, and `FooterText` showing "Firmware mismatch"
  - Named controls:
    - card preview: `Led`, `LedA`, `LedB`, `ScreenPreviewSmall`
    - card body: `IndicatorText`, `ScreenPreviewLarge`
    - page header: `CountHintText`, `AddMissingButton`
    - Connection page: `HardwareSummaryText`, `ControllerErrorText`

- [ ] **Step 1: Write the failing tests.** `tests/Volco.App.Tests/ViewModels/CountHintsTests.cs`:

```csharp
using Volco.App.ViewModels;
using Xunit;

namespace Volco.App.Tests.ViewModels;

public class CountHintsTests
{
    [Theory]
    [InlineData(null, 3, "")]
    [InlineData(3, 3, "")]
    [InlineData(4, 3, "Your controller has 4 buttons; 3 are set up.")]
    [InlineData(2, 1, "Your controller has 2 buttons; 1 is set up.")]
    [InlineData(3, 5, "Your controller has 3 buttons; cards 3–4 do nothing.")]
    [InlineData(1, 2, "Your controller has 1 button; card 1 does nothing.")]
    public void Text(int? controller, int cards, string expected)
        => Assert.Equal(expected, CountHints.Text("button", controller, cards));
}
```

In `ButtonsPageTests.cs`:
- Replace `ButtonsPage_LightPreview_FollowsTheLiveState`. Configure `c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = 5, Buttons = { IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen } };` in the `Show(c => …)` callback, keep its body and asserts for `Led`, `LedA` and `LedB`, and add the new tests below.
- Its `LightDescription` assertion (line ~305) stays.

```csharp
    private static void Hardware(AppConfig c, params IndicatorKind[] kinds)
        => c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = 5, Buttons = kinds.ToList() };

    [AvaloniaFact]
    public void ButtonsPage_ScreenButton_ShowsAScreenPreviewOfTheEditedPreset()
    {
        var (page, rig, vm) = Show(c =>
        {
            Hardware(c, IndicatorKind.Screen, IndicatorKind.Led, IndicatorKind.None);
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteCurrentOutput;
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);

        var small = In<ScreenPreview>(Card(page, 0), "ScreenPreviewSmall");
        Assert.True(small.IsVisible);
        Assert.Equal("Speakers", small.Screen!.Main);
        Assert.True(In<ScreenPreview>(Card(page, 0), "ScreenPreviewLarge").IsVisible);
        Assert.Equal("Light: Screen", In<TextBlock>(Card(page, 0), "IndicatorText").Text);
        Assert.False(In<ScreenPreview>(Card(page, 1), "ScreenPreviewSmall").IsVisible);
        Assert.True(In<Ellipse>(Card(page, 1), "Led").IsVisible);
        Assert.Equal("Light: None", vm.Buttons.Items[2].IndicatorText);
    }

    [AvaloniaFact]
    public void ButtonsPage_NoControllerInfo_AsksToConnect_AndShowsNoPreview()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        Assert.Equal("Connect your controller to see its lights and screens.", In<TextBlock>(Card(page, 0), "IndicatorText").Text);
        Assert.False(In<Ellipse>(Card(page, 0), "Led").IsVisible);
        Assert.False(In<ScreenPreview>(Card(page, 0), "ScreenPreviewSmall").IsVisible);
    }

    [AvaloniaFact]
    public void ButtonsPage_CountHint_AddsTheMissingButtons()
    {
        var (page, rig, vm) = Show(c => Hardware(c, IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair, IndicatorKind.Screen));
        using var _ = rig; using var __ = vm;
        Assert.Equal("Your controller has 4 buttons; 3 are set up.", In<TextBlock>(page, "CountHintText").Text);
        In<Button>(page, "AddMissingButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, rig.Config.Config.Buttons.Count);
        Assert.False(vm.Buttons.HasCountHint);
    }

    [AvaloniaFact]
    public void ButtonsPage_ControllerInfoArrives_CardsFollow()
    {
        var (page, rig, vm) = Show();
        using var _ = rig; using var __ = vm;
        rig.Connection!.SetStatus(Volco.Core.Communication.ConnectionStatus.Connected);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5,
            new[] { IndicatorKind.Screen, IndicatorKind.Led, IndicatorKind.Led }));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Buttons.Items[0].IsScreen);
        Assert.True(In<ScreenPreview>(Card(page, 0), "ScreenPreviewSmall").IsVisible);
    }
```

These tests need `using Volco.Core.Communication;` and `using Volco.App.Controls;`.

In `SlidersPageTests.cs`, add:

```csharp
    [AvaloniaFact]
    public void SlidersPage_CountHint_WhenTheControllerHasFewerSliders()
    {
        var (page, _, rig, vm) = Show(c => c.Controller = new ControllerInfoConfig { ProtocolVersion = 1, Sliders = 3 });
        using var __ = rig; using var ___ = vm;
        Assert.Equal("Your controller has 3 sliders; cards 3–4 do nothing.", In<TextBlock>(page, "CountHintText").Text);
        Assert.False(In<Button>(page, "AddMissingButton").IsVisible);
    }
```

Match the file's own `Show` and `In` helper signatures; `Show` returns a 4-tuple there.

In `ConnectionPageTests.cs`, add (following the file's existing pattern for building the page):

```csharp
    [AvaloniaFact]
    public void HardwareSummary_AndVersionMismatch()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new ConnectionPage(vm.Connection);
        new Window { Content = page }.Show();

        rig.Connection!.SetStatus(ConnectionStatus.Connected);
        rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, 5,
            new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("5 sliders · 3 buttons (2 LED, 1 LED pair)", vm.Connection.HardwareSummary);
        Assert.False(vm.Connection.HasControllerError);

        rig.Connection.RaiseMessage(new HelloMessage(9, 5, new[] { IndicatorKind.Led }));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Controller firmware doesn't match this version of Volco. Reflash it.", vm.Connection.ControllerError);
        Assert.Equal("Firmware mismatch", vm.Connection.FooterText);
    }
```

- [ ] **Step 2: Implement** `src/Volco.App/ViewModels/CountHints.cs`:

```csharp
namespace Volco.App.ViewModels;

/// <summary>The line under the Sliders/Buttons header when the controller's count and the number
/// of cards disagree (spec §6.3). Cards are numbered from 0, as on the cards.</summary>
internal static class CountHints
{
    public static string Text(string noun, int? controllerCount, int cards)
    {
        if (controllerCount is not int n || n == cards) return "";
        var nouns = n == 1 ? noun : noun + "s";
        if (cards < n) return $"Your controller has {n} {nouns}; {cards} {(cards == 1 ? "is" : "are")} set up.";
        return cards - n == 1
            ? $"Your controller has {n} {nouns}; card {n} does nothing."
            : $"Your controller has {n} {nouns}; cards {n}–{cards - 1} do nothing.";
    }
}
```

`ActionCatalog`: add next to `LightDescription`:

```csharp
    /// <summary>The "Screen: …" line on a button card with a screen.</summary>
    public static string ScreenDescription(ButtonBindingConfig binding)
    {
        if (binding.SinglePress.Action != ButtonActionType.None) return "Shows the Press action.";
        if (binding.DoublePress.Action != ButtonActionType.None) return "Shows the Double press action.";
        if (binding.LongPress.Action != ButtonActionType.None) return "Shows the Hold action.";
        return "Blank. None of this button’s actions is set in this preset.";
    }
```

Delete `LightKind`, `LightKindOf` and `LightSource`'s use of `LightKind`. `LightSource` becomes "the first action that isn't None". Every action except None has a state, so the result is unchanged:

```csharp
    public static (ButtonActionConfig Action, string Slot)? LightSource(ButtonBindingConfig binding)
    {
        if (binding.SinglePress.Action != ButtonActionType.None) return (binding.SinglePress, "Press");
        if (binding.DoublePress.Action != ButtonActionType.None) return (binding.DoublePress, "Double press");
        if (binding.LongPress.Action != ButtonActionType.None) return (binding.LongPress, "Hold");
        return null;
    }
```

`ButtonItemViewModel`:
- remove `LightKind` and its four `Is…Light` properties, and `PresetDots`;
- add:

```csharp
    /// <summary>What the controller reported for this input (spec §6.3); null before the first Hello.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLed), nameof(IsLedPair), nameof(IsScreen), nameof(IsIndicatorUnknown), nameof(IndicatorText))]
    private IndicatorKind? _indicator;

    public bool IsLed => Indicator == IndicatorKind.Led;
    public bool IsLedPair => Indicator == IndicatorKind.LedPair;
    public bool IsScreen => Indicator == IndicatorKind.Screen;
    public bool IsIndicatorUnknown => Indicator is null;

    public string IndicatorText => Indicator switch
    {
        null => "Connect your controller to see its lights and screens.",
        IndicatorKind.Led => "Light: LED",
        IndicatorKind.LedPair => "Light: LED pair",
        IndicatorKind.Screen => "Light: Screen",
        _ => "Light: None",
    };

    /// <summary>What this button's screen shows in the edited preset.</summary>
    [ObservableProperty]
    private ScreenContent _screen = ScreenContent.Blank;

    partial void OnIndexChanged(int value) => Refresh();
```

`Refresh()` becomes:

```csharp
    internal void Refresh()
    {
        var edited = Config.BindingFor(_owner.EditingPresetId);
        IsNotSetUp = Config.Actions.Values.All(b => b is null || b.IsEmpty);
        var summary = ActionCatalog.Summary(edited, _owner.PresetName, _owner.DeviceName);
        Summary = summary.Length > 0 ? summary : IsNotSetUp ? "Not set up" : "Nothing in this preset";

        Indicator = _owner.IndicatorFor(Index);
        LightDescription = IsScreen ? ActionCatalog.ScreenDescription(edited) : ActionCatalog.LightDescription(edited);

        // LEDs show what the controller shows now (the live preset); a screen preview shows the
        // preset being edited, so editing it is visible.
        var live = Config.BindingFor(_owner.LivePresetId);
        LightValue = ControllerDisplay.LedValueFor(live, _owner.AppConfig, _owner.LivePresetId, _owner.Audio);
        Screen = ControllerDisplay.Screen(ControllerDisplay.PrimaryAction(_owner.AppConfig, Index, _owner.EditingPresetId),
                                          _owner.AppConfig, _owner.EditingPresetId, _owner.Audio);
    }
```

Then:
- `IButtonCardOwner` gains `IndicatorKind? IndicatorFor(int index);` and `string DeviceName(DeviceRefConfig? device);` (the second is used by Task 12; implement it now).
- `ButtonsViewModel` implements both:

```csharp
    public IndicatorKind? IndicatorFor(int index)
        => _config.Config.Controller is not { } c ? null
           : index < c.Buttons.Count ? c.Buttons[index] : IndicatorKind.None;

    public string DeviceName(DeviceRefConfig? device) => DeviceDisplay.NameOf(_config.Config, _audio, device, "?");
```

`ActionCatalog.Summary`/`ShortLabel` take an optional device namer; existing calls keep working:

```csharp
    public static string ShortLabel(ButtonActionConfig a, Func<string?, string?> presetName,
                                    Func<DeviceRefConfig?, string>? deviceName = null)
    {
        var name = deviceName ?? DeviceShort;
        return a.Action switch
        {
            ButtonActionType.None => "Nothing",
            ButtonActionType.MuteCurrentOutput => "Mute output",
            ButtonActionType.MuteCurrentInput => "Mute mic",
            ButtonActionType.MuteSpecificDevice => $"Mute {name(a.Device)}",
            ButtonActionType.SetOutputDevice => $"Switch to {name(a.Device)}",
            ButtonActionType.ToggleOutputDevice => $"Toggle {name(a.Device)} / {name(a.DeviceB)}",
            ButtonActionType.NextPreset => "Next preset",
            ButtonActionType.GoToPreset => $"Go to {presetName(a.PresetId) ?? "a preset"}",
            _ => a.Action.ToString(),
        };
    }

    public static string Summary(ButtonBindingConfig b, Func<string?, string?> presetName,
                                 Func<DeviceRefConfig?, string>? deviceName = null)
    {
        var parts = new List<string>();
        if (b.SinglePress.Action != ButtonActionType.None) parts.Add(ShortLabel(b.SinglePress, presetName, deviceName));
        if (b.DoublePress.Action != ButtonActionType.None) parts.Add("Double: " + ShortLabel(b.DoublePress, presetName, deviceName));
        if (b.LongPress.Action != ButtonActionType.None) parts.Add("Hold: " + ShortLabel(b.LongPress, presetName, deviceName));
        return string.Join(" · ", parts);
    }
```

`DeviceDisplay.NameOf` returns `"?"` for an unset reference, as `DeviceShort` does.

Count hint in `ButtonsViewModel` (the same pattern goes into `SlidersViewModel` with `"slider"` and `Controller.Sliders`):

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCountHint))]
    private string _countHint = "";

    public bool HasCountHint => CountHint.Length > 0;

    [ObservableProperty]
    private bool _canAddMissing;

    [ObservableProperty]
    private string _addMissingText = "";

    /// <summary>Re-reads the controller's hardware (a Hello arrived) or the card count changed.</summary>
    public void RefreshController()
    {
        var n = _config.Config.Controller?.Buttons.Count;
        CountHint = CountHints.Text("button", n, Items.Count);
        var missing = (n ?? 0) - Items.Count;
        CanAddMissing = missing > 0;
        AddMissingText = missing == 1 ? "Add the missing one" : $"Add the missing {missing}";
        RefreshLights();
    }

    [RelayCommand]
    private void AddMissing()
    {
        var target = _config.Config.Controller?.Buttons.Count ?? 0;
        while (Items.Count < target)
        {
            var cfg = new ButtonConfig();
            Buttons.Add(cfg);
            Items.Add(new ButtonItemViewModel(cfg, Items.Count, _devices, this));
        }
        Renumber();
        RefreshController();
        Save();
    }
```

Call `RefreshController()` at the end of the constructor, `Add()`, `RemoveLast()` and its Undo. In `SlidersViewModel`, `AddMissing` appends `new SliderConfig { Type = SliderMappingType.None }` items through `NewItem`, then calls `AppsChanged()` as `Add()` does.

`SettingsViewModel`:
- subscribe `_runtime.ControllerInfoChanged += OnControllerInfoChanged;`, and unsubscribe in `Dispose`;
- the handler:

```csharp
    private void OnControllerInfoChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            _buttonsViewModel?.RefreshController();
            _slidersViewModel?.RefreshController();
        });
```

`ConnectionViewModel`: subscribe in the constructor and unsubscribe in `Dispose()`:

```csharp
        _runtime.ControllerStateChanged += OnControllerChanged;
        _runtime.ControllerInfoChanged += OnControllerInfo;
```

and add:

```csharp
    public const string MismatchText = "Controller firmware doesn't match this version of Volco. Reflash it.";

    /// <summary>"5 sliders · 3 buttons (2 LED, 1 LED pair)", from the last Hello; "" before one.</summary>
    public string HardwareSummary => Summarize(_config.Config.Controller);
    public bool HasHardwareSummary => HardwareSummary.Length > 0;
    public string ControllerError => _runtime.ControllerState == ControllerState.VersionMismatch ? MismatchText : "";
    public bool HasControllerError => ControllerError.Length > 0;

    internal static string Summarize(ControllerInfoConfig? c)
    {
        if (c is null) return "";
        string Plural(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";
        var groups = new List<string>();
        void Add(IndicatorKind kind, Func<int, string> text)
        {
            var n = c.Buttons.Count(k => k == kind);
            if (n > 0) groups.Add(text(n));
        }
        Add(IndicatorKind.Led, n => $"{n} LED");
        Add(IndicatorKind.LedPair, n => $"{n} LED pair");
        Add(IndicatorKind.Screen, n => Plural(n, "screen"));
        Add(IndicatorKind.None, n => $"{n} with no light");
        var buttons = Plural(c.Buttons.Count, "button") + (groups.Count > 0 ? $" ({string.Join(", ", groups)})" : "");
        return $"{Plural(c.Sliders, "slider")} · {buttons}";
    }

    private void OnControllerChanged(object? sender, ControllerState state)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(ControllerError));
            OnPropertyChanged(nameof(HasControllerError));
            OnPropertyChanged(nameof(FooterText));
        });

    private void OnControllerInfo(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(HardwareSummary));
            OnPropertyChanged(nameof(HasHardwareSummary));
        });
```

`FooterText` starts with `_runtime.ControllerState == ControllerState.VersionMismatch ? "Firmware mismatch" : Status switch { … }`.

**XAML**

`ButtonsPage.axaml`: replace the `LightPreview` panel's contents with:

```xml
                      <Ellipse Name="Led" Classes="Led" Classes.on="{Binding LedOn}" IsVisible="{Binding IsLed}"/>
                      <StackPanel Orientation="Horizontal" Spacing="4" IsVisible="{Binding IsLedPair}">
                        <TextBlock Text="A" Classes="Caption" VerticalAlignment="Center"/>
                        <Ellipse Name="LedA" Classes="Led" Classes.on="{Binding LedA}"/>
                        <Ellipse Name="LedB" Classes="Led" Classes.on="{Binding LedB}"/>
                        <TextBlock Text="B" Classes="Caption" VerticalAlignment="Center"/>
                      </StackPanel>
                      <controls:ScreenPreview Name="ScreenPreviewSmall" Scale="1" Screen="{Binding Screen}"
                                              IsVisible="{Binding IsScreen}"/>
```

In the body, replace the `LightLine` border's content with:

```xml
                        <StackPanel Spacing="8">
                          <TextBlock Name="IndicatorText" Classes="Secondary" FontWeight="SemiBold" Text="{Binding IndicatorText}"/>
                          <TextBlock Name="LightDescriptionText" Classes="Secondary" TextWrapping="Wrap"
                                     Text="{Binding LightDescription}" IsVisible="{Binding !IsIndicatorUnknown}"/>
                          <controls:ScreenPreview Name="ScreenPreviewLarge" Scale="2" Screen="{Binding Screen}"
                                                  IsVisible="{Binding IsScreen}"/>
                        </StackPanel>
```

Under the page header grid of both `ButtonsPage.axaml` and `SlidersPage.axaml`:

```xml
      <Border Name="CountHintPanel" Classes="Panel" Padding="12,8" IsVisible="{Binding HasCountHint}">
        <Grid ColumnDefinitions="*,Auto">
          <TextBlock Name="CountHintText" Classes="Secondary" TextWrapping="Wrap" VerticalAlignment="Center"
                     Text="{Binding CountHint}"/>
          <Button Name="AddMissingButton" Grid.Column="1" Margin="12,0,0,0" Content="{Binding AddMissingText}"
                  Command="{Binding AddMissingCommand}" IsVisible="{Binding CanAddMissing}"/>
        </Grid>
      </Border>
```

`ConnectionPage.axaml`: below the `StatusPanel` grid's row, add a second row (`RowDefinitions="Auto,Auto"`, existing controls in row 0):

```xml
          <StackPanel Grid.Row="1" Grid.ColumnSpan="4" Margin="18,6,0,0" Spacing="2">
            <TextBlock Name="HardwareSummaryText" Classes="Caption" Text="{Binding HardwareSummary}"
                       IsVisible="{Binding HasHardwareSummary}"/>
            <TextBlock Name="ControllerErrorText" Classes="Caption" Foreground="{DynamicResource DangerBrush}"
                       TextWrapping="Wrap" Text="{Binding ControllerError}" IsVisible="{Binding HasControllerError}"/>
          </StackPanel>
```

Run `grep -rn "IsPresetLight\|PresetDots\|LightKind\|IsOnOffLight" src tests`. Fix any remaining use: tests that asserted preset dots now assert the LED for the hardware kind.

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(app): button cards follow the controller's lights and screens; count hints; hardware summary`.

---

### Task 12: Nicknames in the device pickers

**Files:**
- Create: `src/Volco.App/ViewModels/DeviceChoice.cs` (`IDeviceLabeler`, `DeviceChoice`)
- Create: `src/Volco.App/Converters/DeviceLabelConverter.cs`
- Modify: `src/Volco.App/ViewModels/ActionSlotViewModel.cs`, `ButtonItemViewModel.cs`, `SliderItemViewModel.cs`, `SlidersViewModel.cs`
- Modify: `src/Volco.App/Controls/ActionRow.axaml`, `src/Volco.App/Views/Pages/SlidersPage.axaml`
- Test: `tests/Volco.App.Tests/Pages/ButtonsPageTests.cs`, `SlidersPageTests.cs`

The combo boxes keep their **string** items (Windows friendly names), so selection logic and existing tests are unchanged. Only the item template changes: it shows the display name, with the Windows name as secondary text when it differs.

**Interfaces**
- **Produces:**
  - `interface IDeviceLabeler { DeviceChoice Describe(string friendlyName); }`, implemented by `ActionSlotViewModel` and `SliderItemViewModel`
  - `record DeviceChoice(string Label, string Secondary)`
  - `DeviceLabelConverter` (multi-value: item string + combo `DataContext`; parameter `"label"` or `"secondary"`)

- [ ] **Step 1: Write the failing tests.** Add to `ButtonsPageTests.cs`:

```csharp
    [AvaloniaFact]
    public void DevicePicker_ShowsNicknames_WithTheWindowsNameAsSecondaryText()
    {
        var (page, rig, vm) = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "dev-2", Nickname = "Cans" });
            DefaultOf(c, 0).SinglePress.Action = ButtonActionType.MuteSpecificDevice;
        });
        using var _ = rig; using var __ = vm;
        Expand(vm, 0);
        var slot = vm.Buttons.Items[0].Press;
        Assert.Equal(new DeviceChoice("Cans", "Headphones"), ((IDeviceLabeler)slot).Describe("Headphones"));
        Assert.Equal(new DeviceChoice("Speakers", ""), ((IDeviceLabeler)slot).Describe("Speakers"));
    }

    [AvaloniaFact]
    public void CardSummary_UsesNicknames()
    {
        var (_, rig, vm) = Show(c =>
        {
            c.Devices.Add(new DeviceSettingsConfig { Id = "dev-1", Nickname = "Desk" });
            DefaultOf(c, 0).SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.SetOutputDevice, Device = new DeviceRefConfig { Id = "dev-1", FriendlyName = "Speakers" },
            };
        });
        using var _ = rig; using var __ = vm;
        Assert.Equal("Switch to Desk", vm.Buttons.Items[0].Summary);
    }
```

Add to `SlidersPageTests.cs`:

```csharp
    [AvaloniaFact]
    public void SliderDevicePicker_DescribesTheDefaultEntryAndNicknames()
    {
        var (_, _, rig, vm) = Show(c =>
        {
            c.Sliders[0].Type = SliderMappingType.Device;
            c.Devices.Add(new DeviceSettingsConfig { Id = "dev-1", Nickname = "Desk" });
        });
        using var __ = rig; using var ___ = vm;
        var labeler = (IDeviceLabeler)vm.Sliders.Items[0];
        Assert.Equal(new DeviceChoice(SliderItemViewModel.CurrentDefaultOutput, ""), labeler.Describe(SliderItemViewModel.CurrentDefaultOutput));
        Assert.Equal("Desk", labeler.Describe("Speakers").Label);
    }
```

Check the device ids and names in each test file's own fake engine, and adjust the literals to match.

- [ ] **Step 2: Implement** `src/Volco.App/ViewModels/DeviceChoice.cs`:

```csharp
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Devices;

namespace Volco.App.ViewModels;

/// <summary>How a device-picker entry reads: the display name, and the Windows name as secondary
/// text when it differs (spec §6.2).</summary>
public sealed record DeviceChoice(string Label, string Secondary)
{
    public bool HasSecondary => Secondary.Length > 0;

    /// <summary>Describes a picker entry (a Windows friendly name) using the device list and settings.</summary>
    public static DeviceChoice For(AppConfig config, IReadOnlyList<AudioDevice> devices, string friendlyName)
    {
        var device = devices.FirstOrDefault(d => string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));
        var label = device is not null ? DeviceDisplay.For(config, device).Name : DeviceDisplay.AutoShortName(friendlyName);
        return new DeviceChoice(label, label == friendlyName ? "" : friendlyName);
    }
}

/// <summary>A view model whose device combo boxes show <see cref="DeviceChoice"/> labels.</summary>
public interface IDeviceLabeler
{
    DeviceChoice Describe(string friendlyName);
}
```

`src/Volco.App/Converters/DeviceLabelConverter.cs`:

```csharp
using System.Globalization;
using Avalonia.Data.Converters;
using Volco.App.ViewModels;

namespace Volco.App.Converters;

/// <summary>[item (friendly name), combo DataContext (IDeviceLabeler)] → label or secondary text.</summary>
public sealed class DeviceLabelConverter : IMultiValueConverter
{
    public static readonly DeviceLabelConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not string name) return "";
        if (values[1] is not IDeviceLabeler labeler) return parameter as string == "secondary" ? "" : name;
        var choice = labeler.Describe(name);
        return parameter as string == "secondary" ? choice.Secondary : choice.Label;
    }
}
```

`ActionSlotViewModel`:
- implements `IDeviceLabeler`;
- its constructor gets a `Func<string, DeviceChoice> describe` parameter, stored in a field, with `public DeviceChoice Describe(string friendlyName) => _describe(friendlyName);`.

`ButtonItemViewModel` passes `name => _owner.Describe(name)`. `IButtonCardOwner` gains `DeviceChoice Describe(string friendlyName);`, which `ButtonsViewModel` implements as `DeviceChoice.For(_config.Config, _devices, friendlyName)`.

`SliderItemViewModel` implements `IDeviceLabeler`:

```csharp
    public DeviceChoice Describe(string friendlyName)
        => friendlyName == CurrentDefaultOutput ? new DeviceChoice(CurrentDefaultOutput, "") : _owner.Describe(friendlyName);
```

`ISliderCardOwner` gains `DeviceChoice Describe(string friendlyName);`, which `SlidersViewModel` implements with `DeviceChoice.For(_config.Config, _devices, friendlyName)`.

XAML: add a shared item template to `ActionRow.axaml`'s `UserControl.Resources` (with `xmlns:conv="using:Volco.App.Converters"`):

```xml
  <UserControl.Resources>
    <DataTemplate x:Key="DeviceItem" x:DataType="x:String">
      <StackPanel Orientation="Horizontal" Spacing="8">
        <TextBlock>
          <TextBlock.Text>
            <MultiBinding Converter="{x:Static conv:DeviceLabelConverter.Instance}" ConverterParameter="label">
              <Binding/>
              <ReflectionBinding Path="$parent[ComboBox].DataContext"/>
            </MultiBinding>
          </TextBlock.Text>
        </TextBlock>
        <TextBlock Classes="Caption" VerticalAlignment="Center">
          <TextBlock.Text>
            <MultiBinding Converter="{x:Static conv:DeviceLabelConverter.Instance}" ConverterParameter="secondary">
              <Binding/>
              <ReflectionBinding Path="$parent[ComboBox].DataContext"/>
            </MultiBinding>
          </TextBlock.Text>
        </TextBlock>
      </StackPanel>
    </DataTemplate>
  </UserControl.Resources>
```

Then:
- set `ItemTemplate="{StaticResource DeviceItem}"` on `DeviceCombo`, `DeviceACombo` and `DeviceBCombo`;
- add the same resource and attribute to `SlidersPage.axaml`'s `DeviceCombo`;
- when the item template is shown inside the dropdown popup, `$parent[ComboBox]` still resolves through the logical tree. If a headless run shows empty labels, bind the second value with `{ReflectionBinding $parent[ComboBox].DataContext, RelativeSource=…}` instead, or fall back to a `ComboBox.Tag="{Binding}"` on the combo and `$parent[ComboBox].Tag`.

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(app): device pickers and summaries show nicknames`.

---

### Task 13: Firmware protocol library, native test environment and CI job

**Files:**
- Modify: `firmware/platformio.ini`
- Create: `firmware/lib/volco_protocol/src/volco_protocol.h`, `volco_protocol.cpp`
- Create: `firmware/test/test_protocol/test_main.cpp`
- Create: `firmware/test/golden_frames.h` (helper that loads the golden file)
- Modify: `.github/workflows/build.yml` (new `firmware-test` job)

**Interfaces**
- **Produces** (namespace `volco`, used by Tasks 15–17):
  - constants: `kStartMarker`, `kProtocolVersion = 1`, `kMaxPayload = 255`, `kMaxText = 60`, `kMaxButtons = 32`, `kNoIcon = 0xFF`
  - enums: `MsgType`, `Kind` (`KIND_NONE/LED/LED_PAIR/SCREEN`), `Transition` (`TRANSITION_NONE/SLIDE_LEFT/SLIDE_RIGHT`), `TopKind` (`TOP_EMPTY/TEXT/DOTS`)
  - `struct ScreenContent` (fields below; `==` compares them)
  - `class FrameParser { bool feed(uint8_t); const uint8_t* payload() const; uint8_t length() const; }`
  - free functions: `encodeFrame`, `encodeHello`, `encodeAlive`, `encodeSlider`, `encodeButton`, `decodeScreen`
  - `ScreenContent screenText(const char* top, uint8_t icon, const char* main)`
  - `struct LinkSink` (`send`, `applyLed`, `applyScreen`, `hostLost`)
  - `struct LinkConfig`
  - `class Link { begin, onPayload, tick, sendSlider, sendButton, hostAlive, holding }`

- [ ] **Step 1: Update `firmware/platformio.ini`:**

```ini
[platformio]
default_envs = denky32

[env:denky32]
platform = espressif32
board = esp32dev
framework = arduino
monitor_speed = 115200
build_flags =
    -std=gnu++17
build_unflags =
    -std=gnu++11
lib_deps =
    links2004/WebSockets@^2.4.0
    olikraus/U8g2@^2.34.17
test_ignore = *

; Host-side unit tests of the pure libraries in lib/ (protocol, bidi, screen state, render).
; Run with: pio test -e native
[env:native]
platform = native
build_flags =
    -std=gnu++17
    -Wall
    -DVOLCO_NATIVE
test_build_src = no
```

- [ ] **Step 2: Write the failing tests.** `firmware/test/golden_frames.h`:

```cpp
#pragma once
// Loads firmware/test/golden/frames.txt (shared with the C# tests) for native tests.
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <string>
#include <vector>

struct GoldenFrame { std::string name; std::vector<uint8_t> bytes; };

inline std::vector<GoldenFrame> loadGolden() {
  // The test binary's working directory varies; derive the path from this header's location.
  std::string here = __FILE__;
  std::string dir = here.substr(0, here.find_last_of("/\\") + 1);
  const char* candidates[] = {"golden/frames.txt", "test/golden/frames.txt", "../test/golden/frames.txt"};
  FILE* f = fopen((dir + "golden/frames.txt").c_str(), "r");
  for (const char* c : candidates) if (!f) f = fopen(c, "r");
  std::vector<GoldenFrame> out;
  if (!f) return out;
  char line[1024];
  while (fgets(line, sizeof line, f)) {
    if (line[0] == '#' || line[0] == '\n' || line[0] == '\r') continue;
    GoldenFrame g;
    char* tok = strtok(line, " \t\r\n");
    if (!tok) continue;
    g.name = tok;
    while ((tok = strtok(nullptr, " \t\r\n"))) g.bytes.push_back((uint8_t)strtoul(tok, nullptr, 16));
    out.push_back(g);
  }
  fclose(f);
  return out;
}

inline std::vector<uint8_t> golden(const char* name) {
  static std::vector<GoldenFrame> all = loadGolden();
  for (auto& g : all) if (g.name == name) return g.bytes;
  return {};
}
```

`firmware/test/test_protocol/test_main.cpp`:

```cpp
#include <unity.h>
#include <string.h>
#include <vector>
#include "../golden_frames.h"
#include "volco_protocol.h"

using namespace volco;

// ── A recording sink ─────────────────────────────────────────────────────────
struct Call { char what; uint8_t button, value, transition; ScreenContent screen; uint32_t now; };
struct FakeSink : LinkSink {
  std::vector<std::vector<uint8_t>> sent;
  std::vector<Call> calls;
  int lost = 0;
  void send(const uint8_t* f, size_t n) override { sent.emplace_back(f, f + n); }
  void applyLed(uint8_t b, uint8_t v) override { calls.push_back({'L', b, v, 0, {}, 0}); }
  void applyScreen(uint8_t b, uint8_t t, const ScreenContent& c, uint32_t now) override {
    calls.push_back({'S', b, 0, t, c, now});
  }
  void hostLost() override { lost++; }
};

static const uint8_t KINDS[] = {KIND_LED, KIND_LED_PAIR, KIND_SCREEN, KIND_SCREEN};
static LinkConfig config() { return LinkConfig{5, 4, KINDS, 3000, 1000, 500}; }

// Feeds a whole frame through a parser and hands the payload to the link.
static void deliver(Link& link, const std::vector<uint8_t>& frame, uint32_t now) {
  FrameParser p;
  for (uint8_t b : frame)
    if (p.feed(b)) link.onPayload(p.payload(), p.length(), now);
}

static std::vector<uint8_t> frameOf(std::initializer_list<uint8_t> payload) {
  uint8_t out[300];
  std::vector<uint8_t> v(payload);
  size_t n = encodeFrame(v.data(), (uint8_t)v.size(), out);
  return std::vector<uint8_t>(out, out + n);
}

void setUp() {}
void tearDown() {}

// ── Framing ─────────────────────────────────────────────────────────────────
void test_parser_resyncs_after_garbage_and_bad_checksum() {
  FrameParser p;
  std::vector<uint8_t> bytes = {0x00, 0x55, 0xAA, 0x01, 0x82, 0x00 /*bad cs*/};
  auto good = golden("heartbeat");
  bytes.insert(bytes.end(), good.begin(), good.end());
  int frames = 0;
  for (uint8_t b : bytes) if (p.feed(b)) { frames++; TEST_ASSERT_EQUAL_HEX8(0x82, p.payload()[0]); }
  TEST_ASSERT_EQUAL(1, frames);
}

void test_parser_accepts_a_255_byte_payload() {
  std::vector<uint8_t> payload(255, 0x11);
  payload[0] = 0x84;
  uint8_t out[300];
  size_t n = encodeFrame(payload.data(), 255, out);
  FrameParser p;
  int frames = 0;
  for (size_t i = 0; i < n; i++) if (p.feed(out[i])) frames++;
  TEST_ASSERT_EQUAL(1, frames);
  TEST_ASSERT_EQUAL(255, p.length());
}

// ── Golden frames ───────────────────────────────────────────────────────────
void test_encodes_controller_messages_to_golden_bytes() {
  uint8_t out[300];
  const uint8_t kinds[] = {KIND_LED, KIND_LED, KIND_LED_PAIR};
  size_t n = encodeHello(5, kinds, 3, out);
  auto g = golden("hello_led_hardware");
  TEST_ASSERT_FALSE_MESSAGE(g.empty(), "golden/frames.txt not found");
  TEST_ASSERT_EQUAL(g.size(), n);
  TEST_ASSERT_EQUAL_MEMORY(g.data(), out, n);
  n = encodeAlive(out);            g = golden("alive");            TEST_ASSERT_EQUAL_MEMORY(g.data(), out, n);
  n = encodeSlider(2, 512, out);   g = golden("slider_2_512");     TEST_ASSERT_EQUAL_MEMORY(g.data(), out, n);
  n = encodeButton(1, true, out);  g = golden("button_1_pressed"); TEST_ASSERT_EQUAL_MEMORY(g.data(), out, n);
}

static ScreenContent decodeGolden(const char* name, uint8_t& button, uint8_t& transition) {
  auto g = golden(name);
  FrameParser p;
  ScreenContent c;
  for (uint8_t b : g)
    if (p.feed(b)) TEST_ASSERT_TRUE(decodeScreen(p.payload(), p.length(), button, transition, c));
  return c;
}

void test_decodes_golden_screens() {
  uint8_t button, transition;
  ScreenContent c = decodeGolden("screen_muted_mic", button, transition);
  TEST_ASSERT_EQUAL(1, button);
  TEST_ASSERT_EQUAL(TRANSITION_NONE, transition);
  TEST_ASSERT_EQUAL(TOP_TEXT, c.topKind);
  TEST_ASSERT_EQUAL_STRING("MUTED", c.top);
  TEST_ASSERT_EQUAL(7, c.icon);
  TEST_ASSERT_EQUAL(1, c.iconFlags);
  TEST_ASSERT_EQUAL_STRING("Microphone", c.main);

  c = decodeGolden("screen_preset_dots_slide_left", button, transition);
  TEST_ASSERT_EQUAL(TRANSITION_SLIDE_LEFT, transition);
  TEST_ASSERT_EQUAL(TOP_DOTS, c.topKind);
  TEST_ASSERT_EQUAL(1, c.dotsSelected);
  TEST_ASSERT_EQUAL(3, c.dotsCount);

  c = decodeGolden("screen_blank_slide_right", button, transition);
  TEST_ASSERT_EQUAL(kNoIcon, c.icon);
  TEST_ASSERT_EQUAL(0, c.mainLen);

  c = decodeGolden("screen_hebrew_main", button, transition);
  TEST_ASSERT_EQUAL_STRING("\xD7\xA8\xD7\x9E\xD7\xA7\xD7\x95\xD7\x9C\xD7\x99\xD7\x9D", c.main);   // רמקולים
}

void test_decode_screen_rejects_overrunning_lengths() {
  const uint8_t payload[] = {0x84, 0, 0, 1, TOP_TEXT, 50, 'a', 'b'};   // claims 50 bytes, has 2
  uint8_t button, transition;
  ScreenContent c;
  TEST_ASSERT_FALSE(decodeScreen(payload, sizeof payload, button, transition, c));
}

// ── Session ─────────────────────────────────────────────────────────────────
void test_begin_sends_hello() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  TEST_ASSERT_EQUAL(1, sink.sent.size());
  TEST_ASSERT_EQUAL_HEX8(0x01, sink.sent[0][2]);
}

void test_hello_request_is_answered() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("hello_request"), 10);
  TEST_ASSERT_EQUAL(2, sink.sent.size());
  TEST_ASSERT_EQUAL_HEX8(0x01, sink.sent[1][2]);
}

void test_led_and_screen_apply_only_to_matching_kinds() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, frameOf({0x83, 0, 1}), 1);     // LED button: applied
  deliver(link, frameOf({0x83, 2, 1}), 1);     // screen button: ignored
  deliver(link, frameOf({0x83, 9, 1}), 1);     // out of range: ignored
  deliver(link, golden("screen_blank_slide_right"), 1);   // button 2 is a screen: applied
  deliver(link, golden("screen_muted_mic"), 1);           // button 1 is an LED pair: ignored
  TEST_ASSERT_EQUAL(2, sink.calls.size());
  TEST_ASSERT_EQUAL('L', sink.calls[0].what);
  TEST_ASSERT_EQUAL('S', sink.calls[1].what);
  TEST_ASSERT_EQUAL(TRANSITION_SLIDE_RIGHT, sink.calls[1].transition);
}

void test_begin_commit_holds_then_applies_together() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("begin"), 100);
  deliver(link, frameOf({0x83, 0, 1}), 110);
  deliver(link, golden("screen_blank_slide_right"), 120);
  TEST_ASSERT_EQUAL(0, sink.calls.size());
  TEST_ASSERT_TRUE(link.holding());
  deliver(link, golden("commit"), 130);
  TEST_ASSERT_EQUAL(2, sink.calls.size());
  TEST_ASSERT_EQUAL(130, sink.calls[1].now);
  TEST_ASSERT_FALSE(link.holding());
}

void test_held_replacement_keeps_the_held_transition() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("begin"), 0);
  deliver(link, frameOf({0x84, 3, TRANSITION_SLIDE_LEFT, 1, TOP_EMPTY, 0xFF, 0, 1, 'a'}), 1);
  deliver(link, frameOf({0x84, 3, TRANSITION_NONE, 1, TOP_EMPTY, 0xFF, 0, 1, 'b'}), 2);
  deliver(link, golden("commit"), 3);
  TEST_ASSERT_EQUAL(1, sink.calls.size());
  TEST_ASSERT_EQUAL(TRANSITION_SLIDE_LEFT, sink.calls[0].transition);
  TEST_ASSERT_EQUAL_STRING("b", sink.calls[0].screen.main);
}

void test_commit_fallback_after_500ms() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("begin"), 1000);
  deliver(link, frameOf({0x83, 0, 1}), 1001);
  link.tick(1499);
  TEST_ASSERT_EQUAL(0, sink.calls.size());
  link.tick(1500);
  TEST_ASSERT_EQUAL(1, sink.calls.size());
}

void test_commit_without_begin_and_empty_commit_are_noops() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("commit"), 1);
  deliver(link, golden("begin"), 2);
  deliver(link, golden("commit"), 3);
  TEST_ASSERT_EQUAL(0, sink.calls.size());
}

void test_alive_when_idle_for_a_second() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);                       // Hello at t=0
  link.tick(999);
  TEST_ASSERT_EQUAL(1, sink.sent.size());
  link.tick(1000);
  TEST_ASSERT_EQUAL(2, sink.sent.size());
  TEST_ASSERT_EQUAL_HEX8(0x02, sink.sent[1][2]);
  link.sendSlider(0, 100, 1500);       // traffic resets the idle clock
  link.tick(2400);
  TEST_ASSERT_EQUAL(3, sink.sent.size());
}

void test_host_timeout_then_hello_on_next_frame() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("heartbeat"), 100);
  TEST_ASSERT_TRUE(link.hostAlive());
  link.tick(3099);
  TEST_ASSERT_EQUAL(0, sink.lost);
  link.tick(3100);
  TEST_ASSERT_EQUAL(1, sink.lost);
  TEST_ASSERT_FALSE(link.hostAlive());
  size_t before = sink.sent.size();
  deliver(link, golden("heartbeat"), 5000);
  TEST_ASSERT_TRUE(link.hostAlive());
  TEST_ASSERT_EQUAL(before + 1, sink.sent.size());
  TEST_ASSERT_EQUAL_HEX8(0x01, sink.sent.back()[2]);     // Hello
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_parser_resyncs_after_garbage_and_bad_checksum);
  RUN_TEST(test_parser_accepts_a_255_byte_payload);
  RUN_TEST(test_encodes_controller_messages_to_golden_bytes);
  RUN_TEST(test_decodes_golden_screens);
  RUN_TEST(test_decode_screen_rejects_overrunning_lengths);
  RUN_TEST(test_begin_sends_hello);
  RUN_TEST(test_hello_request_is_answered);
  RUN_TEST(test_led_and_screen_apply_only_to_matching_kinds);
  RUN_TEST(test_begin_commit_holds_then_applies_together);
  RUN_TEST(test_held_replacement_keeps_the_held_transition);
  RUN_TEST(test_commit_fallback_after_500ms);
  RUN_TEST(test_commit_without_begin_and_empty_commit_are_noops);
  RUN_TEST(test_alive_when_idle_for_a_second);
  RUN_TEST(test_host_timeout_then_hello_on_next_frame);
  return UNITY_END();
}
```

- [ ] **Step 3: Implement** `firmware/lib/volco_protocol/src/volco_protocol.h`:

```cpp
#pragma once
// The Volco wire protocol, controller side (spec §3). Pure C++: no Arduino, time is passed in,
// output goes through a LinkSink. Built natively for tests (pio test -e native).
#include <stddef.h>
#include <stdint.h>

namespace volco {

constexpr uint8_t kStartMarker = 0xAA;
constexpr uint8_t kProtocolVersion = 1;
constexpr uint16_t kMaxPayload = 255;  // type + body
constexpr uint8_t kMaxText = 60;       // UTF-8 bytes per text slot
constexpr uint8_t kMaxButtons = 32;
constexpr uint8_t kNoIcon = 0xFF;

enum MsgType : uint8_t {
  MSG_HELLO = 0x01, MSG_ALIVE = 0x02, MSG_SLIDER = 0x03, MSG_BUTTON = 0x04,
  MSG_HELLO_REQUEST = 0x81, MSG_HEARTBEAT = 0x82, MSG_LED = 0x83, MSG_SCREEN = 0x84,
  MSG_BEGIN = 0x85, MSG_COMMIT = 0x86,
};
enum Kind : uint8_t { KIND_NONE = 0, KIND_LED = 1, KIND_LED_PAIR = 2, KIND_SCREEN = 3 };
enum Transition : uint8_t { TRANSITION_NONE = 0, TRANSITION_SLIDE_LEFT = 1, TRANSITION_SLIDE_RIGHT = 2 };
enum TopKind : uint8_t { TOP_EMPTY = 0, TOP_TEXT = 1, TOP_DOTS = 2 };

// One screen in layout 1 (spec §3.3). Text is NUL-terminated UTF-8 in logical order.
struct ScreenContent {
  uint8_t layout = 1;
  uint8_t topKind = TOP_EMPTY;
  uint8_t topLen = 0;
  char top[kMaxText + 1] = {0};
  uint8_t dotsSelected = 0, dotsCount = 0;
  uint8_t icon = kNoIcon;
  uint8_t iconFlags = 0;  // bit 0: muted slash
  uint8_t mainLen = 0;
  char main[kMaxText + 1] = {0};
  bool operator==(const ScreenContent& o) const;
  bool operator!=(const ScreenContent& o) const { return !(*this == o); }
};

// Builds a ScreenContent with top text (or none for ""), an icon and main text.
ScreenContent screenText(const char* top, uint8_t icon, const char* main);

// Byte-at-a-time frame parser: 0xAA | length | payload | XOR(payload).
class FrameParser {
 public:
  // True when this byte completed a valid frame; payload()/length() then hold it.
  bool feed(uint8_t b);
  const uint8_t* payload() const { return buf_; }
  uint8_t length() const { return len_; }

 private:
  enum State : uint8_t { WAIT_START, WAIT_LENGTH, WAIT_PAYLOAD, WAIT_CHECKSUM };
  State state_ = WAIT_START;
  uint8_t buf_[kMaxPayload] = {0};
  uint8_t len_ = 0;
  uint16_t idx_ = 0;
};

// Writes a whole frame for `payload` (type + body) into `out` (len + 3 bytes). Returns its size.
size_t encodeFrame(const uint8_t* payload, uint8_t len, uint8_t* out);
size_t encodeHello(uint8_t sliders, const uint8_t* kinds, uint8_t buttons, uint8_t* out);
size_t encodeAlive(uint8_t* out);
size_t encodeSlider(uint8_t index, uint16_t value, uint8_t* out);
size_t encodeButton(uint8_t index, bool pressed, uint8_t* out);

// Decodes a Screen payload. False when a length overruns the payload (the message is dropped).
bool decodeScreen(const uint8_t* payload, uint8_t len, uint8_t& button, uint8_t& transition, ScreenContent& out);

// What the session does to the outside world.
struct LinkSink {
  virtual void send(const uint8_t* frame, size_t len) = 0;
  virtual void applyLed(uint8_t button, uint8_t value) = 0;
  virtual void applyScreen(uint8_t button, uint8_t transition, const ScreenContent& content, uint32_t now) = 0;
  virtual void hostLost() = 0;  // LEDs off, local "not connected" screen
  virtual ~LinkSink() = default;
};

struct LinkConfig {
  uint8_t sliders;
  uint8_t buttons;
  const uint8_t* kinds;  // one Kind per button
  uint32_t hostTimeoutMs;
  uint32_t keepaliveMs;
  uint32_t commitFallbackMs;
};

// The controller side of a session (spec §3.4): Hello, keepalive, host timeout, Begin/Commit.
class Link {
 public:
  Link(const LinkConfig& config, LinkSink& sink);
  void begin(uint32_t now);                                       // boot: sends Hello
  void onPayload(const uint8_t* payload, uint8_t len, uint32_t now);
  void tick(uint32_t now);                                        // every loop pass
  void sendSlider(uint8_t index, uint16_t value, uint32_t now);
  void sendButton(uint8_t index, bool pressed, uint32_t now);
  bool hostAlive() const { return hostAlive_; }
  bool holding() const { return holding_; }

 private:
  struct Held {
    bool hasLed = false;
    uint8_t led = 0;
    bool hasScreen = false;
    uint8_t transition = TRANSITION_NONE;
    ScreenContent screen;
  };
  void sendFrame(const uint8_t* payload, uint8_t len, uint32_t now);
  void sendHello(uint32_t now);
  void applyHeld(uint32_t now);
  bool isLedButton(uint8_t b) const;
  bool isScreenButton(uint8_t b) const;

  LinkConfig config_;
  LinkSink& sink_;
  Held held_[kMaxButtons];
  bool holding_ = false;
  uint32_t holdStart_ = 0;
  bool hostAlive_ = false;
  uint32_t lastRx_ = 0;
  uint32_t lastTx_ = 0;
};

}  // namespace volco
```

`firmware/lib/volco_protocol/src/volco_protocol.cpp`:

```cpp
#include "volco_protocol.h"

#include <string.h>

namespace volco {

bool ScreenContent::operator==(const ScreenContent& o) const {
  return layout == o.layout && topKind == o.topKind && topLen == o.topLen && memcmp(top, o.top, topLen) == 0 &&
         dotsSelected == o.dotsSelected && dotsCount == o.dotsCount && icon == o.icon &&
         iconFlags == o.iconFlags && mainLen == o.mainLen && memcmp(main, o.main, mainLen) == 0;
}

static uint8_t copyText(char* dst, const char* src) {
  size_t n = strlen(src);
  if (n > kMaxText) n = kMaxText;
  memcpy(dst, src, n);
  dst[n] = 0;
  return (uint8_t)n;
}

ScreenContent screenText(const char* top, uint8_t icon, const char* main) {
  ScreenContent c;
  c.topLen = copyText(c.top, top);
  c.topKind = c.topLen ? TOP_TEXT : TOP_EMPTY;
  c.icon = icon;
  c.mainLen = copyText(c.main, main);
  return c;
}

bool FrameParser::feed(uint8_t b) {
  switch (state_) {
    case WAIT_START:
      if (b == kStartMarker) state_ = WAIT_LENGTH;
      return false;
    case WAIT_LENGTH:
      len_ = b;
      idx_ = 0;
      state_ = len_ == 0 ? WAIT_START : WAIT_PAYLOAD;
      return false;
    case WAIT_PAYLOAD:
      buf_[idx_++] = b;
      if (idx_ >= len_) state_ = WAIT_CHECKSUM;
      return false;
    case WAIT_CHECKSUM: {
      state_ = WAIT_START;
      uint8_t cs = 0;
      for (uint16_t i = 0; i < len_; i++) cs ^= buf_[i];
      return cs == b;
    }
  }
  return false;
}

size_t encodeFrame(const uint8_t* payload, uint8_t len, uint8_t* out) {
  out[0] = kStartMarker;
  out[1] = len;
  uint8_t cs = 0;
  for (uint8_t i = 0; i < len; i++) {
    out[2 + i] = payload[i];
    cs ^= payload[i];
  }
  out[2 + len] = cs;
  return (size_t)len + 3;
}

size_t encodeHello(uint8_t sliders, const uint8_t* kinds, uint8_t buttons, uint8_t* out) {
  uint8_t p[4 + kMaxButtons];
  p[0] = MSG_HELLO;
  p[1] = kProtocolVersion;
  p[2] = sliders;
  p[3] = buttons;
  for (uint8_t i = 0; i < buttons && i < kMaxButtons; i++) p[4 + i] = kinds[i];
  return encodeFrame(p, (uint8_t)(4 + buttons), out);
}

size_t encodeAlive(uint8_t* out) {
  const uint8_t p[] = {MSG_ALIVE};
  return encodeFrame(p, 1, out);
}

size_t encodeSlider(uint8_t index, uint16_t value, uint8_t* out) {
  const uint8_t p[] = {MSG_SLIDER, index, (uint8_t)(value >> 8), (uint8_t)(value & 0xFF)};
  return encodeFrame(p, sizeof p, out);
}

size_t encodeButton(uint8_t index, bool pressed, uint8_t* out) {
  const uint8_t p[] = {MSG_BUTTON, index, (uint8_t)(pressed ? 1 : 0)};
  return encodeFrame(p, sizeof p, out);
}

// Reads a length-prefixed text slot at payload[i]; advances i. False on overrun.
static bool readText(const uint8_t* p, uint8_t len, uint16_t& i, char* dst, uint8_t& dstLen) {
  if (i >= len) return false;
  uint8_t n = p[i++];
  if (n > kMaxText || i + n > len) return false;
  memcpy(dst, p + i, n);
  dst[n] = 0;
  dstLen = n;
  i += n;
  return true;
}

bool decodeScreen(const uint8_t* p, uint8_t len, uint8_t& button, uint8_t& transition, ScreenContent& out) {
  if (len < 5 || p[0] != MSG_SCREEN) return false;
  ScreenContent c;
  button = p[1];
  transition = p[2];
  c.layout = p[3];
  c.topKind = p[4];
  uint16_t i = 5;
  if (c.topKind == TOP_TEXT) {
    if (!readText(p, len, i, c.top, c.topLen)) return false;
  } else if (c.topKind == TOP_DOTS) {
    if (i + 2 > len) return false;
    c.dotsSelected = p[i++];
    c.dotsCount = p[i++];
  } else {
    c.topKind = TOP_EMPTY;
  }
  if (i + 2 > len) return false;
  c.icon = p[i++];
  c.iconFlags = p[i++];
  if (!readText(p, len, i, c.main, c.mainLen)) return false;
  if (transition > TRANSITION_SLIDE_RIGHT) transition = TRANSITION_NONE;  // unknown → none
  out = c;
  return true;
}

Link::Link(const LinkConfig& config, LinkSink& sink) : config_(config), sink_(sink) {
  if (config_.buttons > kMaxButtons) config_.buttons = kMaxButtons;
}

void Link::begin(uint32_t now) {
  lastRx_ = now;
  sendHello(now);
}

bool Link::isLedButton(uint8_t b) const {
  return b < config_.buttons && (config_.kinds[b] == KIND_LED || config_.kinds[b] == KIND_LED_PAIR);
}

bool Link::isScreenButton(uint8_t b) const { return b < config_.buttons && config_.kinds[b] == KIND_SCREEN; }

void Link::onPayload(const uint8_t* p, uint8_t len, uint32_t now) {
  if (len < 1) return;
  lastRx_ = now;
  const uint8_t type = p[0];
  if (!hostAlive_) {
    hostAlive_ = true;
    // The PC is back (or here for the first time): announce the hardware so it re-sends
    // everything. A HelloRequest gets its Hello below instead.
    if (type != MSG_HELLO_REQUEST) sendHello(now);
  }
  switch (type) {
    case MSG_HELLO_REQUEST:
      sendHello(now);
      break;
    case MSG_LED:
      if (len >= 3 && isLedButton(p[1])) {
        if (holding_) {
          held_[p[1]].hasLed = true;
          held_[p[1]].led = p[2];
        } else {
          sink_.applyLed(p[1], p[2]);
        }
      }
      break;
    case MSG_SCREEN: {
      uint8_t button, transition;
      ScreenContent c;
      if (!decodeScreen(p, len, button, transition, c) || !isScreenButton(button)) break;
      if (holding_) {
        Held& h = held_[button];
        // A newer instant update keeps a held slide (spec §2.3).
        if (!(h.hasScreen && transition == TRANSITION_NONE)) h.transition = transition;
        h.hasScreen = true;
        h.screen = c;
      } else {
        sink_.applyScreen(button, transition, c, now);
      }
      break;
    }
    case MSG_BEGIN:
      holding_ = true;
      holdStart_ = now;
      break;
    case MSG_COMMIT:
      if (holding_) applyHeld(now);
      break;
    default:
      break;  // Heartbeat (its arrival is the point) and unknown types
  }
}

void Link::applyHeld(uint32_t now) {
  holding_ = false;
  for (uint8_t b = 0; b < config_.buttons; b++) {
    Held& h = held_[b];
    if (h.hasLed) sink_.applyLed(b, h.led);
    if (h.hasScreen) sink_.applyScreen(b, h.transition, h.screen, now);
    h = Held();
  }
}

void Link::tick(uint32_t now) {
  if (holding_ && now - holdStart_ >= config_.commitFallbackMs) applyHeld(now);
  if (hostAlive_ && now - lastRx_ >= config_.hostTimeoutMs) {
    hostAlive_ = false;
    holding_ = false;
    for (uint8_t b = 0; b < config_.buttons; b++) held_[b] = Held();
    sink_.hostLost();
  }
  if (now - lastTx_ >= config_.keepaliveMs) {
    uint8_t f[4];
    sink_.send(f, encodeAlive(f));
    lastTx_ = now;
  }
}

void Link::sendFrame(const uint8_t* payload, uint8_t len, uint32_t now) {
  uint8_t f[kMaxPayload + 3];
  sink_.send(f, encodeFrame(payload, len, f));
  lastTx_ = now;
}

void Link::sendHello(uint32_t now) {
  uint8_t f[4 + kMaxButtons + 3];
  sink_.send(f, encodeHello(config_.sliders, config_.kinds, config_.buttons, f));
  lastTx_ = now;
}

void Link::sendSlider(uint8_t index, uint16_t value, uint32_t now) {
  const uint8_t p[] = {MSG_SLIDER, index, (uint8_t)(value >> 8), (uint8_t)(value & 0xFF)};
  sendFrame(p, sizeof p, now);
}

void Link::sendButton(uint8_t index, bool pressed, uint32_t now) {
  const uint8_t p[] = {MSG_BUTTON, index, (uint8_t)(pressed ? 1 : 0)};
  sendFrame(p, sizeof p, now);
}

}  // namespace volco
```

In `test_begin_sends_hello`, `lastTx_` starts at 0 and `begin(0)` sends Hello at t=0, so `tick(999)` must not send Alive. `test_alive_when_idle_for_a_second` covers this.

- [ ] **Step 4: Add the CI job.** In `.github/workflows/build.yml`, add a job beside `test`:

```yaml
  firmware-test:
    name: Firmware Tests
    runs-on: ubuntu-latest
    steps:
    - name: Checkout Code
      uses: actions/checkout@v7

    - name: Setup Python
      uses: actions/setup-python@v5
      with:
        python-version: '3.12'

    - name: Install PlatformIO
      run: pip install platformio==6.1.18

    - name: Native tests
      working-directory: firmware
      run: pio test -e native
```

- [ ] **Step 5: Commit and verify in CI.** Both jobs must pass. If `pio` fails to find the golden file, the test fails with "golden/frames.txt not found"; fix the path logic in `golden_frames.h`, not the test. Message: `feat(firmware): pure protocol library with native tests in CI`.

---

### Task 14: Firmware right-to-left text (`volco_bidi`)

**Files:**
- Create: `firmware/lib/volco_bidi/src/volco_bidi.h`, `volco_bidi.cpp`
- Create: `firmware/test/test_bidi/test_main.cpp`

**Interfaces**
- **Produces:** `size_t volco::bidiVisual(const char* in, size_t len, char* out, size_t cap, bool* rtl)`. It converts UTF-8 logical order to visual order and returns the bytes written (NUL-terminated when it fits). `*rtl` is set to the paragraph direction. `cap` must be at least `len + 1`; mirrored brackets keep the same byte length.

- [ ] **Step 1: Write the failing tests** `firmware/test/test_bidi/test_main.cpp`:

```cpp
#include <unity.h>
#include <string.h>
#include "volco_bidi.h"

using volco::bidiVisual;

static void expectVisual(const char* logical, const char* visual, bool rtl) {
  char out[128];
  bool isRtl = !rtl;
  size_t n = bidiVisual(logical, strlen(logical), out, sizeof out, &isRtl);
  TEST_ASSERT_EQUAL(strlen(visual), n);
  TEST_ASSERT_EQUAL_STRING(visual, out);
  TEST_ASSERT_EQUAL(rtl, isRtl);
}

void setUp() {}
void tearDown() {}

void test_latin_only_is_unchanged() { expectVisual("Speakers", "Speakers", false); }
void test_empty() { expectVisual("", "", false); }
void test_no_strong_characters_is_ltr() { expectVisual("123 - 4", "123 - 4", false); }
void test_hebrew_only_is_reversed() { expectVisual("שלום", "םולש", true); }

void test_hebrew_with_latin_in_brackets() {
  // רמקולים (USB Audio) → visual "(USB Audio) םילוקמר"
  expectVisual("רמקולים (USB Audio)", "(USB Audio) םילוקמר", true);
}

void test_latin_paragraph_with_hebrew_word() { expectVisual("Mic מיקרופון", "Mic ןופורקימ", false); }
void test_time_inside_hebrew_keeps_its_order() { expectVisual("שעה 10:30", "10:30 העש", true); }
void test_fraction_at_the_start_of_hebrew() { expectVisual("1/2 כוס", "סוכ 1/2", true); }
void test_brackets_around_hebrew_are_mirrored() { expectVisual("(שלום)", "(םולש)", true); }

void test_hebrew_with_time_and_brackets() {
  // Review Focus #4: פגישה (10:30)
  expectVisual("פגישה (10:30)", "(10:30) השיגפ", true);
}

void test_latin_number_after_word() { expectVisual("Volume 10:30", "Volume 10:30", false); }

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_latin_only_is_unchanged);
  RUN_TEST(test_empty);
  RUN_TEST(test_no_strong_characters_is_ltr);
  RUN_TEST(test_hebrew_only_is_reversed);
  RUN_TEST(test_hebrew_with_latin_in_brackets);
  RUN_TEST(test_latin_paragraph_with_hebrew_word);
  RUN_TEST(test_time_inside_hebrew_keeps_its_order);
  RUN_TEST(test_fraction_at_the_start_of_hebrew);
  RUN_TEST(test_brackets_around_hebrew_are_mirrored);
  RUN_TEST(test_hebrew_with_time_and_brackets);
  RUN_TEST(test_latin_number_after_word);
  return UNITY_END();
}
```

- [ ] **Step 2: Implement** `firmware/lib/volco_bidi/src/volco_bidi.h`:

```cpp
#pragma once
// Logical → visual reordering for screen text (spec §4.4): a deliberately small subset of the
// Unicode bidi algorithm, enough for Hebrew device and preset names mixed with Latin and numbers.
#include <stddef.h>
#include <stdint.h>

namespace volco {
// Writes `in` (UTF-8, logical order) to `out` in visual (left-to-right drawing) order.
// Returns the bytes written; `out` is NUL-terminated when cap > that. *rtl = paragraph direction.
size_t bidiVisual(const char* in, size_t len, char* out, size_t cap, bool* rtl);
}  // namespace volco
```

`firmware/lib/volco_bidi/src/volco_bidi.cpp`:

```cpp
#include "volco_bidi.h"

namespace volco {
namespace {

constexpr int kMax = 128;  // code points; text slots are at most 60 bytes
enum Cls : uint8_t { L, R, EN, CS, N };

uint32_t decode(const char* s, size_t len, size_t& i) {
  uint8_t c = (uint8_t)s[i++];
  if (c < 0x80) return c;
  int extra = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : 0;
  uint32_t cp = c & (0x3F >> extra);
  for (int k = 0; k < extra && i < len; k++) cp = (cp << 6) | ((uint8_t)s[i++] & 0x3F);
  return cp;
}

size_t encode(uint32_t cp, char* out) {
  if (cp < 0x80) { out[0] = (char)cp; return 1; }
  if (cp < 0x800) { out[0] = (char)(0xC0 | (cp >> 6)); out[1] = (char)(0x80 | (cp & 0x3F)); return 2; }
  if (cp < 0x10000) {
    out[0] = (char)(0xE0 | (cp >> 12)); out[1] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[2] = (char)(0x80 | (cp & 0x3F));
    return 3;
  }
  out[0] = (char)(0xF0 | (cp >> 18)); out[1] = (char)(0x80 | ((cp >> 12) & 0x3F));
  out[2] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[3] = (char)(0x80 | (cp & 0x3F));
  return 4;
}

Cls classify(uint32_t cp) {
  if ((cp >= 0x5D0 && cp <= 0x5EA) || (cp >= 0x5F0 && cp <= 0x5F4)) return R;
  if (cp >= '0' && cp <= '9') return EN;
  if (cp == '.' || cp == ',' || cp == ':' || cp == '/') return CS;
  if ((cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z')) return L;
  if (cp >= 0xC0 && cp <= 0x24F && cp != 0xD7 && cp != 0xF7) return L;
  if (cp >= 0x370 && cp < 0x590) return L;  // Greek, Cyrillic, …
  return N;
}

uint32_t mirror(uint32_t cp) {
  switch (cp) {
    case '(': return ')'; case ')': return '(';
    case '[': return ']'; case ']': return '[';
    case '{': return '}'; case '}': return '{';
    case '<': return '>'; case '>': return '<';
    default: return cp;
  }
}

}  // namespace

size_t bidiVisual(const char* in, size_t len, char* out, size_t cap, bool* rtl) {
  uint32_t cp[kMax];
  Cls cls[kMax];
  int n = 0;
  for (size_t i = 0; i < len && n < kMax;) {
    cp[n] = decode(in, len, i);
    cls[n] = classify(cp[n]);
    n++;
  }

  // W4 (simplified): a separator between two digits joins the number.
  for (int i = 1; i + 1 < n; i++)
    if (cls[i] == CS && cls[i - 1] == EN && cls[i + 1] == EN) cls[i] = EN;

  // Paragraph direction: the first strong character.
  bool paraRtl = false;
  for (int i = 0; i < n; i++)
    if (cls[i] == L || cls[i] == R) { paraRtl = cls[i] == R; break; }
  if (rtl) *rtl = paraRtl;

  // Resolve every character to a direction (true = RTL).
  bool dir[kMax];
  bool lastStrongRtl = paraRtl;
  for (int i = 0; i < n; i++) {
    if (cls[i] == L || cls[i] == R) lastStrongRtl = cls[i] == R;
    if (cls[i] == L) dir[i] = false;
    else if (cls[i] == R) dir[i] = true;
    else if (cls[i] == EN) dir[i] = lastStrongRtl;   // a number joins the run before it
  }
  for (int i = 0; i < n; i++) {
    if (cls[i] != N && cls[i] != CS) continue;
    int j = i;
    while (j < n && (cls[j] == N || cls[j] == CS)) j++;
    bool before = i > 0 ? dir[i - 1] : paraRtl;
    bool after = j < n ? dir[j] : paraRtl;
    bool d = before == after ? before : paraRtl;
    for (int k = i; k < j; k++) dir[k] = d;
    i = j - 1;
  }

  // Runs of one direction; RTL runs are reversed (numbers inside keep their order) and mirrored,
  // and an RTL paragraph also reverses the order of its runs.
  int runStart[kMax], runEnd[kMax], runs = 0;
  for (int i = 0; i < n;) {
    int j = i;
    while (j < n && dir[j] == dir[i]) j++;
    runStart[runs] = i;
    runEnd[runs] = j;
    runs++;
    i = j;
  }

  size_t w = 0;
  auto put = [&](uint32_t c) {
    char buf[4];
    size_t k = encode(c, buf);
    for (size_t b = 0; b < k && w < cap; b++) out[w++] = buf[b];
  };
  for (int r = 0; r < runs; r++) {
    int idx = paraRtl ? runs - 1 - r : r;
    int s = runStart[idx], e = runEnd[idx];
    if (!dir[s]) {
      for (int i = s; i < e; i++) put(cp[i]);
      continue;
    }
    for (int i = e - 1; i >= s;) {
      if (cls[i] == EN) {
        int k = i;
        while (k - 1 >= s && cls[k - 1] == EN) k--;
        for (int m = k; m <= i; m++) put(cp[m]);
        i = k - 1;
      } else {
        put(mirror(cp[i]));
        i--;
      }
    }
  }
  if (w < cap) out[w] = 0;
  return w;
}

}  // namespace volco
```

Check `test_latin_paragraph_with_hebrew_word`: the space between `Mic` (L) and `מ` (R) takes the paragraph direction, L, so it stays in the L run. The R run "מיקרופון" is reversed. Output: "Mic ןופורקימ". This matches the test.

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(firmware): right-to-left reordering for screen text`.

---

### Task 15: Firmware screen state (`volco_screen_state`): transitions and burn-in protection

**Files:**
- Create: `firmware/lib/volco_screen_state/src/volco_screen_state.h`, `volco_screen_state.cpp`
- Create: `firmware/test/test_screen_state/test_main.cpp`

**Interfaces**
- **Consumes:** `volco::ScreenContent`, `TRANSITION_*` (Task 13).
- **Produces:**

```cpp
namespace volco {
class ScreenSlot {                      // one screen's content and transition
 public:
  explicit ScreenSlot(uint32_t transitionMs);
  void show(const ScreenContent& c, uint8_t transition, uint32_t now, bool panelsOff);
  const ScreenContent& current() const;
  const ScreenContent& previous() const;
  uint8_t transition() const;
  bool inTransition(uint32_t now) const;
  float progress(uint32_t now) const;   // 0..1 through the transition
  uint32_t scrollSince(uint32_t now) const;  // ms since the content settled (0 during a slide)
};
struct PanelConfig { bool dim, off, shift, swallow; uint32_t dimAfterMs, offAfterMs, shiftEveryMs; };
enum Power : uint8_t { POWER_NORMAL, POWER_DIM, POWER_OFF };
class Panels {
 public:
  explicit Panels(const PanelConfig& c);
  void activity(uint32_t now);
  Power power(uint32_t now) const;
  void shift(uint32_t now, int8_t& dx, int8_t& dy) const;
  bool filterButton(uint8_t button, bool pressed, uint32_t now);  // false = swallow
};
}
```

- [ ] **Step 1: Write the failing tests** `firmware/test/test_screen_state/test_main.cpp`:

```cpp
#include <unity.h>
#include "volco_protocol.h"
#include "volco_screen_state.h"

using namespace volco;

static const uint32_t MIN = 60000;
static PanelConfig all() { return PanelConfig{true, true, true, true, 2 * MIN, 30 * MIN, 3 * MIN}; }

void setUp() {}
void tearDown() {}

void test_instant_show_replaces_content() {
  ScreenSlot s(360);
  s.show(screenText("", 1, "A"), TRANSITION_NONE, 100, false);
  TEST_ASSERT_FALSE(s.inTransition(100));
  TEST_ASSERT_EQUAL_STRING("A", s.current().main);
  TEST_ASSERT_EQUAL(50, s.scrollSince(150));
}

void test_slide_runs_for_its_duration_then_settles() {
  ScreenSlot s(360);
  s.show(screenText("", 1, "A"), TRANSITION_NONE, 0, false);
  s.show(screenText("", 1, "B"), TRANSITION_SLIDE_LEFT, 1000, false);
  TEST_ASSERT_TRUE(s.inTransition(1000));
  TEST_ASSERT_FLOAT_WITHIN(0.01, 0.5, s.progress(1180));
  TEST_ASSERT_EQUAL_STRING("A", s.previous().main);
  TEST_ASSERT_EQUAL(0, s.scrollSince(1200));
  TEST_ASSERT_FALSE(s.inTransition(1360));
  TEST_ASSERT_EQUAL(40, s.scrollSince(1400));
}

void test_new_content_mid_slide_snaps() {
  // Review Focus #1: rapid presses never queue animations.
  ScreenSlot s(360);
  s.show(screenText("", 1, "A"), TRANSITION_NONE, 0, false);
  s.show(screenText("", 1, "B"), TRANSITION_SLIDE_LEFT, 1000, false);
  s.show(screenText("", 1, "C"), TRANSITION_SLIDE_LEFT, 1100, false);
  TEST_ASSERT_EQUAL_STRING("B", s.previous().main);     // the first slide snapped to its end
  TEST_ASSERT_EQUAL_STRING("C", s.current().main);
  TEST_ASSERT_FLOAT_WITHIN(0.01, 0.0, s.progress(1100));
}

void test_no_transition_while_panels_are_off() {
  ScreenSlot s(360);
  s.show(screenText("", 1, "B"), TRANSITION_SLIDE_LEFT, 1000, true);
  TEST_ASSERT_FALSE(s.inTransition(1000));
}

void test_dim_then_off_and_activity_wakes() {
  Panels p(all());
  p.activity(0);
  TEST_ASSERT_EQUAL(POWER_NORMAL, p.power(2 * MIN - 1));
  TEST_ASSERT_EQUAL(POWER_DIM, p.power(2 * MIN));
  TEST_ASSERT_EQUAL(POWER_OFF, p.power(30 * MIN));
  p.activity(30 * MIN + 5);
  TEST_ASSERT_EQUAL(POWER_NORMAL, p.power(30 * MIN + 6));
}

void test_disabled_dim_and_off_stay_normal() {
  PanelConfig c = all();
  c.dim = c.off = false;
  Panels p(c);
  TEST_ASSERT_EQUAL(POWER_NORMAL, p.power(60 * MIN));
}

void test_pixel_shift_cycles_within_one_pixel() {
  Panels p(all());
  int8_t dx, dy;
  p.shift(0, dx, dy);
  TEST_ASSERT_EQUAL(0, dx); TEST_ASSERT_EQUAL(0, dy);
  p.shift(3 * MIN, dx, dy);
  TEST_ASSERT_EQUAL(1, dx); TEST_ASSERT_EQUAL(0, dy);
  for (uint32_t k = 0; k < 20; k++) {
    p.shift(k * 3 * MIN, dx, dy);
    TEST_ASSERT_TRUE(dx >= -1 && dx <= 1 && dy >= -1 && dy <= 1);
  }
  PanelConfig c = all(); c.shift = false;
  Panels q(c);
  q.shift(3 * MIN, dx, dy);
  TEST_ASSERT_EQUAL(0, dx);
}

void test_wake_press_and_its_release_are_swallowed_only_when_off() {
  Panels p(all());
  p.activity(0);
  TEST_ASSERT_TRUE(p.filterButton(0, true, 3 * MIN));      // only dimmed: sent
  TEST_ASSERT_TRUE(p.filterButton(0, false, 3 * MIN));
  TEST_ASSERT_FALSE(p.filterButton(1, true, 40 * MIN));    // off: wakes, swallowed
  TEST_ASSERT_EQUAL(POWER_NORMAL, p.power(40 * MIN));
  TEST_ASSERT_FALSE(p.filterButton(1, false, 40 * MIN + 100));
  TEST_ASSERT_TRUE(p.filterButton(1, true, 40 * MIN + 200));
}

void test_wake_press_is_sent_when_swallowing_is_off() {
  PanelConfig c = all(); c.swallow = false;
  Panels p(c);
  p.activity(0);
  TEST_ASSERT_TRUE(p.filterButton(0, true, 40 * MIN));
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_instant_show_replaces_content);
  RUN_TEST(test_slide_runs_for_its_duration_then_settles);
  RUN_TEST(test_new_content_mid_slide_snaps);
  RUN_TEST(test_no_transition_while_panels_are_off);
  RUN_TEST(test_dim_then_off_and_activity_wakes);
  RUN_TEST(test_disabled_dim_and_off_stay_normal);
  RUN_TEST(test_pixel_shift_cycles_within_one_pixel);
  RUN_TEST(test_wake_press_and_its_release_are_swallowed_only_when_off);
  RUN_TEST(test_wake_press_is_sent_when_swallowing_is_off);
  return UNITY_END();
}
```

- [ ] **Step 2: Implement** `firmware/lib/volco_screen_state/src/volco_screen_state.h`:

```cpp
#pragma once
// Per-screen content/transition state and panel power (spec §2.3, §2.4). Pure: time is passed in.
#include <stdint.h>
#include "volco_protocol.h"

namespace volco {

// One screen: what it shows, what it showed before, and the transition between them.
class ScreenSlot {
 public:
  explicit ScreenSlot(uint32_t transitionMs) : transitionMs_(transitionMs) {}

  // New content. A slide still running snaps to its end first, so the new transition starts
  // from what the old one was heading to. While the panels are off, nothing animates.
  void show(const ScreenContent& c, uint8_t transition, uint32_t now, bool panelsOff);

  const ScreenContent& current() const { return current_; }
  const ScreenContent& previous() const { return previous_; }
  uint8_t transition() const { return transition_; }
  bool inTransition(uint32_t now) const {
    return transition_ != TRANSITION_NONE && now - start_ < transitionMs_;
  }
  float progress(uint32_t now) const;
  // The scroll clock: ms since the content settled (after its slide); 0 while sliding.
  uint32_t scrollSince(uint32_t now) const {
    return (int32_t)(now - settled_) < 0 ? 0 : now - settled_;
  }

 private:
  uint32_t transitionMs_;
  ScreenContent current_, previous_;
  uint8_t transition_ = TRANSITION_NONE;
  uint32_t start_ = 0, settled_ = 0;
};

struct PanelConfig {
  bool dim, off, shift, swallow;
  uint32_t dimAfterMs, offAfterMs, shiftEveryMs;
};

enum Power : uint8_t { POWER_NORMAL, POWER_DIM, POWER_OFF };

// Burn-in protection shared by every panel: idle dim/off, pixel shift, swallowing the wake press.
class Panels {
 public:
  explicit Panels(const PanelConfig& c) : c_(c) {}
  void activity(uint32_t now) { last_ = now; }
  Power power(uint32_t now) const;
  void shift(uint32_t now, int8_t& dx, int8_t& dy) const;
  // Call for every debounced button change. False = do not send it to the PC (spec §2.4).
  bool filterButton(uint8_t button, bool pressed, uint32_t now);

 private:
  PanelConfig c_;
  uint32_t last_ = 0;
  uint32_t swallowed_ = 0;  // bit per button whose wake press was swallowed
};

}  // namespace volco
```

`firmware/lib/volco_screen_state/src/volco_screen_state.cpp`:

```cpp
#include "volco_screen_state.h"

namespace volco {

void ScreenSlot::show(const ScreenContent& c, uint8_t transition, uint32_t now, bool panelsOff) {
  previous_ = current_;  // a running slide snaps: its target becomes the new "from"
  current_ = c;
  transition_ = panelsOff ? (uint8_t)TRANSITION_NONE : transition;
  start_ = now;
  settled_ = transition_ == TRANSITION_NONE ? now : now + transitionMs_;
}

float ScreenSlot::progress(uint32_t now) const {
  if (!inTransition(now)) return 1.0f;
  return (float)(now - start_) / (float)transitionMs_;
}

Power Panels::power(uint32_t now) const {
  uint32_t idle = now - last_;
  if (c_.off && idle >= c_.offAfterMs) return POWER_OFF;
  if (c_.dim && idle >= c_.dimAfterMs) return POWER_DIM;
  return POWER_NORMAL;
}

void Panels::shift(uint32_t now, int8_t& dx, int8_t& dy) const {
  static const int8_t kCycle[9][2] = {{0, 0}, {1, 0}, {1, 1}, {0, 1}, {-1, 1}, {-1, 0}, {-1, -1}, {0, -1}, {1, -1}};
  if (!c_.shift || c_.shiftEveryMs == 0) { dx = dy = 0; return; }
  uint32_t i = (now / c_.shiftEveryMs) % 9;
  dx = kCycle[i][0];
  dy = kCycle[i][1];
}

bool Panels::filterButton(uint8_t button, bool pressed, uint32_t now) {
  const uint32_t bit = button < 32 ? (1u << button) : 0;
  if (pressed) {
    bool swallow = c_.swallow && power(now) == POWER_OFF;
    activity(now);
    if (swallow) swallowed_ |= bit;
    return !swallow;
  }
  activity(now);
  if (swallowed_ & bit) {
    swallowed_ &= ~bit;
    return false;
  }
  return true;
}

}  // namespace volco
```

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(firmware): screen transitions and burn-in protection state`.

---

### Task 16: Firmware renderer (`volco_render`): canvas, text, layout 1, slide and shift

**Files:**
- Create: `firmware/lib/volco_render/src/volco_canvas.h`, `volco_canvas.cpp`, `volco_render.h`, `volco_render.cpp`. The generated `volco_icons.*` and `volco_fonts.*` from Task 8 are already in this folder.
- Create: `firmware/test/test_render/test_main.cpp`

**Interfaces**
- **Consumes:** `ScreenContent`, `kNoIcon` (Task 13); `bidiVisual` (Task 14); `kIconBits`, `kIconCount`, `Font`, `Glyph`, `kFontTop`, `kFontMain` (Task 8).
- **Produces:**

```cpp
namespace volco {
class Canvas {   // 128×64, SSD1306 page order: buf[page * 128 + x], bit 0 = top row of the page
 public:
  static constexpr int kWidth = 128, kHeight = 64;
  uint8_t buf[1024];
  void clear(); void set(int x, int y, bool on = true); bool get(int x, int y) const;
  void fillRect(int x, int y, int w, int h, bool on = true); void rect(int x, int y, int w, int h);
  void line(int x0, int y0, int x1, int y1, int thickness, bool on = true);
  void bitmap(int x, int y, int w, int h, const uint8_t* bits, int rowBytes);
  bool operator==(const Canvas& o) const;
};
struct ScrollTiming { uint32_t holdMs, endHoldMs; uint16_t speedPxS; };
const Glyph* findGlyph(const Font& f, uint32_t cp);
int textWidth(const Font& f, const char* utf8, size_t len);
void drawText(Canvas& c, const Font& f, int x, int baseline, const char* utf8, size_t len, int clipX0, int clipX1);
int scrollOffset(int distancePx, uint32_t sinceMs, const ScrollTiming& t);
void renderScreen(Canvas& c, const ScreenContent& s, uint32_t sinceMs, const ScrollTiming& t);
ScreenContent notConnectedScreen();
float easeInOut(float p);
void composeSlide(const Canvas& from, const Canvas& to, float progress, bool left, Canvas& out);
void shiftFrame(const Canvas& in, int dx, int dy, Canvas& out);
}
```

- [ ] **Step 1: Write the failing tests** `firmware/test/test_render/test_main.cpp`:

```cpp
#include <unity.h>
#include <string.h>
#include "volco_render.h"

using namespace volco;

static const ScrollTiming T = {2000, 1200, 30};

static bool anyIn(const Canvas& c, int x0, int y0, int x1, int y1) {   // [x0,x1) × [y0,y1)
  for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if (c.get(x, y)) return true;
  return false;
}
static int count(const Canvas& c) {
  int n = 0;
  for (int y = 0; y < 64; y++) for (int x = 0; x < 128; x++) n += c.get(x, y);
  return n;
}

void setUp() {}
void tearDown() {}

void test_canvas_page_layout() {
  Canvas c; c.clear();
  c.set(5, 9);
  TEST_ASSERT_EQUAL_HEX8(0x02, c.buf[1 * 128 + 5]);   // page 1, bit 1
  TEST_ASSERT_TRUE(c.get(5, 9));
  c.set(-1, 0); c.set(128, 0); c.set(0, 64);           // ignored
  TEST_ASSERT_EQUAL(1, count(c));
}

void test_glyph_lookup() {
  TEST_ASSERT_NOT_NULL(findGlyph(kFontMain, 'A'));
  TEST_ASSERT_NOT_NULL(findGlyph(kFontTop, 0x5E9));    // ש
  TEST_ASSERT_NULL(findGlyph(kFontMain, 0x4E00));
  int w = findGlyph(kFontMain, 'A')->advance + findGlyph(kFontMain, 'B')->advance;
  TEST_ASSERT_EQUAL(w, textWidth(kFontMain, "AB", 2));
}

void test_blank_screen_is_empty() {
  Canvas c;
  ScreenContent blank;
  renderScreen(c, blank, 0, T);
  TEST_ASSERT_EQUAL(0, count(c));
}

void test_slots_stay_in_their_areas() {
  Canvas c;
  renderScreen(c, screenText("MUTED", kNoIcon, ""), 0, T);
  TEST_ASSERT_TRUE(anyIn(c, 0, 0, 128, 16));
  TEST_ASSERT_FALSE(anyIn(c, 0, 16, 128, 64));

  renderScreen(c, screenText("", kNoIcon, "Speakers"), 0, T);
  TEST_ASSERT_TRUE(anyIn(c, 0, 46, 128, 64));
  TEST_ASSERT_FALSE(anyIn(c, 0, 0, 128, 46));

  renderScreen(c, screenText("", 1, ""), 0, T);
  TEST_ASSERT_TRUE(anyIn(c, 50, 18, 78, 46));
  TEST_ASSERT_FALSE(anyIn(c, 0, 0, 50, 64));
  TEST_ASSERT_FALSE(anyIn(c, 78, 0, 128, 64));
}

void test_muted_slash_and_unknown_icon() {
  Canvas plain, muted, unknown, generic;
  ScreenContent s = screenText("", 7, "");
  renderScreen(plain, s, 0, T);
  s.iconFlags = 1;
  renderScreen(muted, s, 0, T);
  TEST_ASSERT_FALSE(plain == muted);
  renderScreen(unknown, screenText("", 200, ""), 0, T);
  renderScreen(generic, screenText("", 0, ""), 0, T);
  TEST_ASSERT_TRUE(unknown == generic);
}

void test_dots_fill_the_selected_one() {
  Canvas c;
  ScreenContent s;
  s.topKind = TOP_DOTS; s.dotsSelected = 1; s.dotsCount = 3;
  renderScreen(c, s, 0, T);
  // 3 dots: total width 26, start x = 64 - 13 = 51; the second dot is at x 61..66, rows 5..10.
  for (int y = 5; y < 11; y++) for (int x = 61; x < 67; x++) TEST_ASSERT_TRUE(c.get(x, y));
  TEST_ASSERT_FALSE(c.get(53, 7));   // inside the first, outlined dot
}

void test_scroll_offset_timing() {
  TEST_ASSERT_EQUAL(0, scrollOffset(60, 0, T));
  TEST_ASSERT_EQUAL(0, scrollOffset(60, 1999, T));
  TEST_ASSERT_EQUAL(30, scrollOffset(60, 3000, T));
  TEST_ASSERT_EQUAL(60, scrollOffset(60, 4100, T));       // scrolled 2000 ms, holding at the end
  TEST_ASSERT_EQUAL(0, scrollOffset(60, 5200, T));        // cycle = 2000 + 2000 + 1200
}

void test_long_text_scrolls_and_is_clipped() {
  Canvas a, b;
  ScreenContent s = screenText("", kNoIcon, "Speakers (Realtek High Definition Audio)");
  renderScreen(a, s, 0, T);
  renderScreen(b, s, 4000, T);
  TEST_ASSERT_FALSE(a == b);
  TEST_ASSERT_FALSE(anyIn(a, 0, 0, 2, 64));
  TEST_ASSERT_FALSE(anyIn(a, 126, 0, 128, 64));
}

void test_slide_composition() {
  Canvas from, to, out;
  from.clear(); to.clear();
  from.fillRect(64, 0, 1, 64);    // a column at x 64 in "from"
  to.fillRect(0, 0, 1, 64);       // a column at x 0 in "to"
  composeSlide(from, to, 0.0f, true, out);  TEST_ASSERT_TRUE(out == from);
  composeSlide(from, to, 1.0f, true, out);  TEST_ASSERT_TRUE(out == to);
  composeSlide(from, to, 0.5f, true, out);  // halfway: from's x 64 is at 0, to's x 0 is at 64
  TEST_ASSERT_TRUE(out.get(0, 10));
  TEST_ASSERT_TRUE(out.get(64, 10));
}

void test_shift_moves_pixels() {
  Canvas in, out;
  in.clear(); in.set(10, 10);
  shiftFrame(in, 1, 1, out);
  TEST_ASSERT_TRUE(out.get(11, 11));
  TEST_ASSERT_EQUAL(1, count(out));
  shiftFrame(in, -1, -1, out);
  TEST_ASSERT_TRUE(out.get(9, 9));
}

void test_not_connected_screen() {
  ScreenContent s = notConnectedScreen();
  TEST_ASSERT_EQUAL_STRING("NOT CONNECTED", s.top);
  TEST_ASSERT_EQUAL_STRING("Volco", s.main);
  TEST_ASSERT_EQUAL(kNoIcon, s.icon);
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_canvas_page_layout);
  RUN_TEST(test_glyph_lookup);
  RUN_TEST(test_blank_screen_is_empty);
  RUN_TEST(test_slots_stay_in_their_areas);
  RUN_TEST(test_muted_slash_and_unknown_icon);
  RUN_TEST(test_dots_fill_the_selected_one);
  RUN_TEST(test_scroll_offset_timing);
  RUN_TEST(test_long_text_scrolls_and_is_clipped);
  RUN_TEST(test_slide_composition);
  RUN_TEST(test_shift_moves_pixels);
  RUN_TEST(test_not_connected_screen);
  return UNITY_END();
}
```

- [ ] **Step 2: Implement** `firmware/lib/volco_render/src/volco_canvas.h` / `.cpp`:

```cpp
#pragma once
#include <stdint.h>
#include <string.h>

namespace volco {

// A 128×64 1-bit frame in the SSD1306's own page order (the same layout as U8g2's full buffer):
// byte buf[page * 128 + x] holds rows page*8 .. page*8+7 of column x, bit 0 at the top.
class Canvas {
 public:
  static constexpr int kWidth = 128, kHeight = 64;
  uint8_t buf[kWidth * kHeight / 8];

  void clear() { memset(buf, 0, sizeof buf); }
  void set(int x, int y, bool on = true) {
    if (x < 0 || y < 0 || x >= kWidth || y >= kHeight) return;
    uint8_t& b = buf[(y >> 3) * kWidth + x];
    if (on) b |= (uint8_t)(1u << (y & 7)); else b &= (uint8_t)~(1u << (y & 7));
  }
  bool get(int x, int y) const {
    if (x < 0 || y < 0 || x >= kWidth || y >= kHeight) return false;
    return buf[(y >> 3) * kWidth + x] & (1u << (y & 7));
  }
  void fillRect(int x, int y, int w, int h, bool on = true);
  void rect(int x, int y, int w, int h);
  void line(int x0, int y0, int x1, int y1, int thickness, bool on = true);
  // XBM bitmap (row-major, LSB = leftmost); only 1-bits are drawn.
  void bitmap(int x, int y, int w, int h, const uint8_t* bits, int rowBytes);
  bool operator==(const Canvas& o) const { return memcmp(buf, o.buf, sizeof buf) == 0; }
};

}  // namespace volco
```

```cpp
#include "volco_canvas.h"
#include <stdlib.h>

namespace volco {

void Canvas::fillRect(int x, int y, int w, int h, bool on) {
  for (int yy = y; yy < y + h; yy++)
    for (int xx = x; xx < x + w; xx++) set(xx, yy, on);
}

void Canvas::rect(int x, int y, int w, int h) {
  for (int i = 0; i < w; i++) { set(x + i, y); set(x + i, y + h - 1); }
  for (int i = 0; i < h; i++) { set(x, y + i); set(x + w - 1, y + i); }
}

void Canvas::line(int x0, int y0, int x1, int y1, int thickness, bool on) {
  int dx = abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
  int dy = -abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
  int err = dx + dy, half = thickness / 2;
  while (true) {
    fillRect(x0 - half, y0 - half, thickness, thickness, on);
    if (x0 == x1 && y0 == y1) break;
    int e2 = 2 * err;
    if (e2 >= dy) { err += dy; x0 += sx; }
    if (e2 <= dx) { err += dx; y0 += sy; }
  }
}

void Canvas::bitmap(int x, int y, int w, int h, const uint8_t* bits, int rowBytes) {
  for (int row = 0; row < h; row++)
    for (int col = 0; col < w; col++)
      if (bits[row * rowBytes + (col >> 3)] & (1u << (col & 7))) set(x + col, y + row);
}

}  // namespace volco
```

`firmware/lib/volco_render/src/volco_render.h`:

```cpp
#pragma once
// Layout 1 ("C nudged", spec §2.1) drawn into a Canvas, plus the slide and pixel shift.
#include <stddef.h>
#include <stdint.h>
#include "volco_canvas.h"
#include "volco_fonts.h"
#include "volco_icons.h"
#include "volco_protocol.h"

namespace volco {

struct ScrollTiming {
  uint32_t holdMs, endHoldMs;
  uint16_t speedPxS;
};

const Glyph* findGlyph(const Font& f, uint32_t cp);
int textWidth(const Font& f, const char* utf8, size_t len);
// Draws UTF-8 text (already in visual order) with its pen at x on `baseline`; only columns in
// [clipX0, clipX1) are touched. Missing glyphs draw nothing and take no space.
void drawText(Canvas& c, const Font& f, int x, int baseline, const char* utf8, size_t len, int clipX0, int clipX1);
// How far text wider than its area by `distancePx` has scrolled after showing for sinceMs.
int scrollOffset(int distancePx, uint32_t sinceMs, const ScrollTiming& t);
// Clears `c` and draws one screen. sinceMs drives scrolling (ScreenSlot::scrollSince).
void renderScreen(Canvas& c, const ScreenContent& s, uint32_t sinceMs, const ScrollTiming& t);
// The local screen shown at boot and after the PC goes silent (spec §2.4).
ScreenContent notConnectedScreen();
float easeInOut(float p);
// `left`: new content enters from the right (slide left); otherwise from the left.
void composeSlide(const Canvas& from, const Canvas& to, float progress, bool left, Canvas& out);
void shiftFrame(const Canvas& in, int dx, int dy, Canvas& out);

}  // namespace volco
```

`firmware/lib/volco_render/src/volco_render.cpp`:

```cpp
#include "volco_render.h"

#include <math.h>
#include "volco_bidi.h"

namespace volco {
namespace {

constexpr int kTextX = 2, kTextW = 124;   // text area: x 2..125
constexpr int kTopBaseline = 11, kMainBaseline = 60, kIconX = 50, kIconY = 18;

uint32_t nextCodepoint(const char* s, size_t len, size_t& i) {
  uint8_t c = (uint8_t)s[i++];
  if (c < 0x80) return c;
  int extra = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : 0;
  uint32_t cp = c & (0x3F >> extra);
  for (int k = 0; k < extra && i < len; k++) cp = (cp << 6) | ((uint8_t)s[i++] & 0x3F);
  return cp;
}

// One text line: reordered for right-to-left, centred when it fits, scrolling when it doesn't.
void drawLine(Canvas& c, const Font& f, const char* text, uint8_t len, int baseline, uint32_t since,
              const ScrollTiming& t) {
  if (len == 0) return;
  char vis[2 * kMaxText + 1];
  bool rtl = false;
  size_t n = bidiVisual(text, len, vis, sizeof vis, &rtl);
  int w = textWidth(f, vis, n);
  if (w <= kTextW) {
    drawText(c, f, kTextX + (kTextW - w) / 2, baseline, vis, n, 0, Canvas::kWidth);
    return;
  }
  int off = scrollOffset(w - kTextW + 6, since, t);
  // Left-to-right text starts at its left edge and scrolls left; right-to-left text starts at
  // its right edge (the start of the sentence) and scrolls right.
  int x = rtl ? kTextX + kTextW - w + off : kTextX - off;
  drawText(c, f, x, baseline, vis, n, kTextX, kTextX + kTextW);
}

void drawDots(Canvas& c, uint8_t selected, uint8_t count) {
  if (count == 0) return;
  if (count > 12) count = 12;
  int total = count * 10 - 4;
  int x0 = 64 - total / 2;
  for (int i = 0; i < count; i++) {
    if (i == selected) c.fillRect(x0 + i * 10, 5, 6, 6);
    else c.rect(x0 + i * 10, 5, 6, 6);
  }
}

}  // namespace

const Glyph* findGlyph(const Font& f, uint32_t cp) {
  int lo = 0, hi = (int)f.glyphCount - 1;
  while (lo <= hi) {
    int mid = (lo + hi) / 2;
    if (f.glyphs[mid].codepoint == cp) return &f.glyphs[mid];
    if (f.glyphs[mid].codepoint < cp) lo = mid + 1; else hi = mid - 1;
  }
  return nullptr;
}

int textWidth(const Font& f, const char* s, size_t len) {
  int w = 0;
  for (size_t i = 0; i < len;) {
    const Glyph* g = findGlyph(f, nextCodepoint(s, len, i));
    if (g) w += g->advance;
  }
  return w;
}

void drawText(Canvas& c, const Font& f, int x, int baseline, const char* s, size_t len, int clipX0, int clipX1) {
  for (size_t i = 0; i < len;) {
    const Glyph* g = findGlyph(f, nextCodepoint(s, len, i));
    if (!g) continue;
    const uint8_t* bits = f.bitmaps + g->offset;
    int rowBytes = (g->width + 7) / 8;
    for (int row = 0; row < g->height; row++)
      for (int col = 0; col < g->width; col++) {
        int px = x + g->xOffset + col;
        if (px < clipX0 || px >= clipX1) continue;
        if (bits[row * rowBytes + (col >> 3)] & (1u << (col & 7))) c.set(px, baseline + g->yOffset + row);
      }
    x += g->advance;
  }
}

int scrollOffset(int distance, uint32_t since, const ScrollTiming& t) {
  if (distance <= 0 || t.speedPxS == 0) return 0;
  uint32_t scrollMs = (uint32_t)distance * 1000u / t.speedPxS;
  uint32_t k = since % (t.holdMs + scrollMs + t.endHoldMs);
  if (k < t.holdMs) return 0;
  if (k < t.holdMs + scrollMs) return (int)((k - t.holdMs) * t.speedPxS / 1000u);
  return distance;
}

void renderScreen(Canvas& c, const ScreenContent& s, uint32_t since, const ScrollTiming& t) {
  c.clear();
  if (s.layout != 1) {  // a layout this firmware doesn't know: just the main text (spec §3.3)
    drawLine(c, kFontMain, s.main, s.mainLen, 36, since, t);
    return;
  }
  if (s.topKind == TOP_TEXT) drawLine(c, kFontTop, s.top, s.topLen, kTopBaseline, since, t);
  else if (s.topKind == TOP_DOTS) drawDots(c, s.dotsSelected, s.dotsCount);
  if (s.icon != kNoIcon) {
    uint8_t id = s.icon < kIconCount ? s.icon : 0;  // unknown icon → generic
    c.bitmap(kIconX, kIconY, kIconSize, kIconSize, kIconBits[id], kIconRowBytes);
    if (s.iconFlags & 1) {  // muted: a slash with a dark outline so it reads over filled shapes
      c.line(kIconX + 1, kIconY + 1, kIconX + 26, kIconY + 26, 5, false);
      c.line(kIconX + 1, kIconY + 1, kIconX + 26, kIconY + 26, 2, true);
    }
  }
  drawLine(c, kFontMain, s.main, s.mainLen, kMainBaseline, since, t);
}

ScreenContent notConnectedScreen() { return screenText("NOT CONNECTED", kNoIcon, "Volco"); }

float easeInOut(float p) {
  if (p <= 0) return 0;
  if (p >= 1) return 1;
  return p < 0.5f ? 4 * p * p * p : 1 - powf(-2 * p + 2, 3) / 2;
}

void composeSlide(const Canvas& from, const Canvas& to, float progress, bool left, Canvas& out) {
  int off = (int)lroundf(easeInOut(progress) * Canvas::kWidth);
  for (int x = 0; x < Canvas::kWidth; x++) {
    int sx = left ? x + off : x - off;
    const Canvas& src = left ? (sx < Canvas::kWidth ? from : to) : (sx >= 0 ? from : to);
    int col = left ? (sx < Canvas::kWidth ? sx : sx - Canvas::kWidth) : (sx >= 0 ? sx : sx + Canvas::kWidth);
    for (int page = 0; page < 8; page++) out.buf[page * Canvas::kWidth + x] = src.buf[page * Canvas::kWidth + col];
  }
}

void shiftFrame(const Canvas& in, int dx, int dy, Canvas& out) {
  for (int x = 0; x < Canvas::kWidth; x++) {
    int sx = x - dx;
    uint64_t column = 0;
    if (sx >= 0 && sx < Canvas::kWidth)
      for (int page = 0; page < 8; page++) column |= (uint64_t)in.buf[page * Canvas::kWidth + sx] << (8 * page);
    column = dy >= 0 ? column << dy : column >> -dy;
    for (int page = 0; page < 8; page++) out.buf[page * Canvas::kWidth + x] = (uint8_t)(column >> (8 * page));
  }
}

}  // namespace volco
```

If `test_dots_fill_the_selected_one` is off by one, check `drawDots` against the spec (6×6 squares, 10 px pitch, centred, rows 5–10) and fix the code, not the expectation.

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(firmware): layout 1 renderer with fonts, icons, scrolling, slide and shift`.

---

### Task 17: Firmware restructure on the new protocol (LED hardware works end-to-end)

**Files:**
- Rewrite: `firmware/src/hardware.h`, `firmware/src/config.h`, `firmware/src/main.cpp`
- Create: `firmware/src/debug_log.h`, `transport.h`, `transport.cpp`, `indicators.h`, `indicators.cpp`
- Create: `firmware/src/screens.h`, and `firmware/src/screens.cpp` as a **no-screen stub** (Task 18 replaces it)
- Modify: `.github/workflows/build.yml` (the firmware job also builds the ESP32 firmware)

**Interfaces**
- **Consumes:** `volco::Link`, `FrameParser`, `LinkSink`, `ScreenContent` (Task 13).
- **Produces** (`screens.h`, implemented for real in Task 18):

```cpp
void screens_init(uint32_t now);
void screens_show(uint8_t button, uint8_t transition, const volco::ScreenContent& content, uint32_t now);
void screens_host_lost(uint32_t now);
void screens_activity(uint32_t now);
bool screens_filter_button(uint8_t button, bool pressed, uint32_t now);  // false = swallow
void screens_tick(uint32_t now);
```

This task has no unit test of its own: `src/` is Arduino code. Its checks are:
- the ESP32 build in CI;
- the native libraries' tests, which stay green;
- the manual LED checklist at the end of this plan.

- [ ] **Step 1: Rewrite `firmware/src/hardware.h`:**

```cpp
#pragma once

#include <stdint.h>

// ============================================================
// Types for the hardware tables in config.h
// ============================================================
//
// The firmware knows nothing about what a button *does*. The PC sends each LED button a value
// (message 0x83) and each screen button what to draw (0x84); an Indicator says what this
// particular hardware has for a button, and Hello reports it to the PC.

enum IndicatorKind : uint8_t {
  IND_KIND_NONE,      // nothing to show
  IND_KIND_LED,       // one LED: lit for any value except 0
  IND_KIND_LED_PAIR,  // two LEDs: value 1 lights A, value 2 lights B, anything else neither
  IND_KIND_SCREEN,    // a 128x64 SSD1306 on an I2C bus
};

struct Indicator {
  IndicatorKind kind;
  uint8_t pinA;     // the LED (IND_KIND_LED) or LED A (IND_KIND_LED_PAIR)
  uint8_t pinB;     // LED B (IND_KIND_LED_PAIR only)
  bool activeLow;   // true when driving the pin LOW lights the LED
  uint8_t bus;      // screen: I2C bus 0 or 1
  uint8_t address;  // screen: 7-bit I2C address, 0x3C or 0x3D
};

struct ButtonDef {
  uint8_t pin;          // input pin; wired to GND when pressed (INPUT_PULLUP)
  Indicator indicator;  // what this button shows
};

// LED polarity: which pin level lights the LED.
#define ACTIVE_HIGH false
#define ACTIVE_LOW true

// Indicator constructors, for the BUTTONS table in config.h.
#define IND_NONE Indicator{IND_KIND_NONE, 0, 0, false, 0, 0}
#define IND_LED(pin, polarity) Indicator{IND_KIND_LED, (pin), 0, (polarity), 0, 0}
#define IND_LED_PAIR(pinA, pinB, polarity) Indicator{IND_KIND_LED_PAIR, (pinA), (pinB), (polarity), 0, 0}
#define IND_SCREEN(bus, address) Indicator{IND_KIND_SCREEN, 0, 0, false, (bus), (address)}
```

- [ ] **Step 2: Rewrite `firmware/src/config.h`.** The hardware tables keep the user's values; they become `constexpr` so `screens.cpp` can check them at compile time:

```cpp
#pragma once

#include "hardware.h"

// ============================================================
// Transport — uncomment exactly one
// ============================================================
#define TRANSPORT_SERIAL         // USB serial; Wi-Fi off
// #define TRANSPORT_WEBSOCKET   // Wi-Fi, WebSocket client of the app (SERVER_IP:SERVER_PORT)
// #define TRANSPORT_UDP         // Wi-Fi, UDP to the app, listening on UDP_LOCAL_PORT
// #define TRANSPORT_LOCAL_DEBUG // no PC: frames dropped, input logged to serial

// ============================================================
// Wi-Fi — only used for TRANSPORT_WEBSOCKET and TRANSPORT_UDP
// ============================================================
#define WIFI_SSID "SSID"          // network name
#define WIFI_PASSWORD "PASSWORD"  // network password
#define SERVER_IP "192.168.1.10"  // the PC running Volco
#define SERVER_PORT 12345         // WebSocket port or UDP port on the PC
#define UDP_LOCAL_PORT 12346      // port this controller listens on for UDP from the PC

// ============================================================
// Serial
// ============================================================
#define SERIAL_BAUD 115200  // must match the baud rate on Volco's Connection page

// ============================================================
// Hardware layout
// ============================================================
//
// The order of each table is the input number the PC sees: the first slider is slider 0, the
// first button is button 0, and so on. The Volco settings window lists them in the same order.

// Analog sliders: one ADC pin per slider.
static constexpr uint8_t SLIDER_PINS[] = {33, 35, 32, 36, 34};

// Buttons: { input pin, indicator }.
//   IND_NONE                             nothing to show
//   IND_LED(pin, polarity)               one LED: on for any value except 0
//   IND_LED_PAIR(pinA, pinB, polarity)   two LEDs: 1 lights A, 2 lights B
//   IND_SCREEN(bus, address)             an SSD1306 screen: bus 0 or 1, address 0x3C or 0x3D
// polarity is ACTIVE_HIGH (HIGH lights the LED) or ACTIVE_LOW (LOW does).
static constexpr ButtonDef BUTTONS[] = {
    {4, IND_LED(12, ACTIVE_LOW)},
    {14, IND_LED(21, ACTIVE_LOW)},
    {5, IND_LED_PAIR(18, 19, ACTIVE_HIGH)},
};

// ============================================================
// Screens — only used when a button in BUTTONS is IND_SCREEN
// ============================================================
// I2C buses. A bus is only started when a screen uses it, so these pins are free otherwise.
#define I2C0_SDA 25                 // bus 0 data pin
#define I2C0_SCL 26                 // bus 0 clock pin
#define I2C1_SDA 27                 // bus 1 data pin
#define I2C1_SCL 13                 // bus 1 clock pin
#define I2C_CLOCK_HZ 400000         // bus speed; SSD1306 modules usually tolerate 1000000

#define SCREEN_BRIGHTNESS 255           // normal brightness (contrast), 0-255
#define SCREEN_TRANSITION_MS 360        // how long a slide between two screens takes
#define SCREEN_SCROLL_HOLD_MS 2000      // long text waits this long before it scrolls
#define SCREEN_SCROLL_SPEED_PX_S 30     // how fast long text scrolls
#define SCREEN_SCROLL_END_HOLD_MS 1200  // pause at the end of the text before jumping back

// Burn-in protection. Any button press or slider move counts as activity.
#define SCREEN_DIM_ENABLED true                     // dim the screens when idle
#define SCREEN_DIM_AFTER_MS (2 * 60 * 1000)         //   ...after this long without activity
#define SCREEN_DIM_BRIGHTNESS 8                     //   ...to this brightness (0-255)
#define SCREEN_OFF_ENABLED true                     // turn the screens off when idle
#define SCREEN_OFF_AFTER_MS (30 * 60 * 1000)        //   ...after this long without activity
#define SCREEN_PIXEL_SHIFT_ENABLED true             // nudge content by 1 px now and then
#define SCREEN_PIXEL_SHIFT_EVERY_MS (3 * 60 * 1000) //   ...this often
#define SCREEN_WAKE_SWALLOWS_PRESS true             // a press that wakes dark screens isn't sent to the PC

// ============================================================
// Connection to the PC
// ============================================================
#define HOST_TIMEOUT_MS 3000      // no frame from the PC for this long: LEDs off, "not connected" screen
#define KEEPALIVE_MS 1000         // send Alive when nothing else was sent for this long
#define COMMIT_FALLBACK_MS 500    // apply held updates if a Begin's Commit never arrives
#define RECONNECT_INTERVAL_MS 3000  // how often the link is checked and, if needed, restarted

// ============================================================
// Input tuning
// ============================================================
#define DEBOUNCE_MS 30              // a button's reading must be stable this long before a change is reported
#define SLIDER_READ_INTERVAL_MS 10  // how often sliders are sampled
#define FILTER_WEIGHT 0.2           // slider smoothing (0-1); lower is smoother but slower
#define SLIDER_DEADBAND 25          // a smoothed slider must move more than this (ADC units) to be reported
```

- [ ] **Step 3: Create the split source files.** `firmware/src/debug_log.h`:

```cpp
#pragma once
#include <Arduino.h>
// Debug lines go to Serial. On TRANSPORT_SERIAL they share the port with frames; the PC's parser
// only acts on valid 0xAA frames, so log lines are harmlessly skipped.
#define DEBUG_LOG(msg) Serial.println(msg)
```

`firmware/src/transport.h`:

```cpp
#pragma once
#include <stddef.h>
#include <stdint.h>

// One implementation per TRANSPORT_* (transport.cpp). Nothing else knows which link is in use.
void transport_init();                                // bring the link up (and Serial for logging)
bool transport_connected();                           // whether the link is usable
void transport_send(const uint8_t* data, size_t len); // one frame
int transport_receive(uint8_t* buf, size_t maxLen);   // non-blocking; 0 when nothing is waiting
void transport_reconnect();                           // restore a lost link (no-op where it manages itself)
```

Create `firmware/src/transport.cpp`: move the four transport blocks from the old `main.cpp` verbatim, including the `#if defined(TRANSPORT_…)` includes and the `#error` fallback. Add `#include <Arduino.h>`, `#include "config.h"`, `#include "debug_log.h"` and `#include "transport.h"`. Then make these changes (spec §3.5):

```cpp
// TRANSPORT_SERIAL — transport_init():
  WiFi.disconnect(true, true);
  WiFi.mode(WIFI_OFF);
  // A preset change is a ~560-byte burst and a screen redraw blocks the loop for ~30 ms:
  // the default 256-byte UART buffer could overflow.
  Serial.setRxBufferSize(1024);
  Serial.begin(SERIAL_BAUD);

// TRANSPORT_WEBSOCKET:
static uint8_t wsBuf[1024];
// … in wsEvent, case WStype_BIN:
      if (wsBufLen + length <= sizeof(wsBuf)) {
        memcpy(wsBuf + wsBufLen, payload, length);
        wsBufLen += length;
      } else {
        DEBUG_LOG("[DBG] WebSocket receive buffer full; message dropped");
      }

// TRANSPORT_UDP — whole datagrams, served out of their own buffer:
static uint8_t udpPacket[1024];
static size_t udpLen = 0, udpPos = 0;

int transport_receive(uint8_t* buf, size_t maxLen) {
  if (udpPos >= udpLen) {
    int size = udpClient.parsePacket();
    if (size <= 0) return 0;
    if (size > (int)sizeof(udpPacket)) DEBUG_LOG("[DBG] UDP packet larger than the buffer; truncated");
    udpLen = udpClient.read(udpPacket, sizeof(udpPacket));
    udpPos = 0;
  }
  size_t n = min(udpLen - udpPos, maxLen);
  memcpy(buf, udpPacket + udpPos, n);
  udpPos += n;
  return (int)n;
}
```

`firmware/src/indicators.h` / `.cpp`: move `writeLed`, `showIndicator` and `initIndicator` from the old `main.cpp`, with their comments. Both switches gain `case IND_KIND_SCREEN: break;`, since screens are drawn by `screens.cpp`. The header:

```cpp
#pragma once
#include <stdint.h>
#include "hardware.h"

// value 0 = off; LED: lit for any other value; LED pair: 1 lights A, 2 lights B.
void showIndicator(const Indicator& ind, uint8_t value);
// Sets the pins up and shows "off" (a different pin level per polarity).
void initIndicator(const Indicator& ind);
```

`firmware/src/screens.h`:

```cpp
#pragma once
#include <stdint.h>
#include "volco_protocol.h"

// The screen subsystem (screens.cpp). With no IND_SCREEN buttons every call is a no-op and I2C
// is never touched.
void screens_init(uint32_t now);
void screens_show(uint8_t button, uint8_t transition, const volco::ScreenContent& content, uint32_t now);
void screens_host_lost(uint32_t now);    // every screen shows the local "not connected" screen
void screens_activity(uint32_t now);     // a slider moved (button changes go through filter_button)
bool screens_filter_button(uint8_t button, bool pressed, uint32_t now);  // false = swallow
void screens_tick(uint32_t now);         // every loop pass: power, then redraw at most one panel
```

`firmware/src/screens.cpp` (stub until Task 18):

```cpp
#include "screens.h"

// Placeholder until the screen renderer lands (Task 18): this build has no screens.
void screens_init(uint32_t) {}
void screens_show(uint8_t, uint8_t, const volco::ScreenContent&, uint32_t) {}
void screens_host_lost(uint32_t) {}
void screens_activity(uint32_t) {}
bool screens_filter_button(uint8_t, bool, uint32_t) { return true; }
void screens_tick(uint32_t) {}
```

- [ ] **Step 4: Rewrite `firmware/src/main.cpp`:**

```cpp
#include <Arduino.h>

#include "config.h"
#include "debug_log.h"
#include "indicators.h"
#include "screens.h"
#include "transport.h"
#include "volco_protocol.h"

// ============================================================
// Table sizes (the tables themselves are in config.h)
// ============================================================
static constexpr uint8_t NUM_SLIDERS = sizeof(SLIDER_PINS) / sizeof(SLIDER_PINS[0]);
static constexpr uint8_t NUM_BUTTONS = sizeof(BUTTONS) / sizeof(BUTTONS[0]);
static_assert(NUM_BUTTONS <= volco::kMaxButtons, "Too many buttons for the protocol");

// ============================================================
// State
// ============================================================
static uint16_t lastSliderValues[NUM_SLIDERS];
static float smoothedValues[NUM_SLIDERS] = {0};
static unsigned long lastSliderReadTime = 0;

static bool buttonRawStates[NUM_BUTTONS];
static bool buttonReportedStates[NUM_BUTTONS];
static unsigned long buttonChangeTime[NUM_BUTTONS];

static unsigned long lastReconnectTime = 0;

// What Hello reports, derived from BUTTONS (wire values: 0 none, 1 LED, 2 LED pair, 3 screen).
static uint8_t kinds[NUM_BUTTONS];

// ============================================================
// The PC link: protocol state lives in lib/volco_protocol; this sink connects it to the hardware.
// ============================================================
class HardwareSink : public volco::LinkSink {
 public:
  void send(const uint8_t* frame, size_t len) override { transport_send(frame, len); }
  void applyLed(uint8_t button, uint8_t value) override { showIndicator(BUTTONS[button].indicator, value); }
  void applyScreen(uint8_t button, uint8_t transition, const volco::ScreenContent& c, uint32_t now) override {
    screens_show(button, transition, c, now);
  }
  void hostLost() override {
    DEBUG_LOG("[DBG] PC silent: LEDs off, screens show 'not connected'");
    for (uint8_t i = 0; i < NUM_BUTTONS; i++) showIndicator(BUTTONS[i].indicator, 0);
    screens_host_lost(millis());
  }
};

static HardwareSink sink;
static volco::FrameParser parser;
static volco::Link pcLink({NUM_SLIDERS, NUM_BUTTONS, kinds, HOST_TIMEOUT_MS, KEEPALIVE_MS, COMMIT_FALLBACK_MS}, sink);

// ============================================================
// Setup
// ============================================================
void setup() {
  for (uint8_t i = 0; i < NUM_SLIDERS; i++) pinMode(SLIDER_PINS[i], INPUT);

  for (uint8_t i = 0; i < NUM_BUTTONS; i++) {
    pinMode(BUTTONS[i].pin, INPUT_PULLUP);
    buttonRawStates[i] = HIGH;
    buttonReportedStates[i] = HIGH;
    buttonChangeTime[i] = 0;
    initIndicator(BUTTONS[i].indicator);
    kinds[i] = (uint8_t)BUTTONS[i].indicator.kind;  // IndicatorKind values are the wire values
  }

  analogReadResolution(12);
  for (uint8_t i = 0; i < NUM_SLIDERS; i++) {
    lastSliderValues[i] = analogRead(SLIDER_PINS[i]);
    smoothedValues[i] = lastSliderValues[i];
  }

  transport_init();
  screens_init(millis());
  pcLink.begin(millis());  // Hello: the ESP32 may have just reset because the PC opened the port
#if defined(TRANSPORT_LOCAL_DEBUG)
  DEBUG_LOG("[DBG] *** LOCAL DEBUG MODE — no host connection, verbose event logging ***");
#endif
  DEBUG_LOG("[DBG] Setup complete");
}

// ============================================================
// Main loop: sliders → buttons → receive → link → screens → connection. Never blocks for long.
// ============================================================
void loop() {
  const uint32_t now = millis();

  // 1. Sliders (every SLIDER_READ_INTERVAL_MS): EMA smoothing, then a deadband.
  if (now - lastSliderReadTime >= SLIDER_READ_INTERVAL_MS) {
    lastSliderReadTime = now;
    for (uint8_t i = 0; i < NUM_SLIDERS; i++) {
      uint16_t raw = analogRead(SLIDER_PINS[i]);
      smoothedValues[i] = raw * FILTER_WEIGHT + smoothedValues[i] * (1.0 - FILTER_WEIGHT);
      uint16_t filtered = (uint16_t)smoothedValues[i];
      int16_t delta = (int16_t)filtered - (int16_t)lastSliderValues[i];
      if (abs(delta) > SLIDER_DEADBAND) {
        lastSliderValues[i] = filtered;
        screens_activity(now);
        pcLink.sendSlider(i, filtered, now);
#if defined(TRANSPORT_LOCAL_DEBUG)
        Serial.printf("[SLIDER] idx=%d  filtered=%d  delta=%d\n", i, filtered, delta);
#endif
      }
    }
  }

  // 2. Buttons: debounce on timestamps, so a press is seen even when a redraw delayed this pass.
  for (uint8_t i = 0; i < NUM_BUTTONS; i++) {
    bool raw = digitalRead(BUTTONS[i].pin);
    if (raw != buttonRawStates[i]) {
      buttonRawStates[i] = raw;
      buttonChangeTime[i] = now;
    }
    if (now - buttonChangeTime[i] >= DEBOUNCE_MS && buttonRawStates[i] != buttonReportedStates[i]) {
      buttonReportedStates[i] = buttonRawStates[i];
      bool pressed = buttonReportedStates[i] == LOW;  // INPUT_PULLUP: LOW = pressed
      if (screens_filter_button(i, pressed, now)) pcLink.sendButton(i, pressed, now);
#if defined(TRANSPORT_LOCAL_DEBUG)
      Serial.printf("[BUTTON] idx=%d  state=%s\n", i, pressed ? "PRESSED" : "RELEASED");
#endif
    }
  }

  // 3. Receive: drain everything waiting (spec §3.5), frame by frame into the link.
  uint8_t buf[256];
  int n;
  while ((n = transport_receive(buf, sizeof buf)) > 0)
    for (int i = 0; i < n; i++)
      if (parser.feed(buf[i])) pcLink.onPayload(parser.payload(), parser.length(), now);

  // 4. Link: keepalive, host timeout, Commit fallback.
  pcLink.tick(now);

  // 5. Screens: power state, then redraw at most one panel.
  screens_tick(now);

  // 6. Connection.
  if (now - lastReconnectTime >= RECONNECT_INTERVAL_MS) {
    lastReconnectTime = now;
    if (!transport_connected()) {
      DEBUG_LOG("[DBG] Reconnecting...");
      transport_reconnect();
    }
  }
}
```

- [ ] **Step 5: Build the ESP32 firmware in CI.** Append to the `firmware-test` job in `build.yml`:

```yaml
    - name: Build firmware (ESP32)
      working-directory: firmware
      run: pio run -e denky32
```

- [ ] **Step 6: Commit and verify in CI.** All three jobs (tests, firmware tests, firmware build) must pass. Message: `refactor(firmware): split into transport/indicators/screens on the new protocol`.

---

### Task 18: Firmware screens (`screens.cpp` with U8g2)

**Files:**
- Rewrite: `firmware/src/screens.cpp`

**Interfaces**
- **Consumes:** `volco::Canvas`, `renderScreen`, `composeSlide`, `shiftFrame`, `notConnectedScreen`, `ScrollTiming` (Task 16); `ScreenSlot`, `Panels`, `PanelConfig` (Task 15); the settings in `config.h` (Task 17).
- **Produces:** the real implementation of `screens.h` (Task 17).

Checks: the ESP32 build in CI, plus the "after wiring screens" manual checklist. All logic worth unit-testing is already in the libraries.

- [ ] **Step 1: Write `firmware/src/screens.cpp`:**

```cpp
#include "screens.h"

#include <Arduino.h>
#include <U8g2lib.h>
#include <Wire.h>
#include <string.h>

#include "config.h"
#include "debug_log.h"
#include "volco_render.h"
#include "volco_screen_state.h"

namespace {

constexpr uint8_t NUM_BUTTONS = sizeof(BUTTONS) / sizeof(BUTTONS[0]);

constexpr uint8_t countScreens(int bus = -1) {
  uint8_t n = 0;
  for (const ButtonDef& b : BUTTONS)
    if (b.indicator.kind == IND_KIND_SCREEN && (bus < 0 || b.indicator.bus == bus)) n++;
  return n;
}

constexpr bool screensValid() {
  for (const ButtonDef& b : BUTTONS)
    if (b.indicator.kind == IND_KIND_SCREEN &&
        (b.indicator.bus > 1 || (b.indicator.address != 0x3C && b.indicator.address != 0x3D)))
      return false;
  return true;
}

constexpr bool addressesUnique() {
  for (uint8_t i = 0; i < NUM_BUTTONS; i++)
    for (uint8_t j = i + 1; j < NUM_BUTTONS; j++) {
      const Indicator& a = BUTTONS[i].indicator;
      const Indicator& b = BUTTONS[j].indicator;
      if (a.kind == IND_KIND_SCREEN && b.kind == IND_KIND_SCREEN && a.bus == b.bus && a.address == b.address)
        return false;
    }
  return true;
}

constexpr uint8_t NUM_SCREENS = countScreens();
static_assert(screensValid(), "IND_SCREEN(bus, address): bus must be 0 or 1, address 0x3C or 0x3D");
static_assert(countScreens(0) <= 2 && countScreens(1) <= 2, "At most two screens per I2C bus");
static_assert(addressesUnique(), "Two screens share the same bus and address");

constexpr uint32_t kFrameMs = 20;  // at most one redraw per panel every 20 ms

struct Screen {
  uint8_t button = 0;
  U8G2* panel = nullptr;
  volco::ScreenSlot slot{SCREEN_TRANSITION_MS};
  volco::Canvas sent;          // the frame last pushed to the panel: unchanged frames cost no I2C
  uint32_t lastRender = 0;
};

Screen screens[NUM_SCREENS > 0 ? NUM_SCREENS : 1];
int8_t screenOf[NUM_BUTTONS];  // button → index into screens, or -1

volco::Panels panels({SCREEN_DIM_ENABLED, SCREEN_OFF_ENABLED, SCREEN_PIXEL_SHIFT_ENABLED,
                      SCREEN_WAKE_SWALLOWS_PRESS, SCREEN_DIM_AFTER_MS, SCREEN_OFF_AFTER_MS,
                      SCREEN_PIXEL_SHIFT_EVERY_MS});
const volco::ScrollTiming kScroll = {SCREEN_SCROLL_HOLD_MS, SCREEN_SCROLL_END_HOLD_MS, SCREEN_SCROLL_SPEED_PX_S};
volco::Power lastPower = volco::POWER_NORMAL;
uint8_t nextPanel = 0;

// Scratch frames, static to keep ~3 KB off the loop task's stack.
volco::Canvas frameA, frameB, frameOut;

void render(Screen& s, uint32_t now) {
  if (s.slot.inTransition(now)) {
    volco::renderScreen(frameA, s.slot.previous(), 0, kScroll);
    volco::renderScreen(frameB, s.slot.current(), 0, kScroll);
    volco::composeSlide(frameA, frameB, s.slot.progress(now), s.slot.transition() != volco::TRANSITION_SLIDE_RIGHT, frameOut);
  } else {
    volco::renderScreen(frameOut, s.slot.current(), s.slot.scrollSince(now), kScroll);
  }
  int8_t dx, dy;
  panels.shift(now, dx, dy);
  if (dx || dy) {
    volco::shiftFrame(frameOut, dx, dy, frameA);
    frameOut = frameA;
  }
  if (frameOut == s.sent) return;
  s.sent = frameOut;
  memcpy(s.panel->getBufferPtr(), frameOut.buf, sizeof frameOut.buf);  // same page layout as U8g2's full buffer
  s.panel->sendBuffer();
}

}  // namespace

void screens_init(uint32_t now) {
  for (uint8_t b = 0; b < NUM_BUTTONS; b++) screenOf[b] = -1;
  if (NUM_SCREENS == 0) return;  // no screens: never touch I2C (GPIO 21 may be an LED)

  bool bus0 = false, bus1 = false;
  uint8_t n = 0;
  for (uint8_t b = 0; b < NUM_BUTTONS; b++) {
    const Indicator& ind = BUTTONS[b].indicator;
    if (ind.kind != IND_KIND_SCREEN) continue;
    Screen& s = screens[n];
    s.button = b;
    screenOf[b] = (int8_t)n;
    if (ind.bus == 0) {
      if (!bus0) { Wire.setPins(I2C0_SDA, I2C0_SCL); bus0 = true; }
      s.panel = new U8G2_SSD1306_128X64_NONAME_F_HW_I2C(U8G2_R0, U8X8_PIN_NONE);
    } else {
      if (!bus1) { Wire1.setPins(I2C1_SDA, I2C1_SCL); bus1 = true; }
      s.panel = new U8G2_SSD1306_128X64_NONAME_F_2ND_HW_I2C(U8G2_R0, U8X8_PIN_NONE);
    }
    s.panel->setI2CAddress(ind.address << 1);  // U8g2 takes the 8-bit form
    s.panel->setBusClock(I2C_CLOCK_HZ);
    s.panel->begin();
    s.panel->setContrast(SCREEN_BRIGHTNESS);
    s.slot.show(volco::notConnectedScreen(), volco::TRANSITION_NONE, now, false);
    memset(s.sent.buf, 0xFF, sizeof s.sent.buf);  // force the first push
    n++;
  }
  panels.activity(now);
  DEBUG_LOG("[DBG] Screens: " + String(NUM_SCREENS));
}

void screens_show(uint8_t button, uint8_t transition, const volco::ScreenContent& content, uint32_t now) {
  if (button >= NUM_BUTTONS || screenOf[button] < 0) return;
  screens[screenOf[button]].slot.show(content, transition, now, panels.power(now) == volco::POWER_OFF);
}

void screens_host_lost(uint32_t now) {
  for (uint8_t i = 0; i < NUM_SCREENS; i++)
    screens[i].slot.show(volco::notConnectedScreen(), volco::TRANSITION_NONE, now, false);
}

void screens_activity(uint32_t now) {
  if (NUM_SCREENS > 0) panels.activity(now);
}

bool screens_filter_button(uint8_t button, bool pressed, uint32_t now) {
  if (NUM_SCREENS == 0) return true;
  return panels.filterButton(button, pressed, now);
}

void screens_tick(uint32_t now) {
  if (NUM_SCREENS == 0) return;

  const volco::Power power = panels.power(now);
  if (power != lastPower) {
    for (uint8_t i = 0; i < NUM_SCREENS; i++) {
      screens[i].panel->setPowerSave(power == volco::POWER_OFF ? 1 : 0);
      if (power != volco::POWER_OFF)
        screens[i].panel->setContrast(power == volco::POWER_DIM ? SCREEN_DIM_BRIGHTNESS : SCREEN_BRIGHTNESS);
    }
    lastPower = power;
  }
  if (power == volco::POWER_OFF) return;

  // Redraw at most one panel per pass (~25-30 ms of I2C at 400 kHz), round-robin, so one pass
  // never blocks the loop for longer than one panel's redraw (spec §4.6).
  for (uint8_t k = 0; k < NUM_SCREENS; k++) {
    uint8_t i = (uint8_t)((nextPanel + k) % NUM_SCREENS);
    Screen& s = screens[i];
    if (now - s.lastRender < kFrameMs) continue;
    s.lastRender = now;
    nextPanel = (uint8_t)((i + 1) % NUM_SCREENS);
    render(s, now);
    break;
  }
}
```

- [ ] **Step 2: Prove the screen path compiles.** The user's `config.h` has no screens, so the code above is compiled but dead in the default build. Add a second, build-only environment to `platformio.ini`:

```ini
; Build-only check that the screen code compiles (2 screens on bus 0). Not for flashing.
[env:denky32_screens_check]
extends = env:denky32
build_flags =
    ${env:denky32.build_flags}
    -DVOLCO_SCREENS_CHECK
```

and in `config.h`, make the `BUTTONS` table:

```cpp
#ifdef VOLCO_SCREENS_CHECK  // CI-only: proves the screen code compiles (see platformio.ini)
static constexpr ButtonDef BUTTONS[] = {
    {4, IND_SCREEN(0, 0x3C)},
    {14, IND_SCREEN(0, 0x3D)},
    {5, IND_LED_PAIR(18, 19, ACTIVE_HIGH)},
};
#else
static constexpr ButtonDef BUTTONS[] = {
    {4, IND_LED(12, ACTIVE_LOW)},
    {14, IND_LED(21, ACTIVE_LOW)},
    {5, IND_LED_PAIR(18, 19, ACTIVE_HIGH)},
};
#endif
```

Add `pio run -e denky32_screens_check` as another step of the CI `firmware-test` job.

- [ ] **Step 3: Commit and verify in CI.** Message: `feat(firmware): SSD1306 screens with slides, scrolling, dim/off and pixel shift`.

---

### Task 19: Web simulator speaks the new protocol (LED and screen controllers)

**Files:**
- Rewrite: `tools/simulator/index.html`. It loads the generated `icons.js` from Task 8 from the same folder, so it still works when opened from disk.

**Interfaces**
- **Consumes:** the §3 protocol; `window.VOLCO_ICONS` (Task 8).
- **Produces:** a page with:
  - a **controller type** picker: LED controller (5 sliders; LED, LED, LED pair) or Screen controller (5 sliders; 4 screens);
  - Hello at connect and on HelloRequest;
  - Alive when idle;
  - the 3 s host timeout, after which it shows "not connected" and sends Hello on the next frame;
  - Begin/Commit with the 500 ms fallback;
  - screens rendering layout 1, with scrolling, the synchronized slide and the muted slash;
  - a frame log.

There's no automated test (it's a manual tool); the manual checklist uses it.

- [ ] **Step 1: Replace `tools/simulator/index.html` with:**

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Volco Simulator</title>
  <script src="https://cdn.tailwindcss.com"></script>
  <script src="icons.js"></script>
  <style>
    .slider-vertical { appearance: slider-vertical; width: 8px; height: 200px; padding: 0 5px; }
    .led { width: 12px; height: 12px; border-radius: 50%; background: #374151; }
    .led.on { background: #22c55e; box-shadow: 0 0 8px #22c55e; }
    canvas.oled { width: 256px; height: 128px; image-rendering: pixelated; background: #05070a; border-radius: 4px; }
  </style>
</head>
<body class="bg-gray-900 text-gray-100 min-h-screen p-6">
  <div class="max-w-5xl mx-auto bg-gray-800 p-6 rounded-2xl border border-gray-700 space-y-6">
    <h1 class="text-2xl font-bold text-blue-400">Volco Simulator</h1>

    <div class="flex flex-wrap gap-4 items-end">
      <label class="text-xs text-gray-400">Host<br>
        <input id="host" value="localhost" class="bg-gray-700 border border-gray-600 rounded px-3 py-2"></label>
      <label class="text-xs text-gray-400">Port<br>
        <input id="port" type="number" value="8765" class="w-24 bg-gray-700 border border-gray-600 rounded px-3 py-2"></label>
      <label class="text-xs text-gray-400">Controller type<br>
        <select id="type" class="bg-gray-700 border border-gray-600 rounded px-3 py-2">
          <option value="led">LED controller (current hardware)</option>
          <option value="screen">Screen controller</option>
        </select></label>
      <button id="connectBtn" class="bg-blue-600 hover:bg-blue-500 font-bold py-2 px-4 rounded">Connect</button>
      <label class="flex items-center gap-1.5 text-xs text-gray-400"><input id="autoReconnect" type="checkbox"> Auto-reconnect</label>
    </div>

    <div class="flex items-center gap-2 text-sm">
      <div id="statusDot" class="w-3 h-3 rounded-full bg-red-500"></div>
      <span id="statusText" class="text-gray-300">Disconnected</span>
    </div>

    <div class="flex flex-wrap gap-12 items-start">
      <div id="sliders" class="flex gap-6"></div>
      <div id="buttons" class="flex flex-col gap-4"></div>
    </div>

    <div>
      <div class="text-xs text-gray-400 mb-1">Frames (newest first)</div>
      <pre id="log" class="text-xs h-40 overflow-auto bg-gray-900 p-2 rounded"></pre>
    </div>
  </div>

<script>
// Volco wire protocol, controller side (docs/superpowers/specs/2026-10-02-oled-screens-design.md §3).
const TYPES = {
  led:    { sliders: 5, buttons: [1, 1, 2] },     // kinds: 1 LED, 2 LED pair, 3 screen
  screen: { sliders: 5, buttons: [3, 3, 3, 3] },
};
const MSG = { HELLO: 0x01, ALIVE: 0x02, SLIDER: 0x03, BUTTON: 0x04, HELLO_REQUEST: 0x81,
              HEARTBEAT: 0x82, LED: 0x83, SCREEN: 0x84, BEGIN: 0x85, COMMIT: 0x86 };
const NAMES = Object.fromEntries(Object.entries(MSG).map(([k, v]) => [v, k]));
const T = { hostTimeout: 3000, keepalive: 1000, commitFallback: 500, transition: 360,
            scrollHold: 2000, scrollSpeed: 30, scrollEndHold: 1200 };
const NOT_CONNECTED = { layout: 1, topKind: 1, top: 'NOT CONNECTED', icon: 255, flags: 0, main: 'Volco' };

const $ = id => document.getElementById(id);
let ws = null, reconnectTimer = null, type = 'led';
let lastTx = 0, lastRx = 0, hostAlive = false, holding = false, holdStart = 0, held = [];
let buttons = [];   // { kind, a, b, ctx, cur, prev, transition, start, settled }

// ── Framing ────────────────────────────────────────────────────────────────
function frameOf(payload) {
  let cs = 0;
  for (const b of payload) cs ^= b;
  return new Uint8Array([0xAA, payload.length, ...payload, cs]);
}
function hex(bytes) { return [...bytes].map(b => b.toString(16).padStart(2, '0')).join(' '); }
function log(dir, payload) {
  const line = `${dir} ${(NAMES[payload[0]] || '0x' + payload[0].toString(16)).padEnd(13)} ${hex(payload)}`;
  const el = $('log');
  el.textContent = (line + '\n' + el.textContent).split('\n').slice(0, 40).join('\n');
}
function send(payload) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  ws.send(frameOf(payload).buffer);
  lastTx = performance.now();
  if (payload[0] !== MSG.ALIVE) log('→', payload);
}
function sendHello() {
  const k = TYPES[type].buttons;
  send([MSG.HELLO, 1, TYPES[type].sliders, k.length, ...k]);
}

let rx = { state: 0, len: 0, buf: [] };
function feed(bytes) {
  for (const b of bytes) {
    if (rx.state === 0) { if (b === 0xAA) rx.state = 1; }
    else if (rx.state === 1) { rx.len = b; rx.buf = []; rx.state = b === 0 ? 0 : 2; }
    else if (rx.state === 2) { rx.buf.push(b); if (rx.buf.length >= rx.len) rx.state = 3; }
    else {
      rx.state = 0;
      let cs = 0;
      for (const x of rx.buf) cs ^= x;
      if (cs === b) handle(rx.buf, performance.now());
    }
  }
}

// ── Session (mirrors firmware/lib/volco_protocol Link) ─────────────────────
function decodeScreen(p) {
  const td = new TextDecoder();
  let i = 5;
  const c = { layout: p[3], topKind: p[4], top: '', dotsSelected: 0, dotsCount: 0, icon: 255, flags: 0, main: '' };
  const text = () => { const n = p[i++]; if (n === undefined || i + n > p.length) throw 0; const s = td.decode(new Uint8Array(p.slice(i, i + n))); i += n; return s; };
  try {
    if (c.topKind === 1) c.top = text();
    else if (c.topKind === 2) { c.dotsSelected = p[i++]; c.dotsCount = p[i++]; }
    if (i + 2 > p.length) return null;
    c.icon = p[i++]; c.flags = p[i++];
    c.main = text();
  } catch { return null; }
  return { button: p[1], transition: p[2] <= 2 ? p[2] : 0, content: c };
}

function handle(p, now) {
  log('←', p);
  lastRx = now;
  if (!hostAlive) {
    hostAlive = true;
    if (p[0] !== MSG.HELLO_REQUEST) sendHello();
  }
  const kind = i => buttons[i]?.kind;
  switch (p[0]) {
    case MSG.HELLO_REQUEST: sendHello(); break;
    case MSG.LED:
      if (kind(p[1]) === 1 || kind(p[1]) === 2) {
        if (holding) held[p[1]] = { ...held[p[1]], led: p[2] }; else applyLed(p[1], p[2]);
      }
      break;
    case MSG.SCREEN: {
      const s = decodeScreen(p);
      if (!s || kind(s.button) !== 3) break;
      if (holding) {
        const h = held[s.button] || {};
        const transition = h.screen && s.transition === 0 ? h.transition : s.transition;
        held[s.button] = { ...h, screen: s.content, transition };
      } else applyScreen(s.button, s.transition, s.content, now);
      break;
    }
    case MSG.BEGIN: holding = true; holdStart = now; break;
    case MSG.COMMIT: if (holding) applyHeld(now); break;
  }
}

function applyHeld(now) {
  holding = false;
  held.forEach((h, b) => {
    if (!h) return;
    if (h.led !== undefined) applyLed(b, h.led);
    if (h.screen) applyScreen(b, h.transition, h.screen, now);
  });
  held = [];
}

function applyLed(b, v) {
  const btn = buttons[b];
  if (btn.kind === 1) btn.a.classList.toggle('on', v !== 0);
  if (btn.kind === 2) { btn.a.classList.toggle('on', v === 1); btn.b.classList.toggle('on', v === 2); }
}

function applyScreen(b, transition, content, now) {
  const s = buttons[b];
  s.prev = s.cur;   // a running slide snaps: its target becomes the "from"
  s.cur = content;
  s.transition = transition;
  s.start = now;
  s.settled = transition ? now + T.transition : now;
}

function hostLost(now) {
  hostAlive = false;
  holding = false;
  held = [];
  buttons.forEach((btn, i) => {
    if (btn.kind === 1 || btn.kind === 2) applyLed(i, 0);
    if (btn.kind === 3) applyScreen(i, 0, NOT_CONNECTED, now);
  });
}

setInterval(() => {
  const now = performance.now();
  if (holding && now - holdStart >= T.commitFallback) applyHeld(now);
  if (hostAlive && now - lastRx >= T.hostTimeout) hostLost(now);
  if (ws && ws.readyState === WebSocket.OPEN && now - lastTx >= T.keepalive) send([MSG.ALIVE]);
}, 50);

// ── Screen rendering (layout 1, spec §2.1; text uses the browser's fonts) ───
function scrollOffset(dist, since) {
  const scroll = dist * 1000 / T.scrollSpeed, cycle = T.scrollHold + scroll + T.scrollEndHold;
  const k = since % cycle;
  return k < T.scrollHold ? 0 : k < T.scrollHold + scroll ? Math.round((k - T.scrollHold) * T.scrollSpeed / 1000) : dist;
}
function drawLine(ctx, text, font, baseline, since) {
  if (!text) return;
  ctx.font = font;
  const w = Math.ceil(ctx.measureText(text).width);
  const firstStrong = text.match(/[A-Za-zÀ-ɏ֐-׿]/);
  const rtl = firstStrong && /[֐-׿]/.test(firstStrong[0]);
  if (w <= 124) { ctx.fillText(text, 2 + Math.floor((124 - w) / 2), baseline); return; }
  const off = scrollOffset(w - 124 + 6, since);
  ctx.save(); ctx.beginPath(); ctx.rect(2, 0, 124, 64); ctx.clip();
  ctx.fillText(text, rtl ? 2 + 124 - w + off : 2 - off, baseline);
  ctx.restore();
}
function drawContent(ctx, c, since, dx) {
  ctx.save();
  ctx.translate(dx, 0);
  ctx.fillStyle = '#e8f0ff';
  if (c.layout !== 1) { drawLine(ctx, c.main, 'bold 14px Arimo, Arial, sans-serif', 36, since); ctx.restore(); return; }
  if (c.topKind === 1) drawLine(ctx, c.top, '10px Arimo, Arial, sans-serif', 11, since);
  if (c.topKind === 2) {
    const n = Math.min(c.dotsCount, 12), x0 = 64 - Math.floor((n * 10 - 4) / 2);
    for (let i = 0; i < n; i++) {
      if (i === c.dotsSelected) ctx.fillRect(x0 + i * 10, 5, 6, 6);
      else { ctx.strokeStyle = '#e8f0ff'; ctx.lineWidth = 1; ctx.strokeRect(x0 + i * 10 + 0.5, 5.5, 5, 5); }
    }
  }
  if (c.icon !== 255) {
    const rows = (window.VOLCO_ICONS || [])[c.icon < (window.VOLCO_ICONS || []).length ? c.icon : 0] || [];
    rows.forEach((row, y) => { for (let x = 0; x < row.length; x++) if (row[x] === '1') ctx.fillRect(50 + x, 18 + y, 1, 1); });
    if (c.flags & 1) {
      ctx.lineCap = 'square';
      ctx.strokeStyle = '#05070a'; ctx.lineWidth = 5; ctx.beginPath(); ctx.moveTo(51, 19); ctx.lineTo(76, 44); ctx.stroke();
      ctx.strokeStyle = '#e8f0ff'; ctx.lineWidth = 2; ctx.beginPath(); ctx.moveTo(51, 19); ctx.lineTo(76, 44); ctx.stroke();
    }
  }
  drawLine(ctx, c.main, 'bold 14px Arimo, Arial, sans-serif', 60, since);
  ctx.restore();
}
const ease = p => p < .5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2;
function renderScreens() {
  const now = performance.now();
  for (const s of buttons) {
    if (s.kind !== 3) continue;
    const ctx = s.ctx;
    ctx.fillStyle = '#05070a'; ctx.fillRect(0, 0, 128, 64);
    const t = now - s.start;
    if (s.transition && t < T.transition) {
      const off = Math.round(ease(t / T.transition) * 128), left = s.transition !== 2;
      drawContent(ctx, s.prev, 0, left ? -off : off);
      drawContent(ctx, s.cur, 0, left ? 128 - off : off - 128);
    } else {
      drawContent(ctx, s.cur, Math.max(0, now - s.settled), 0);
    }
  }
  requestAnimationFrame(renderScreens);
}
requestAnimationFrame(renderScreens);

// ── UI ─────────────────────────────────────────────────────────────────────
function throttle(fn, ms) {
  let last = 0, pending = null;
  return (...args) => {
    const now = Date.now();
    clearTimeout(pending);
    if (now - last >= ms) { last = now; fn(...args); }
    else pending = setTimeout(() => { last = Date.now(); fn(...args); }, ms - (now - last));
  };
}

function build() {
  const t = TYPES[type];
  $('sliders').replaceChildren();
  for (let i = 0; i < t.sliders; i++) {
    const col = document.createElement('div');
    col.className = 'flex flex-col items-center gap-2';
    const input = Object.assign(document.createElement('input'), { type: 'range', min: 0, max: 4095, value: 2048, className: 'slider-vertical' });
    const label = Object.assign(document.createElement('span'), { className: 'text-xs font-mono text-gray-400', textContent: `S${i}` });
    const value = Object.assign(document.createElement('span'), { className: 'text-xs font-mono text-blue-400', textContent: '2048' });
    const sendSlider = throttle(v => send([MSG.SLIDER, i, v >> 8, v & 255]), 20);
    input.addEventListener('input', e => { value.textContent = e.target.value; sendSlider(+e.target.value); });
    col.append(input, label, value);
    $('sliders').append(col);
  }

  $('buttons').replaceChildren();
  buttons = t.buttons.map((kind, i) => {
    const row = document.createElement('div');
    row.className = 'flex items-center gap-3';
    const btn = Object.assign(document.createElement('button'), {
      className: 'w-28 h-12 bg-gray-700 hover:bg-gray-600 active:bg-blue-600 rounded-lg border border-gray-600 font-bold text-sm',
      textContent: `Button ${i}` });
    let down = false;
    btn.addEventListener('pointerdown', () => { down = true; send([MSG.BUTTON, i, 1]); });
    const up = () => { if (down) { down = false; send([MSG.BUTTON, i, 0]); } };
    btn.addEventListener('pointerup', up);
    btn.addEventListener('pointerleave', up);
    row.append(btn);
    const entry = { kind, cur: NOT_CONNECTED, prev: NOT_CONNECTED, transition: 0, start: 0, settled: 0 };
    if (kind === 1 || kind === 2) {
      entry.a = Object.assign(document.createElement('div'), { className: 'led', title: kind === 1 ? 'LED' : 'LED A' });
      row.append(entry.a);
      if (kind === 2) {
        entry.b = Object.assign(document.createElement('div'), { className: 'led', title: 'LED B' });
        row.append(entry.b);
      }
    }
    if (kind === 3) {
      const canvas = Object.assign(document.createElement('canvas'), { width: 128, height: 64, className: 'oled' });
      entry.ctx = canvas.getContext('2d');
      row.append(canvas);
    }
    $('buttons').append(row);
    return entry;
  });
}

function setStatus(text, color) {
  $('statusText').textContent = text;
  $('statusDot').className = `w-3 h-3 rounded-full ${color}`;
}

function connect() {
  ws = new WebSocket(`ws://${$('host').value}:${$('port').value}/`);
  ws.binaryType = 'arraybuffer';
  ws.onopen = () => {
    setStatus('Connected', 'bg-green-500');
    $('connectBtn').textContent = 'Disconnect';
    hostAlive = false;
    lastRx = performance.now();
    sendHello();    // as the firmware does at boot
  };
  ws.onmessage = e => feed(new Uint8Array(e.data));
  ws.onclose = () => {
    ws = null;
    setStatus('Disconnected', 'bg-red-500');
    $('connectBtn').textContent = 'Connect';
    hostLost(performance.now());
    if ($('autoReconnect').checked && !reconnectTimer)
      reconnectTimer = setTimeout(() => { reconnectTimer = null; if (!ws) connect(); }, 3000);
  };
}

$('connectBtn').addEventListener('click', () => {
  if (ws) { clearTimeout(reconnectTimer); reconnectTimer = null; ws.close(); } else connect();
});
$('type').addEventListener('change', e => {
  if (ws) ws.close();
  type = e.target.value;
  build();
});
build();
</script>
</body>
</html>
```

- [ ] **Step 2: Smoke-check the page loads without script errors.** If a browser is available (the `claude-in-chrome` skill), open the file. Otherwise run `node -e` over the script block to check the syntax:

```bash
python3 - <<'PY'
import re, subprocess, pathlib
html = pathlib.Path("tools/simulator/index.html").read_text()
js = re.findall(r"<script>(.*?)</script>", html, re.S)[-1]
pathlib.Path("/tmp/claude-0/-root-dev-volco/e4b9ebc0-3e85-4074-9113-ad7a2dc0800e/scratchpad/sim.js").write_text(js)
PY
node --check /tmp/claude-0/-root-dev-volco/e4b9ebc0-3e85-4074-9113-ad7a2dc0800e/scratchpad/sim.js || echo "node not installed: skip"
```

- [ ] **Step 3: Commit.** Message: `feat(simulator): new protocol, LED and screen controller types`.

---

### Task 20: Docs, spec corrections, and the final CI run

**Files:**
- Rewrite: `firmware/README.md`
- Modify: `CLAUDE.md` (Architecture → firmware and wire protocol; Build & Test → firmware tests)
- Modify: `docs/superpowers/specs/2026-10-02-oled-screens-design.md` (implementation refinements)
- Modify: `docs/superpowers/specs/2026-10-01-volco-ux-redesign.md` (pointers in §4.3 and §7)

- [ ] **Step 1: Rewrite `firmware/README.md`.** Keep the current README's voice and structure: what the firmware does, files, `config.h`, `main.cpp`. The new content must include:

1. **"What the firmware does":**
   - it reports raw input and Hello;
   - it shows LED values and **draws screens** from descriptions the PC sends;
   - it runs scrolling, slides and burn-in protection locally;
   - it still knows nothing about what a button does.
2. **Files table:**
   - `src/` — `config.h`, `hardware.h`, `main.cpp`, `transport.*`, `indicators.*`, `screens.*`, `debug_log.h`;
   - `lib/` — `volco_protocol`, `volco_bidi`, `volco_screen_state`, `volco_render` (pure C++, tested with `pio test -e native`);
   - `assets/` — icons (`manifest.txt`, PNGs), fonts (Arimo, OFL);
   - `tools/` — `draw_icons.py`, `gen_assets.py`;
   - `test/` — native tests and the shared `golden/frames.txt`.
3. **`config.h`:** every setting, grouped as in the file, one line each. Copy the comments from Task 17's `config.h`. The indicator table gains `IND_SCREEN(bus, address)`.
4. **Wiring screens and pins:**
   - two hardware I2C buses × addresses `0x3C`/`0x3D`, so at most 4 screens; at least one module per bus needs its address resistor moved to `0x3D`;
   - free safe GPIOs on the current board: 13, 16, 17, 22, 23, 25, 26, 27;
   - avoid GPIO 21 while it drives an LED (it is Arduino's default SDA), and the strapping pins 0, 2, 15;
   - if slides look rough, try `I2C_CLOCK_HZ 1000000`.
5. **Migrating a button from LED to screen:** change its entry from `IND_LED(...)` to `IND_SCREEN(0, 0x3C)`, set the bus pins, and reflash. The app picks the change up from Hello. Nothing to change in Volco.
6. **Wire protocol:** the full §3.2 message table, the framing, the Screen body (§3.3), and the behaviour rules:
   - Hello at boot, on HelloRequest, and after a host timeout;
   - keepalive;
   - Begin/Commit and the 500 ms fallback;
   - kind mismatches ignored.
7. **Rendering:**
   - layout 1's geometry;
   - fonts and icons are generated by `tools/gen_assets.py`: `python3 firmware/tools/gen_assets.py`, which needs Pillow ≥ 10; commit its outputs;
   - to change an icon, edit its PNG (28×28, 1-bit) and re-run;
   - to add one, append a manifest row, add the PNG, and re-run.
8. **Main loop:**
   - order: sliders, buttons, drain receive, link tick, screens tick, connection check;
   - one panel redraw per pass;
   - the button debounce is timestamp-based.
9. **Tests:** `pio test -e native` runs the library tests (CI runs it too). The README still does not cover building, flashing or wiring diagrams.

- [ ] **Step 2: `CLAUDE.md`.**
- **Replace the "Wire Protocol" section** with the §3.2 table (types `0x01`–`0x04` from the controller, `0x81`–`0x86` from the PC). Point at `Communication/ControllerDisplay` (what each button shows) and `Runtime/ControllerLink` (Hello, keepalive, dedup, Begin/Commit bursts).
- **"ESP32 Firmware" section:**
  - mention the `lib/` libraries and `IND_SCREEN`;
  - replace "The firmware has no notion of mute or devices; it only reports raw input and renders `0x05` feedback values" with "…it reports raw input and its hardware (Hello), and renders the LED values and screen descriptions the PC sends".
- **`Volco.Core` bullets:** `Runtime/` gains `ControllerLink`; `Communication/` gains `ControllerDisplay` (replacing `ButtonFeedback`); add `Devices/DeviceDisplay`. The Config paragraph: schema v4 adds `devices` (nicknames and icons) and `controller` (the last Hello).
- **"Build & Test":**
  - the `firmware-test` CI job runs `pio test -e native` and builds both ESP32 environments;
  - `firmware/tools/gen_assets.py` regenerates icons and fonts.
- **`Volco.Windows` paragraph:** `DeviceRegistry` also reads each endpoint's form factor.
- **Simulator section:** controller types and screen rendering.

- [ ] **Step 3: Correct the OLED spec** where implementation refined it:
  - **§4.4 Fonts.** Generated with Pillow straight into a small Volco bitmap-font format (`lib/volco_render/src/volco_fonts.*`); no `otf2bdf`/`bdfconv`. Arimo's licence is the **SIL Open Font License 1.1**, not Apache 2.0.
  - **§4.1 and §4.3.** Rendering happens in Volco's own 1-bit canvas (`lib/volco_render`) and U8g2 is only the SSD1306 driver, so slides and pixel shift are buffer copies and the renderer is unit-tested natively. The file table gains `lib/volco_render/`.
  - **§6.3 Screen preview.** An Avalonia control using the same PNGs and Arimo at the same pixel sizes, not thresholded to 1-bit.
  - **§8.3.** The CI job also builds `denky32` and `denky32_screens_check`.
  - **Status line:** "Approved; implemented by `docs/superpowers/plans/2026-10-02-oled-screens.md`".

- [ ] **Step 4: UX redesign spec pointers.** In `2026-10-01-volco-ux-redesign.md` §4.3, replace the "OLED is deferred" bullet with "Superseded by `2026-10-02-oled-screens-design.md`, which replaces this protocol." Add the same pointer to the §7 OLED bullet.

- [ ] **Step 5: Final verification.** Run:
  - `grep -rn "ButtonFeedback\|0x05\|PostConnectRepush" src tests firmware/src tools CLAUDE.md`: no stale references, except the history in older specs and plans.
  - `git diff --name-only main... | grep '^src/Volco.Windows/'`: only `Audio/DeviceRegistry.cs` and `NativeMethods.txt`.

  Then push and watch CI until **Run Tests**, **Firmware Tests** (native tests and both ESP32 builds) pass.

- [ ] **Step 6: Commit.** Message: `docs: firmware README, CLAUDE.md and specs for the OLED screens`.

---

## Manual checklist (after CI is green; the user runs it on hardware)

**On the current LED hardware, flashed with the new firmware.** Repeat over Serial, then UDP, then WebSocket (change `TRANSPORT_*` and the app's Connection page).
- [ ] The app connects; Connection shows "5 sliders · 3 buttons (2 LED, 1 LED pair)"; button cards show "Light: LED", "Light: LED", "Light: LED pair".
- [ ] Mute and toggle LEDs follow presses, and change right after editing a button's action in Settings.
- [ ] Quitting Volco turns every LED off within ~3 s; restarting Volco relights them.
- [ ] A preset change switches all LEDs on the same instant.
- [ ] UDP only: an untouched controller stays "Connected" for a minute.

**Simulator** (`tools/simulator/index.html`, Volco on WebSocket):
- [ ] The LED controller type behaves like the hardware above.
- [ ] Screen controller type:
  - cards show "Light: Screen" and previews;
  - screens show the right icon, top and name;
  - a Hebrew nickname reads correctly;
  - long names scroll after 2 s.
- [ ] Next preset: all four screens slide left together. With `SynchronizedPresetTransitions = false` (temporary code change in `VolcoRuntimeOptions`), they slide one after another.

**After wiring screens** (set `IND_SCREEN` entries and the I2C pins):
- [ ] Layout matches the mockup; Hebrew and RTL scrolling; the muted slash.
- [ ] Synchronized slide on Next preset; rapid presses end on the right preset with no stutter queue.
- [ ] A 4-screen preset burst works over Serial, UDP and WebSocket.
- [ ] After 2 min idle the screens dim; after 30 min they turn off. The press that wakes them does nothing else. Pixel shift is barely visible.
- [ ] Sleep/resume the PC: screens show "NOT CONNECTED", then recover on their own.
- [ ] Slide smoothness at 400 kHz, then at 1 MHz.
