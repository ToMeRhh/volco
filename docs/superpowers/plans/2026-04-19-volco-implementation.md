# Volco Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows system tray app that receives slider/button events from an external hardware controller over serial or UDP and controls per-process and device-level audio.

**Architecture:** Monolithic WinUI 3 app with clear namespace separation — Config, Communication, Audio, Input, UI. A shared `ProtocolParser` decodes a framed binary protocol from either serial or UDP. Button presses are classified (single/double/long) before dispatch to configurable actions. Sliders map ADC values to audio volume via Windows Core Audio APIs.

**Tech Stack:** C# / .NET 8 / WinUI 3 (Windows App SDK), NAudio, AudioSwitcher.AudioApi.CoreAudio, H.NotifyIcon.WinUI, System.IO.Ports, xUnit for tests.

---

## File Structure

```
Volco.sln
src/Volco/
├── Volco.csproj
├── App.xaml / App.xaml.cs
├── Config/
│   ├── AppConfig.cs
│   └── ConfigManager.cs
├── Communication/
│   ├── ControllerMessage.cs
│   ├── IControllerConnection.cs
│   ├── ProtocolParser.cs
│   ├── SerialConnection.cs
│   └── UdpConnection.cs
├── Audio/
│   ├── IAudioManager.cs
│   ├── AudioManager.cs
│   └── SessionMonitor.cs
├── Input/
│   ├── ButtonPressDetector.cs
│   ├── SliderProcessor.cs
│   └── Actions/
│       ├── IButtonAction.cs
│       ├── ActionFactory.cs
│       ├── MuteSpecificDeviceAction.cs
│       ├── MuteCurrentOutputAction.cs
│       ├── MuteCurrentInputAction.cs
│       ├── ToggleOutputDeviceAction.cs
│       └── SetOutputDeviceAction.cs
├── UI/
│   ├── SettingsWindow.xaml / .xaml.cs
│   ├── ConnectionTab.xaml / .xaml.cs
│   ├── SlidersTab.xaml / .xaml.cs
│   ├── ButtonsTab.xaml / .xaml.cs
│   └── OutputDevicesTab.xaml / .xaml.cs
└── Assets/
    └── volco.ico
tests/Volco.Tests/
├── Volco.Tests.csproj
├── Communication/
│   └── ProtocolParserTests.cs
├── Input/
│   ├── ButtonPressDetectorTests.cs
│   └── SliderProcessorTests.cs
├── Config/
│   └── ConfigManagerTests.cs
└── Input/Actions/
    └── ActionFactoryTests.cs
```

---

### Task 1: Project Scaffolding

**Files:**
- Create: `Volco.sln`
- Create: `src/Volco/Volco.csproj`
- Create: `src/Volco/App.xaml`
- Create: `src/Volco/App.xaml.cs`
- Create: `tests/Volco.Tests/Volco.Tests.csproj`

- [ ] **Step 1: Create the WinUI 3 project**

Run (from repo root):
```bash
dotnet new winui3 -n Volco -o src/Volco --framework net8.0-windows10.0.22621.0
```

- [ ] **Step 2: Create the test project**

```bash
dotnet new xunit -n Volco.Tests -o tests/Volco.Tests
```

- [ ] **Step 3: Create the solution and wire up projects**

```bash
dotnet new sln -n Volco
dotnet sln add src/Volco/Volco.csproj
dotnet sln add tests/Volco.Tests/Volco.Tests.csproj
dotnet add tests/Volco.Tests/Volco.Tests.csproj reference src/Volco/Volco.csproj
```

- [ ] **Step 4: Add NuGet packages to the main project**

```bash
dotnet add src/Volco/Volco.csproj package NAudio
dotnet add src/Volco/Volco.csproj package AudioSwitcher.AudioApi.CoreAudio
dotnet add src/Volco/Volco.csproj package H.NotifyIcon.WinUI
dotnet add src/Volco/Volco.csproj package System.IO.Ports
```

- [ ] **Step 5: Verify the solution builds**

```bash
dotnet build Volco.sln
```
Expected: Build succeeded with 0 errors.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: scaffold WinUI 3 solution with test project and NuGet dependencies"
```

---

### Task 2: Configuration Data Model

**Files:**
- Create: `src/Volco/Config/AppConfig.cs`
- Create: `tests/Volco.Tests/Config/ConfigManagerTests.cs`

- [ ] **Step 1: Write the failing test for config serialization round-trip**

Create `tests/Volco.Tests/Config/ConfigManagerTests.cs`:

```csharp
using System.Text.Json;
using Volco.Config;

namespace Volco.Tests.Config;

public class ConfigManagerTests
{
    [Fact]
    public void AppConfig_RoundTrips_ThroughJson()
    {
        var config = new AppConfig
        {
            Connection = new ConnectionConfig
            {
                Type = ConnectionType.Serial,
                Serial = new SerialConfig { ComPort = "COM3", BaudRate = 9600 },
                Udp = new UdpConfig { ListenPort = 12345 }
            },
            Sliders = new List<SliderConfig>
            {
                new() { Index = 0, Type = SliderMappingType.Processes, Processes = new List<string> { "spotify.exe" }, Invert = false },
                new() { Index = 1, Type = SliderMappingType.Device, Invert = false },
                new() { Index = 2, Type = SliderMappingType.Unmapped, Invert = true },
            },
            Buttons = new List<ButtonConfig>
            {
                new()
                {
                    Index = 0,
                    SinglePress = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput },
                    DoublePress = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice },
                    LongPress = new ButtonActionConfig { Action = ButtonActionType.None }
                }
            },
            OutputDevices = new OutputDevicesConfig { Device1 = "Speakers", Device2 = "Headphones" }
        };

        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(config, options);
        var deserialized = JsonSerializer.Deserialize<AppConfig>(json, options);

        Assert.NotNull(deserialized);
        Assert.Equal(ConnectionType.Serial, deserialized.Connection.Type);
        Assert.Equal("COM3", deserialized.Connection.Serial.ComPort);
        Assert.Equal(3, deserialized.Sliders.Count);
        Assert.Equal(SliderMappingType.Unmapped, deserialized.Sliders[2].Type);
        Assert.True(deserialized.Sliders[2].Invert);
        Assert.Equal(ButtonActionType.MuteCurrentOutput, deserialized.Buttons[0].SinglePress.Action);
        Assert.Equal("Headphones", deserialized.OutputDevices.Device2);
    }

    [Fact]
    public void AppConfig_DefaultConfig_HasFiveSlidersAndThreeButtons()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(5, config.Sliders.Count);
        Assert.Equal(3, config.Buttons.Count);
        for (int i = 0; i < 5; i++)
            Assert.Equal(i, config.Sliders[i].Index);
        for (int i = 0; i < 3; i++)
            Assert.Equal(i, config.Buttons[i].Index);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Volco.Tests --filter "ConfigManagerTests" -v n
```
Expected: FAIL — `AppConfig` type does not exist.

- [ ] **Step 3: Implement the config data model**

Create `src/Volco/Config/AppConfig.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Volco.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectionType { Serial, Udp }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SliderMappingType { Processes, Device, Unmapped }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ButtonActionType
{
    None,
    MuteSpecificDevice,
    MuteCurrentOutput,
    MuteCurrentInput,
    ToggleOutputDevice,
    SetOutputDevice
}

public class SerialConfig
{
    public string ComPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 9600;
}

public class UdpConfig
{
    public int ListenPort { get; set; } = 12345;
}

public class ConnectionConfig
{
    public ConnectionType Type { get; set; } = ConnectionType.Serial;
    public SerialConfig Serial { get; set; } = new();
    public UdpConfig Udp { get; set; } = new();
}

public class SliderConfig
{
    public int Index { get; set; }
    public SliderMappingType Type { get; set; } = SliderMappingType.Device;
    public List<string> Processes { get; set; } = new();
    public bool Invert { get; set; }
}

public class ButtonActionConfig
{
    public ButtonActionType Action { get; set; } = ButtonActionType.None;
    public string? DeviceId { get; set; }
}

public class ButtonConfig
{
    public int Index { get; set; }
    public ButtonActionConfig SinglePress { get; set; } = new();
    public ButtonActionConfig DoublePress { get; set; } = new();
    public ButtonActionConfig LongPress { get; set; } = new();
}

public class OutputDevicesConfig
{
    public string Device1 { get; set; } = "";
    public string Device2 { get; set; } = "";
}

public class AppConfig
{
    public ConnectionConfig Connection { get; set; } = new();
    public List<SliderConfig> Sliders { get; set; } = new();
    public List<ButtonConfig> Buttons { get; set; } = new();
    public OutputDevicesConfig OutputDevices { get; set; } = new();

    public static AppConfig CreateDefault()
    {
        var config = new AppConfig();
        for (int i = 0; i < 5; i++)
            config.Sliders.Add(new SliderConfig { Index = i });
        for (int i = 0; i < 3; i++)
            config.Buttons.Add(new ButtonConfig { Index = i });
        return config;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Volco.Tests --filter "ConfigManagerTests" -v n
```
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Config/AppConfig.cs tests/Volco.Tests/Config/ConfigManagerTests.cs
git commit -m "feat: add configuration data model with JSON serialization"
```

---

### Task 3: ConfigManager (Load/Save/Change Notification)

**Files:**
- Create: `src/Volco/Config/ConfigManager.cs`
- Modify: `tests/Volco.Tests/Config/ConfigManagerTests.cs`

- [ ] **Step 1: Write failing tests for ConfigManager load/save**

Add to `tests/Volco.Tests/Config/ConfigManagerTests.cs`:

```csharp
public class ConfigManagerLoadSaveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigManagerLoadSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"volco-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaultConfig()
    {
        var manager = new ConfigManager(_configPath);
        manager.Load();

        Assert.Equal(5, manager.Config.Sliders.Count);
        Assert.Equal(3, manager.Config.Buttons.Count);
    }

    [Fact]
    public void Save_ThenLoad_PreservesConfig()
    {
        var manager = new ConfigManager(_configPath);
        manager.Load();
        manager.Config.Connection.Type = ConnectionType.Udp;
        manager.Config.OutputDevices.Device1 = "Speakers";
        manager.Save();

        var manager2 = new ConfigManager(_configPath);
        manager2.Load();

        Assert.Equal(ConnectionType.Udp, manager2.Config.Connection.Type);
        Assert.Equal("Speakers", manager2.Config.OutputDevices.Device1);
    }

    [Fact]
    public void Save_FiresConfigChangedEvent()
    {
        var manager = new ConfigManager(_configPath);
        manager.Load();
        bool eventFired = false;
        manager.ConfigChanged += (_, _) => eventFired = true;

        manager.Save();

        Assert.True(eventFired);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Volco.Tests --filter "ConfigManagerLoadSaveTests" -v n
```
Expected: FAIL — `ConfigManager` does not exist.

- [ ] **Step 3: Implement ConfigManager**

Create `src/Volco/Config/ConfigManager.cs`:

```csharp
using System.Text.Json;

namespace Volco.Config;

public class ConfigManager
{
    private readonly string _configPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppConfig Config { get; private set; } = new();
    public event EventHandler? ConfigChanged;

    public ConfigManager(string configPath)
    {
        _configPath = configPath;
    }

    public void Load()
    {
        if (File.Exists(_configPath))
        {
            var json = File.ReadAllText(_configPath);
            Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
        }
        else
        {
            Config = AppConfig.CreateDefault();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(Config, JsonOptions);
        File.WriteAllText(_configPath, json);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Volco.Tests --filter "ConfigManagerLoadSaveTests" -v n
```
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Config/ConfigManager.cs tests/Volco.Tests/Config/ConfigManagerTests.cs
git commit -m "feat: add ConfigManager with JSON load/save and change notification"
```

---

### Task 4: Wire Protocol — Message Types

**Files:**
- Create: `src/Volco/Communication/ControllerMessage.cs`

- [ ] **Step 1: Create message types**

Create `src/Volco/Communication/ControllerMessage.cs`:

```csharp
namespace Volco.Communication;

public enum MessageType : byte
{
    Slider = 0x01,
    Button = 0x02
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
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Volco/Communication/ControllerMessage.cs
git commit -m "feat: add controller message types for slider and button events"
```

---

### Task 5: Wire Protocol — ProtocolParser (TDD)

**Files:**
- Create: `src/Volco/Communication/ProtocolParser.cs`
- Create: `tests/Volco.Tests/Communication/ProtocolParserTests.cs`

- [ ] **Step 1: Write failing tests for ProtocolParser**

Create `tests/Volco.Tests/Communication/ProtocolParserTests.cs`:

```csharp
using Volco.Communication;

namespace Volco.Tests.Communication;

public class ProtocolParserTests
{
    private readonly ProtocolParser _parser = new();
    private readonly List<ControllerMessage> _received = new();

    public ProtocolParserTests()
    {
        _parser.MessageParsed += (_, msg) => _received.Add(msg);
    }

    private byte[] FramePayload(byte[] payload)
    {
        byte checksum = 0;
        foreach (var b in payload)
            checksum ^= b;
        var frame = new byte[payload.Length + 3];
        frame[0] = 0xAA;
        frame[1] = (byte)payload.Length;
        Array.Copy(payload, 0, frame, 2, payload.Length);
        frame[^1] = checksum;
        return frame;
    }

    [Fact]
    public void ParsesSliderMessage()
    {
        // Slider 2, value 512 (0x0200 big-endian)
        var payload = new byte[] { 0x01, 0x02, 0x02, 0x00 };
        _parser.FeedBytes(FramePayload(payload));

        Assert.Single(_received);
        var msg = Assert.IsType<SliderMessage>(_received[0]);
        Assert.Equal(2, msg.SliderIndex);
        Assert.Equal(512, msg.Value);
    }

    [Fact]
    public void ParsesButtonMessage()
    {
        // Button 1, pressed
        var payload = new byte[] { 0x02, 0x01, 0x01 };
        _parser.FeedBytes(FramePayload(payload));

        Assert.Single(_received);
        var msg = Assert.IsType<ButtonMessage>(_received[0]);
        Assert.Equal(1, msg.ButtonIndex);
        Assert.Equal(ButtonState.Pressed, msg.State);
    }

    [Fact]
    public void RejectsInvalidChecksum()
    {
        var payload = new byte[] { 0x02, 0x01, 0x01 };
        var frame = FramePayload(payload);
        frame[^1] ^= 0xFF; // corrupt checksum
        _parser.FeedBytes(frame);

        Assert.Empty(_received);
    }

    [Fact]
    public void ParsesMultipleMessagesInOneBuffer()
    {
        var frame1 = FramePayload(new byte[] { 0x01, 0x00, 0x00, 0x64 }); // slider 0, value 100
        var frame2 = FramePayload(new byte[] { 0x02, 0x02, 0x00 });       // button 2, released
        var combined = frame1.Concat(frame2).ToArray();
        _parser.FeedBytes(combined);

        Assert.Equal(2, _received.Count);
        Assert.IsType<SliderMessage>(_received[0]);
        Assert.IsType<ButtonMessage>(_received[1]);
    }

    [Fact]
    public void ParsesMessageSplitAcrossMultipleFeeds()
    {
        var frame = FramePayload(new byte[] { 0x01, 0x03, 0x03, 0xFF }); // slider 3, value 1023
        var half = frame.Length / 2;
        _parser.FeedBytes(frame[..half]);
        Assert.Empty(_received);
        _parser.FeedBytes(frame[half..]);

        Assert.Single(_received);
        var msg = Assert.IsType<SliderMessage>(_received[0]);
        Assert.Equal(3, msg.SliderIndex);
        Assert.Equal(1023, msg.Value);
    }

    [Fact]
    public void SkipsGarbageBytesBeforeStartMarker()
    {
        var garbage = new byte[] { 0x00, 0x55, 0xFF };
        var frame = FramePayload(new byte[] { 0x02, 0x00, 0x01 }); // button 0, pressed
        _parser.FeedBytes(garbage.Concat(frame).ToArray());

        Assert.Single(_received);
        Assert.IsType<ButtonMessage>(_received[0]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Volco.Tests --filter "ProtocolParserTests" -v n
```
Expected: FAIL — `ProtocolParser` does not exist.

- [ ] **Step 3: Implement ProtocolParser**

Create `src/Volco/Communication/ProtocolParser.cs`:

```csharp
namespace Volco.Communication;

public class ProtocolParser
{
    private const byte StartMarker = 0xAA;
    private readonly List<byte> _buffer = new();

    public event EventHandler<ControllerMessage>? MessageParsed;

    public void FeedBytes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _buffer.Add(b);
            TryParse();
        }
    }

    private void TryParse()
    {
        while (true)
        {
            // Find start marker
            var startIndex = _buffer.IndexOf(StartMarker);
            if (startIndex < 0)
            {
                _buffer.Clear();
                return;
            }
            if (startIndex > 0)
                _buffer.RemoveRange(0, startIndex);

            // Need at least: marker + length
            if (_buffer.Count < 2)
                return;

            int payloadLength = _buffer[1];
            int frameLength = 2 + payloadLength + 1; // marker + length + payload + checksum

            if (_buffer.Count < frameLength)
                return;

            // Validate checksum
            byte checksum = 0;
            for (int i = 2; i < 2 + payloadLength; i++)
                checksum ^= _buffer[i];

            if (checksum != _buffer[2 + payloadLength])
            {
                // Bad checksum — skip this start marker and try next
                _buffer.RemoveAt(0);
                continue;
            }

            // Extract payload and parse
            var payload = _buffer.GetRange(2, payloadLength).ToArray();
            _buffer.RemoveRange(0, frameLength);

            var message = ParsePayload(payload);
            if (message != null)
                MessageParsed?.Invoke(this, message);
        }
    }

    private static ControllerMessage? ParsePayload(byte[] payload)
    {
        if (payload.Length < 3)
            return null;

        var type = (MessageType)payload[0];
        return type switch
        {
            MessageType.Slider when payload.Length >= 4 =>
                new SliderMessage(payload[1], (ushort)((payload[2] << 8) | payload[3])),
            MessageType.Button =>
                new ButtonMessage(payload[1], (ButtonState)payload[2]),
            _ => null
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Volco.Tests --filter "ProtocolParserTests" -v n
```
Expected: 6 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Communication/ProtocolParser.cs tests/Volco.Tests/Communication/ProtocolParserTests.cs
git commit -m "feat: add ProtocolParser with framing, checksum validation, and message deserialization"
```

---

### Task 6: Communication Layer — Interface and Implementations

**Files:**
- Create: `src/Volco/Communication/IControllerConnection.cs`
- Create: `src/Volco/Communication/SerialConnection.cs`
- Create: `src/Volco/Communication/UdpConnection.cs`

- [ ] **Step 1: Create the connection interface**

Create `src/Volco/Communication/IControllerConnection.cs`:

```csharp
using Volco.Config;

namespace Volco.Communication;

public enum ConnectionStatus
{
    Disconnected,
    Connected,
    Error
}

public interface IControllerConnection : IDisposable
{
    event EventHandler<ControllerMessage>? MessageReceived;
    event EventHandler<ConnectionStatus>? StatusChanged;
    ConnectionStatus Status { get; }
    void Connect();
    void Disconnect();
}
```

- [ ] **Step 2: Implement SerialConnection**

Create `src/Volco/Communication/SerialConnection.cs`:

```csharp
using System.IO.Ports;
using Volco.Config;

namespace Volco.Communication;

public class SerialConnection : IControllerConnection
{
    private readonly SerialConfig _config;
    private readonly ProtocolParser _parser = new();
    private SerialPort? _serialPort;

    public event EventHandler<ControllerMessage>? MessageReceived;
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public SerialConnection(SerialConfig config)
    {
        _config = config;
        _parser.MessageParsed += (_, msg) => MessageReceived?.Invoke(this, msg);
    }

    public void Connect()
    {
        try
        {
            _serialPort = new SerialPort(_config.ComPort, _config.BaudRate);
            _serialPort.DataReceived += OnDataReceived;
            _serialPort.Open();
            Status = ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, Status);
        }
        catch (Exception)
        {
            Status = ConnectionStatus.Error;
            StatusChanged?.Invoke(this, Status);
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null || !_serialPort.IsOpen)
            return;

        var bytesToRead = _serialPort.BytesToRead;
        var buffer = new byte[bytesToRead];
        _serialPort.Read(buffer, 0, bytesToRead);
        _parser.FeedBytes(buffer);
    }

    public void Disconnect()
    {
        if (_serialPort is { IsOpen: true })
        {
            _serialPort.DataReceived -= OnDataReceived;
            _serialPort.Close();
        }
        _serialPort = null;
        Status = ConnectionStatus.Disconnected;
        StatusChanged?.Invoke(this, Status);
    }

    public void Dispose()
    {
        Disconnect();
        _serialPort?.Dispose();
    }
}
```

- [ ] **Step 3: Implement UdpConnection**

Create `src/Volco/Communication/UdpConnection.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Volco.Config;

namespace Volco.Communication;

public class UdpConnection : IControllerConnection
{
    private readonly UdpConfig _config;
    private readonly ProtocolParser _parser = new();
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;

    public event EventHandler<ControllerMessage>? MessageReceived;
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public UdpConnection(UdpConfig config)
    {
        _config = config;
        _parser.MessageParsed += (_, msg) => MessageReceived?.Invoke(this, msg);
    }

    public void Connect()
    {
        try
        {
            _udpClient = new UdpClient(_config.ListenPort);
            _cts = new CancellationTokenSource();
            Status = ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, Status);
            _ = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception)
        {
            Status = ConnectionStatus.Error;
            StatusChanged?.Invoke(this, Status);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                _parser.FeedBytes(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                Status = ConnectionStatus.Error;
                StatusChanged?.Invoke(this, Status);
                break;
            }
        }
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient = null;
        _cts?.Dispose();
        _cts = null;
        Status = ConnectionStatus.Disconnected;
        StatusChanged?.Invoke(this, Status);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
```

- [ ] **Step 4: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Communication/IControllerConnection.cs src/Volco/Communication/SerialConnection.cs src/Volco/Communication/UdpConnection.cs
git commit -m "feat: add IControllerConnection with Serial and UDP implementations"
```

---

### Task 7: Button Press Detector (TDD)

**Files:**
- Create: `src/Volco/Input/ButtonPressDetector.cs`
- Create: `tests/Volco.Tests/Input/ButtonPressDetectorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Volco.Tests/Input/ButtonPressDetectorTests.cs`:

```csharp
using Volco.Communication;
using Volco.Input;

namespace Volco.Tests.Input;

public class ButtonPressDetectorTests
{
    private readonly ButtonPressDetector _detector;
    private readonly List<(int ButtonIndex, PressType PressType)> _events = new();

    public ButtonPressDetectorTests()
    {
        _detector = new ButtonPressDetector(
            longPressMs: 500,
            doublePressWindowMs: 300
        );
        _detector.PressDetected += (_, e) => _events.Add((e.ButtonIndex, e.PressType));
    }

    [Fact]
    public async Task SinglePress_DetectedAfterTimeout()
    {
        _detector.HandleButtonEvent(0, ButtonState.Pressed);
        _detector.HandleButtonEvent(0, ButtonState.Released);

        // Wait for the double-press window to expire
        await Task.Delay(400);

        Assert.Single(_events);
        Assert.Equal(PressType.Single, _events[0].PressType);
        Assert.Equal(0, _events[0].ButtonIndex);
    }

    [Fact]
    public async Task DoublePress_DetectedOnSecondRelease()
    {
        _detector.HandleButtonEvent(1, ButtonState.Pressed);
        _detector.HandleButtonEvent(1, ButtonState.Released);
        await Task.Delay(100); // within window
        _detector.HandleButtonEvent(1, ButtonState.Pressed);
        _detector.HandleButtonEvent(1, ButtonState.Released);

        // Small delay to let event fire
        await Task.Delay(50);

        Assert.Single(_events);
        Assert.Equal(PressType.Double, _events[0].PressType);
        Assert.Equal(1, _events[0].ButtonIndex);
    }

    [Fact]
    public async Task LongPress_DetectedWhileHeld()
    {
        _detector.HandleButtonEvent(2, ButtonState.Pressed);
        await Task.Delay(600); // exceeds 500ms threshold

        Assert.Single(_events);
        Assert.Equal(PressType.Long, _events[0].PressType);
        Assert.Equal(2, _events[0].ButtonIndex);

        // Release should not trigger single press
        _detector.HandleButtonEvent(2, ButtonState.Released);
        await Task.Delay(400);

        Assert.Single(_events); // still just the one long press
    }

    [Fact]
    public async Task IndependentButtons_DoNotInterfere()
    {
        _detector.HandleButtonEvent(0, ButtonState.Pressed);
        _detector.HandleButtonEvent(0, ButtonState.Released);
        _detector.HandleButtonEvent(1, ButtonState.Pressed);
        _detector.HandleButtonEvent(1, ButtonState.Released);

        await Task.Delay(400);

        Assert.Equal(2, _events.Count);
        Assert.Equal(0, _events[0].ButtonIndex);
        Assert.Equal(1, _events[1].ButtonIndex);
        Assert.All(_events, e => Assert.Equal(PressType.Single, e.PressType));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Volco.Tests --filter "ButtonPressDetectorTests" -v n
```
Expected: FAIL — `ButtonPressDetector` does not exist.

- [ ] **Step 3: Implement ButtonPressDetector**

Create `src/Volco/Input/ButtonPressDetector.cs`:

```csharp
using Volco.Communication;

namespace Volco.Input;

public enum PressType { Single, Double, Long }

public class ButtonPressEventArgs : EventArgs
{
    public int ButtonIndex { get; }
    public PressType PressType { get; }

    public ButtonPressEventArgs(int buttonIndex, PressType pressType)
    {
        ButtonIndex = buttonIndex;
        PressType = pressType;
    }
}

public class ButtonPressDetector
{
    private readonly int _longPressMs;
    private readonly int _doublePressWindowMs;
    private readonly Dictionary<int, ButtonStateTracker> _trackers = new();

    public event EventHandler<ButtonPressEventArgs>? PressDetected;

    public ButtonPressDetector(int longPressMs = 500, int doublePressWindowMs = 300)
    {
        _longPressMs = longPressMs;
        _doublePressWindowMs = doublePressWindowMs;
    }

    public void HandleButtonEvent(int buttonIndex, ButtonState state)
    {
        if (!_trackers.TryGetValue(buttonIndex, out var tracker))
        {
            tracker = new ButtonStateTracker(buttonIndex, _longPressMs, _doublePressWindowMs, OnPressDetected);
            _trackers[buttonIndex] = tracker;
        }

        tracker.HandleState(state);
    }

    private void OnPressDetected(int buttonIndex, PressType pressType)
    {
        PressDetected?.Invoke(this, new ButtonPressEventArgs(buttonIndex, pressType));
    }

    private class ButtonStateTracker
    {
        private readonly int _buttonIndex;
        private readonly int _longPressMs;
        private readonly int _doublePressWindowMs;
        private readonly Action<int, PressType> _callback;
        private CancellationTokenSource? _longPressCts;
        private CancellationTokenSource? _singlePressCts;
        private bool _longPressFired;
        private bool _waitingForDoublePress;

        public ButtonStateTracker(int buttonIndex, int longPressMs, int doublePressWindowMs, Action<int, PressType> callback)
        {
            _buttonIndex = buttonIndex;
            _longPressMs = longPressMs;
            _doublePressWindowMs = doublePressWindowMs;
            _callback = callback;
        }

        public void HandleState(ButtonState state)
        {
            if (state == ButtonState.Pressed)
                OnPressed();
            else
                OnReleased();
        }

        private void OnPressed()
        {
            _longPressFired = false;
            _longPressCts?.Cancel();
            _longPressCts = new CancellationTokenSource();
            _ = LongPressTimerAsync(_longPressCts.Token);
        }

        private void OnReleased()
        {
            _longPressCts?.Cancel();

            if (_longPressFired)
            {
                _longPressFired = false;
                return;
            }

            if (_waitingForDoublePress)
            {
                _singlePressCts?.Cancel();
                _waitingForDoublePress = false;
                _callback(_buttonIndex, PressType.Double);
                return;
            }

            _waitingForDoublePress = true;
            _singlePressCts?.Cancel();
            _singlePressCts = new CancellationTokenSource();
            _ = SinglePressTimerAsync(_singlePressCts.Token);
        }

        private async Task LongPressTimerAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(_longPressMs, ct);
                _longPressFired = true;
                _callback(_buttonIndex, PressType.Long);
            }
            catch (OperationCanceledException) { }
        }

        private async Task SinglePressTimerAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(_doublePressWindowMs, ct);
                _waitingForDoublePress = false;
                _callback(_buttonIndex, PressType.Single);
            }
            catch (OperationCanceledException) { }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Volco.Tests --filter "ButtonPressDetectorTests" -v n
```
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Input/ButtonPressDetector.cs tests/Volco.Tests/Input/ButtonPressDetectorTests.cs
git commit -m "feat: add ButtonPressDetector with single, double, and long press classification"
```

---

### Task 8: Audio Manager Interface and Implementation

**Files:**
- Create: `src/Volco/Audio/IAudioManager.cs`
- Create: `src/Volco/Audio/AudioManager.cs`

- [ ] **Step 1: Create the audio manager interface**

Create `src/Volco/Audio/IAudioManager.cs`:

```csharp
namespace Volco.Audio;

public class AudioDeviceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

public interface IAudioManager
{
    void SetProcessVolume(string processName, float volume);
    void SetDeviceVolume(float volume);
    void ToggleMuteOutput(string? deviceId = null);
    void ToggleMuteInput();
    void SetDefaultOutputDevice(string deviceName);
    List<AudioDeviceInfo> GetOutputDevices();
    List<AudioDeviceInfo> GetInputDevices();
    List<string> GetActiveProcessNames();
}
```

- [ ] **Step 2: Implement AudioManager**

Create `src/Volco/Audio/AudioManager.cs`:

```csharp
using NAudio.CoreAudioApi;

namespace Volco.Audio;

public class AudioManager : IAudioManager
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public void SetProcessVolume(string processName, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var sessionProcessName = GetProcessName(session);
            if (string.Equals(sessionProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                session.SimpleAudioVolume.Volume = volume;
            }
        }
    }

    public void SetDeviceVolume(float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
    }

    public void ToggleMuteOutput(string? deviceId = null)
    {
        MMDevice device;
        if (deviceId != null)
        {
            device = FindDeviceByName(DataFlow.Render, deviceId);
            if (device == null) return;
        }
        else
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }

    public void ToggleMuteInput()
    {
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }

    public void SetDefaultOutputDevice(string deviceName)
    {
        // NAudio doesn't directly support changing the default device.
        // Use the AudioSwitcher library for this.
        var controller = new AudioSwitcher.AudioApi.CoreAudio.CoreAudioController();
        var devices = controller.GetPlaybackDevices();
        var target = devices.FirstOrDefault(d =>
            d.FullName.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
        target?.SetAsDefault();
    }

    public List<AudioDeviceInfo> GetOutputDevices()
    {
        return _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName })
            .ToList();
    }

    public List<AudioDeviceInfo> GetInputDevices()
    {
        return _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName })
            .ToList();
    }

    public List<string> GetActiveProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            var name = GetProcessName(sessions[i]);
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }

        return names.ToList();
    }

    private MMDevice? FindDeviceByName(DataFlow flow, string name)
    {
        return _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetProcessName(AudioSessionControl session)
    {
        try
        {
            var pid = (int)session.GetProcessID;
            if (pid == 0) return "";
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return "";
        }
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Volco/Audio/IAudioManager.cs src/Volco/Audio/AudioManager.cs
git commit -m "feat: add AudioManager with volume, mute, and device switching via NAudio"
```

---

### Task 9: Session Monitor

**Files:**
- Create: `src/Volco/Audio/SessionMonitor.cs`

- [ ] **Step 1: Implement SessionMonitor**

Create `src/Volco/Audio/SessionMonitor.cs`:

```csharp
namespace Volco.Audio;

public class SessionMonitor : IDisposable
{
    private readonly IAudioManager _audioManager;
    private readonly Timer _timer;
    private HashSet<string> _activeProcesses = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> ActiveProcesses => _activeProcesses;

    public SessionMonitor(IAudioManager audioManager, int pollIntervalMs = 2000)
    {
        _audioManager = audioManager;
        _timer = new Timer(_ => Refresh(), null, 0, pollIntervalMs);
    }

    public void Refresh()
    {
        var names = _audioManager.GetActiveProcessNames();
        _activeProcesses = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Volco/Audio/SessionMonitor.cs
git commit -m "feat: add SessionMonitor for periodic audio session enumeration"
```

---

### Task 10: Action System

**Files:**
- Create: `src/Volco/Input/Actions/IButtonAction.cs`
- Create: `src/Volco/Input/Actions/MuteSpecificDeviceAction.cs`
- Create: `src/Volco/Input/Actions/MuteCurrentOutputAction.cs`
- Create: `src/Volco/Input/Actions/MuteCurrentInputAction.cs`
- Create: `src/Volco/Input/Actions/ToggleOutputDeviceAction.cs`
- Create: `src/Volco/Input/Actions/SetOutputDeviceAction.cs`
- Create: `src/Volco/Input/Actions/ActionFactory.cs`
- Create: `tests/Volco.Tests/Input/Actions/ActionFactoryTests.cs`

- [ ] **Step 1: Create the action interface and all implementations**

Create `src/Volco/Input/Actions/IButtonAction.cs`:

```csharp
namespace Volco.Input.Actions;

public interface IButtonAction
{
    void Execute();
}
```

Create `src/Volco/Input/Actions/MuteCurrentOutputAction.cs`:

```csharp
using Volco.Audio;

namespace Volco.Input.Actions;

public class MuteCurrentOutputAction : IButtonAction
{
    private readonly IAudioManager _audio;
    public MuteCurrentOutputAction(IAudioManager audio) => _audio = audio;
    public void Execute() => _audio.ToggleMuteOutput();
}
```

Create `src/Volco/Input/Actions/MuteCurrentInputAction.cs`:

```csharp
using Volco.Audio;

namespace Volco.Input.Actions;

public class MuteCurrentInputAction : IButtonAction
{
    private readonly IAudioManager _audio;
    public MuteCurrentInputAction(IAudioManager audio) => _audio = audio;
    public void Execute() => _audio.ToggleMuteInput();
}
```

Create `src/Volco/Input/Actions/MuteSpecificDeviceAction.cs`:

```csharp
using Volco.Audio;

namespace Volco.Input.Actions;

public class MuteSpecificDeviceAction : IButtonAction
{
    private readonly IAudioManager _audio;
    private readonly string _deviceId;

    public MuteSpecificDeviceAction(IAudioManager audio, string deviceId)
    {
        _audio = audio;
        _deviceId = deviceId;
    }

    public void Execute() => _audio.ToggleMuteOutput(_deviceId);
}
```

Create `src/Volco/Input/Actions/ToggleOutputDeviceAction.cs`:

```csharp
using Volco.Audio;
using Volco.Config;

namespace Volco.Input.Actions;

public class ToggleOutputDeviceAction : IButtonAction
{
    private readonly IAudioManager _audio;
    private readonly ConfigManager _configManager;
    private bool _onDevice1 = true;

    public ToggleOutputDeviceAction(IAudioManager audio, ConfigManager configManager)
    {
        _audio = audio;
        _configManager = configManager;
    }

    public void Execute()
    {
        var devices = _configManager.Config.OutputDevices;
        var target = _onDevice1 ? devices.Device2 : devices.Device1;
        _audio.SetDefaultOutputDevice(target);
        _onDevice1 = !_onDevice1;
    }
}
```

Create `src/Volco/Input/Actions/SetOutputDeviceAction.cs`:

```csharp
using Volco.Audio;

namespace Volco.Input.Actions;

public class SetOutputDeviceAction : IButtonAction
{
    private readonly IAudioManager _audio;
    private readonly string _deviceId;

    public SetOutputDeviceAction(IAudioManager audio, string deviceId)
    {
        _audio = audio;
        _deviceId = deviceId;
    }

    public void Execute() => _audio.SetDefaultOutputDevice(_deviceId);
}
```

- [ ] **Step 2: Write failing test for ActionFactory**

Create `tests/Volco.Tests/Input/Actions/ActionFactoryTests.cs`:

```csharp
using Volco.Audio;
using Volco.Config;
using Volco.Input.Actions;

namespace Volco.Tests.Input.Actions;

public class ActionFactoryTests
{
    private readonly FakeAudioManager _audio = new();
    private readonly ConfigManager _configManager;

    public ActionFactoryTests()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"volco-test-{Guid.NewGuid()}", "config.json");
        _configManager = new ConfigManager(tempPath);
        _configManager.Load();
    }

    [Fact]
    public void Create_MuteCurrentOutput_ReturnsCorrectType()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.MuteCurrentOutput };
        var action = ActionFactory.Create(config, _audio, _configManager);
        Assert.IsType<MuteCurrentOutputAction>(action);
    }

    [Fact]
    public void Create_MuteSpecificDevice_ReturnsCorrectType()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, DeviceId = "Speakers" };
        var action = ActionFactory.Create(config, _audio, _configManager);
        Assert.IsType<MuteSpecificDeviceAction>(action);
    }

    [Fact]
    public void Create_None_ReturnsNull()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.None };
        var action = ActionFactory.Create(config, _audio, _configManager);
        Assert.Null(action);
    }

    [Fact]
    public void Create_ToggleOutputDevice_ReturnsCorrectType()
    {
        var config = new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice };
        var action = ActionFactory.Create(config, _audio, _configManager);
        Assert.IsType<ToggleOutputDeviceAction>(action);
    }

    private class FakeAudioManager : IAudioManager
    {
        public void SetProcessVolume(string processName, float volume) { }
        public void SetDeviceVolume(float volume) { }
        public void ToggleMuteOutput(string? deviceId = null) { }
        public void ToggleMuteInput() { }
        public void SetDefaultOutputDevice(string deviceName) { }
        public List<AudioDeviceInfo> GetOutputDevices() => new();
        public List<AudioDeviceInfo> GetInputDevices() => new();
        public List<string> GetActiveProcessNames() => new();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test tests/Volco.Tests --filter "ActionFactoryTests" -v n
```
Expected: FAIL — `ActionFactory` does not exist.

- [ ] **Step 4: Implement ActionFactory**

Create `src/Volco/Input/Actions/ActionFactory.cs`:

```csharp
using Volco.Audio;
using Volco.Config;

namespace Volco.Input.Actions;

public static class ActionFactory
{
    public static IButtonAction? Create(ButtonActionConfig config, IAudioManager audio, ConfigManager configManager)
    {
        return config.Action switch
        {
            ButtonActionType.MuteCurrentOutput => new MuteCurrentOutputAction(audio),
            ButtonActionType.MuteCurrentInput => new MuteCurrentInputAction(audio),
            ButtonActionType.MuteSpecificDevice => new MuteSpecificDeviceAction(audio, config.DeviceId ?? ""),
            ButtonActionType.ToggleOutputDevice => new ToggleOutputDeviceAction(audio, configManager),
            ButtonActionType.SetOutputDevice => new SetOutputDeviceAction(audio, config.DeviceId ?? ""),
            ButtonActionType.None => null,
            _ => null
        };
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test tests/Volco.Tests --filter "ActionFactoryTests" -v n
```
Expected: 4 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Volco/Input/Actions/ tests/Volco.Tests/Input/Actions/
git commit -m "feat: add button action system with factory and all action implementations"
```

---

### Task 11: Slider Processor (TDD)

**Files:**
- Create: `src/Volco/Input/SliderProcessor.cs`
- Create: `tests/Volco.Tests/Input/SliderProcessorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Volco.Tests/Input/SliderProcessorTests.cs`:

```csharp
using Volco.Audio;
using Volco.Config;
using Volco.Input;

namespace Volco.Tests.Input;

public class SliderProcessorTests
{
    [Fact]
    public void MapAdcToVolume_ZeroReturnsZero()
    {
        Assert.Equal(0f, SliderProcessor.MapAdcToVolume(0, invert: false), 3);
    }

    [Fact]
    public void MapAdcToVolume_MaxReturnsOne()
    {
        Assert.Equal(1f, SliderProcessor.MapAdcToVolume(1023, invert: false), 3);
    }

    [Fact]
    public void MapAdcToVolume_MidReturnsHalf()
    {
        Assert.Equal(0.5f, SliderProcessor.MapAdcToVolume(512, invert: false), 1);
    }

    [Fact]
    public void MapAdcToVolume_InvertFlipsValue()
    {
        var normal = SliderProcessor.MapAdcToVolume(200, invert: false);
        var inverted = SliderProcessor.MapAdcToVolume(200, invert: true);
        Assert.Equal(1f, normal + inverted, 3);
    }

    [Fact]
    public void GetUnmappedProcesses_ExcludesMappedProcesses()
    {
        var config = new AppConfig();
        config.Sliders.Add(new SliderConfig
        {
            Index = 0,
            Type = SliderMappingType.Processes,
            Processes = new List<string> { "chrome.exe", "spotify.exe" }
        });
        config.Sliders.Add(new SliderConfig
        {
            Index = 1,
            Type = SliderMappingType.Unmapped
        });

        var activeProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome.exe", "spotify.exe", "discord.exe", "firefox.exe"
        };

        var unmapped = SliderProcessor.GetUnmappedProcesses(config, activeProcesses);

        Assert.Contains("discord.exe", unmapped);
        Assert.Contains("firefox.exe", unmapped);
        Assert.DoesNotContain("chrome.exe", unmapped);
        Assert.DoesNotContain("spotify.exe", unmapped);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Volco.Tests --filter "SliderProcessorTests" -v n
```
Expected: FAIL — `SliderProcessor` does not exist.

- [ ] **Step 3: Implement SliderProcessor**

Create `src/Volco/Input/SliderProcessor.cs`:

```csharp
using Volco.Audio;
using Volco.Config;

namespace Volco.Input;

public class SliderProcessor
{
    private readonly IAudioManager _audio;
    private readonly ConfigManager _configManager;
    private readonly SessionMonitor _sessionMonitor;

    public SliderProcessor(IAudioManager audio, ConfigManager configManager, SessionMonitor sessionMonitor)
    {
        _audio = audio;
        _configManager = configManager;
        _sessionMonitor = sessionMonitor;
    }

    public static float MapAdcToVolume(ushort adcValue, bool invert)
    {
        float volume = adcValue / 1023f;
        return invert ? 1f - volume : volume;
    }

    public static List<string> GetUnmappedProcesses(AppConfig config, IReadOnlySet<string> activeProcesses)
    {
        var mappedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slider in config.Sliders)
        {
            if (slider.Type == SliderMappingType.Processes)
            {
                foreach (var p in slider.Processes)
                    mappedProcesses.Add(p);
            }
        }

        return activeProcesses
            .Where(p => !mappedProcesses.Contains(p))
            .ToList();
    }

    public void HandleSliderEvent(int sliderIndex, ushort adcValue)
    {
        var config = _configManager.Config;
        if (sliderIndex < 0 || sliderIndex >= config.Sliders.Count)
            return;

        var sliderConfig = config.Sliders[sliderIndex];
        var volume = MapAdcToVolume(adcValue, sliderConfig.Invert);

        switch (sliderConfig.Type)
        {
            case SliderMappingType.Device:
                _audio.SetDeviceVolume(volume);
                break;

            case SliderMappingType.Processes:
                foreach (var processName in sliderConfig.Processes)
                    _audio.SetProcessVolume(processName, volume);
                break;

            case SliderMappingType.Unmapped:
                var unmapped = GetUnmappedProcesses(config, _sessionMonitor.ActiveProcesses);
                foreach (var processName in unmapped)
                    _audio.SetProcessVolume(processName, volume);
                break;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Volco.Tests --filter "SliderProcessorTests" -v n
```
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Input/SliderProcessor.cs tests/Volco.Tests/Input/SliderProcessorTests.cs
git commit -m "feat: add SliderProcessor with ADC mapping, inversion, and unmapped process detection"
```

---

### Task 12: UI — Settings Window Shell

**Files:**
- Create: `src/Volco/UI/SettingsWindow.xaml`
- Create: `src/Volco/UI/SettingsWindow.xaml.cs`

- [ ] **Step 1: Create the settings window XAML**

Create `src/Volco/UI/SettingsWindow.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Window
    x:Class="Volco.UI.SettingsWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:Volco.UI"
    Title="Volco Settings"
    Width="600" Height="500">

    <NavigationView x:Name="NavView"
                    IsBackButtonVisible="Collapsed"
                    IsSettingsVisible="False"
                    SelectionChanged="NavView_SelectionChanged"
                    PaneDisplayMode="Top">
        <NavigationView.MenuItems>
            <NavigationViewItem Content="Connection" Tag="connection" />
            <NavigationViewItem Content="Sliders" Tag="sliders" />
            <NavigationViewItem Content="Buttons" Tag="buttons" />
            <NavigationViewItem Content="Output Devices" Tag="outputdevices" />
        </NavigationView.MenuItems>

        <Frame x:Name="ContentFrame" />
    </NavigationView>
</Window>
```

- [ ] **Step 2: Create the settings window code-behind**

Create `src/Volco/UI/SettingsWindow.xaml.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Volco.Audio;
using Volco.Config;

namespace Volco.UI;

public sealed partial class SettingsWindow : Window
{
    private readonly ConfigManager _configManager;
    private readonly IAudioManager _audioManager;

    public SettingsWindow(ConfigManager configManager, IAudioManager audioManager)
    {
        InitializeComponent();
        _configManager = configManager;
        _audioManager = audioManager;

        // Select first tab by default
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            switch (tag)
            {
                case "connection":
                    ContentFrame.Navigate(typeof(ConnectionTab), _configManager);
                    break;
                case "sliders":
                    ContentFrame.Navigate(typeof(SlidersTab), _configManager);
                    break;
                case "buttons":
                    ContentFrame.Navigate(typeof(ButtonsTab), (_configManager, _audioManager));
                    break;
                case "outputdevices":
                    ContentFrame.Navigate(typeof(OutputDevicesTab), (_configManager, _audioManager));
                    break;
            }
        }
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded (tabs don't exist yet — the Frame navigation types will resolve at runtime).

- [ ] **Step 4: Commit**

```bash
git add src/Volco/UI/SettingsWindow.xaml src/Volco/UI/SettingsWindow.xaml.cs
git commit -m "feat: add SettingsWindow with tabbed navigation shell"
```

---

### Task 13: UI — Connection Tab

**Files:**
- Create: `src/Volco/UI/ConnectionTab.xaml`
- Create: `src/Volco/UI/ConnectionTab.xaml.cs`

- [ ] **Step 1: Create Connection tab XAML**

Create `src/Volco/UI/ConnectionTab.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="Volco.UI.ConnectionTab"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <StackPanel Padding="24" Spacing="16">
        <TextBlock Text="Connection Settings" Style="{StaticResource SubtitleTextBlockStyle}" />

        <!-- Connection Type -->
        <StackPanel Spacing="8">
            <TextBlock Text="Connection Type" Style="{StaticResource BodyStrongTextBlockStyle}" />
            <RadioButtons x:Name="ConnectionTypeRadio" SelectionChanged="ConnectionType_Changed">
                <RadioButton Content="Serial (USB)" Tag="Serial" />
                <RadioButton Content="UDP (Wireless)" Tag="Udp" />
            </RadioButtons>
        </StackPanel>

        <!-- Serial Settings -->
        <StackPanel x:Name="SerialPanel" Spacing="8">
            <TextBlock Text="Serial Settings" Style="{StaticResource BodyStrongTextBlockStyle}" />
            <StackPanel Orientation="Horizontal" Spacing="16">
                <StackPanel Spacing="4">
                    <TextBlock Text="COM Port" />
                    <ComboBox x:Name="ComPortCombo" Width="150" SelectionChanged="SerialSetting_Changed" />
                </StackPanel>
                <StackPanel Spacing="4">
                    <TextBlock Text="Baud Rate" />
                    <ComboBox x:Name="BaudRateCombo" Width="150" SelectionChanged="SerialSetting_Changed">
                        <ComboBoxItem Content="9600" Tag="9600" />
                        <ComboBoxItem Content="19200" Tag="19200" />
                        <ComboBoxItem Content="38400" Tag="38400" />
                        <ComboBoxItem Content="57600" Tag="57600" />
                        <ComboBoxItem Content="115200" Tag="115200" />
                    </ComboBox>
                </StackPanel>
            </StackPanel>
        </StackPanel>

        <!-- UDP Settings -->
        <StackPanel x:Name="UdpPanel" Spacing="8" Visibility="Collapsed">
            <TextBlock Text="UDP Settings" Style="{StaticResource BodyStrongTextBlockStyle}" />
            <StackPanel Spacing="4">
                <TextBlock Text="Listen Port" />
                <NumberBox x:Name="UdpPortBox" Minimum="1024" Maximum="65535"
                           SpinButtonPlacementMode="Compact" ValueChanged="UdpPort_Changed" />
            </StackPanel>
        </StackPanel>

        <!-- Status -->
        <StackPanel Orientation="Horizontal" Spacing="8">
            <Ellipse x:Name="StatusIndicator" Width="12" Height="12" Fill="Gray" />
            <TextBlock x:Name="StatusText" Text="Disconnected" />
        </StackPanel>
    </StackPanel>
</Page>
```

- [ ] **Step 2: Create Connection tab code-behind**

Create `src/Volco/UI/ConnectionTab.xaml.cs`:

```csharp
using System.IO.Ports;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Config;

namespace Volco.UI;

public sealed partial class ConnectionTab : Page
{
    private ConfigManager? _configManager;
    private bool _isLoading = true;

    public ConnectionTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _configManager = e.Parameter as ConfigManager;
        if (_configManager == null) return;

        _isLoading = true;
        LoadComPorts();
        LoadConfig();
        _isLoading = false;
    }

    private void LoadComPorts()
    {
        ComPortCombo.Items.Clear();
        foreach (var port in SerialPort.GetPortNames())
        {
            ComPortCombo.Items.Add(new ComboBoxItem { Content = port, Tag = port });
        }
    }

    private void LoadConfig()
    {
        var config = _configManager!.Config.Connection;

        // Set connection type radio
        ConnectionTypeRadio.SelectedIndex = config.Type == ConnectionType.Serial ? 0 : 1;

        // Set COM port
        for (int i = 0; i < ComPortCombo.Items.Count; i++)
        {
            if ((ComPortCombo.Items[i] as ComboBoxItem)?.Tag?.ToString() == config.Serial.ComPort)
            {
                ComPortCombo.SelectedIndex = i;
                break;
            }
        }

        // Set baud rate
        for (int i = 0; i < BaudRateCombo.Items.Count; i++)
        {
            if ((BaudRateCombo.Items[i] as ComboBoxItem)?.Tag?.ToString() == config.Serial.BaudRate.ToString())
            {
                BaudRateCombo.SelectedIndex = i;
                break;
            }
        }

        UdpPortBox.Value = config.Udp.ListenPort;
        UpdatePanelVisibility();
    }

    private void ConnectionType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        var selected = ConnectionTypeRadio.SelectedIndex;
        _configManager.Config.Connection.Type = selected == 0 ? ConnectionType.Serial : ConnectionType.Udp;
        UpdatePanelVisibility();
        _configManager.Save();
    }

    private void UpdatePanelVisibility()
    {
        var isSerial = _configManager?.Config.Connection.Type == ConnectionType.Serial;
        SerialPanel.Visibility = isSerial ? Visibility.Visible : Visibility.Collapsed;
        UdpPanel.Visibility = isSerial ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SerialSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (ComPortCombo.SelectedItem is ComboBoxItem portItem)
            _configManager.Config.Connection.Serial.ComPort = portItem.Tag?.ToString() ?? "";
        if (BaudRateCombo.SelectedItem is ComboBoxItem baudItem)
            _configManager.Config.Connection.Serial.BaudRate = int.Parse(baudItem.Tag?.ToString() ?? "9600");
        _configManager.Save();
    }

    private void UdpPort_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoading || _configManager == null) return;
        _configManager.Config.Connection.Udp.ListenPort = (int)args.NewValue;
        _configManager.Save();
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Volco/UI/ConnectionTab.xaml src/Volco/UI/ConnectionTab.xaml.cs
git commit -m "feat: add Connection settings tab with serial/UDP configuration"
```

---

### Task 14: UI — Sliders Tab

**Files:**
- Create: `src/Volco/UI/SlidersTab.xaml`
- Create: `src/Volco/UI/SlidersTab.xaml.cs`

- [ ] **Step 1: Create Sliders tab XAML**

Create `src/Volco/UI/SlidersTab.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="Volco.UI.SlidersTab"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <StackPanel Padding="24" Spacing="16">
        <TextBlock Text="Slider Mappings" Style="{StaticResource SubtitleTextBlockStyle}" />

        <ItemsRepeater x:Name="SliderRepeater">
            <ItemsRepeater.ItemTemplate>
                <DataTemplate>
                    <Border Padding="12" Margin="0,0,0,8"
                            CornerRadius="8"
                            Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                            BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                            BorderThickness="1">
                        <StackPanel Spacing="8">
                            <TextBlock Style="{StaticResource BodyStrongTextBlockStyle}">
                                <Run Text="Slider " /><Run Text="{Binding Index}" />
                            </TextBlock>
                            <StackPanel Orientation="Horizontal" Spacing="16">
                                <StackPanel Spacing="4">
                                    <TextBlock Text="Mapping Type" />
                                    <ComboBox Tag="{Binding Index}" Width="180"
                                              SelectionChanged="MappingType_Changed">
                                        <ComboBoxItem Content="Processes" Tag="Processes" />
                                        <ComboBoxItem Content="Output Device" Tag="Device" />
                                        <ComboBoxItem Content="All Non-Mapped" Tag="Unmapped" />
                                    </ComboBox>
                                </StackPanel>
                                <StackPanel Spacing="4">
                                    <TextBlock Text="Processes (comma-separated)" />
                                    <TextBox Tag="{Binding Index}" Width="250"
                                             PlaceholderText="e.g. spotify.exe, chrome.exe"
                                             TextChanged="Processes_Changed" />
                                </StackPanel>
                                <CheckBox Content="Invert" Tag="{Binding Index}"
                                          Checked="Invert_Changed" Unchecked="Invert_Changed" />
                            </StackPanel>
                        </StackPanel>
                    </Border>
                </DataTemplate>
            </ItemsRepeater.ItemTemplate>
        </ItemsRepeater>
    </StackPanel>
</Page>
```

- [ ] **Step 2: Create Sliders tab code-behind**

Create `src/Volco/UI/SlidersTab.xaml.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Config;

namespace Volco.UI;

public class SliderViewModel
{
    public int Index { get; set; }
    public string TypeTag { get; set; } = "Device";
    public string ProcessesText { get; set; } = "";
    public bool Invert { get; set; }
}

public sealed partial class SlidersTab : Page
{
    private ConfigManager? _configManager;
    private bool _isLoading = true;

    public SlidersTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _configManager = e.Parameter as ConfigManager;
        if (_configManager == null) return;

        _isLoading = true;
        var viewModels = _configManager.Config.Sliders.Select(s => new SliderViewModel
        {
            Index = s.Index,
            TypeTag = s.Type.ToString(),
            ProcessesText = string.Join(", ", s.Processes),
            Invert = s.Invert
        }).ToList();

        SliderRepeater.ItemsSource = viewModels;
        _isLoading = false;
    }

    private void MappingType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is ComboBox combo && combo.Tag is int index)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                var sliderConfig = _configManager.Config.Sliders.First(s => s.Index == index);
                sliderConfig.Type = Enum.Parse<SliderMappingType>(tag);
                _configManager.Save();
            }
        }
    }

    private void Processes_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is TextBox textBox && textBox.Tag is int index)
        {
            var sliderConfig = _configManager.Config.Sliders.First(s => s.Index == index);
            sliderConfig.Processes = textBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            _configManager.Save();
        }
    }

    private void Invert_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is CheckBox checkBox && checkBox.Tag is int index)
        {
            var sliderConfig = _configManager.Config.Sliders.First(s => s.Index == index);
            sliderConfig.Invert = checkBox.IsChecked == true;
            _configManager.Save();
        }
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Volco/UI/SlidersTab.xaml src/Volco/UI/SlidersTab.xaml.cs
git commit -m "feat: add Sliders configuration tab with mapping type, processes, and invert"
```

---

### Task 15: UI — Buttons Tab

**Files:**
- Create: `src/Volco/UI/ButtonsTab.xaml`
- Create: `src/Volco/UI/ButtonsTab.xaml.cs`

- [ ] **Step 1: Create Buttons tab XAML**

Create `src/Volco/UI/ButtonsTab.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="Volco.UI.ButtonsTab"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <ScrollViewer>
        <StackPanel Padding="24" Spacing="16">
            <TextBlock Text="Button Actions" Style="{StaticResource SubtitleTextBlockStyle}" />

            <StackPanel x:Name="ButtonsPanel" Spacing="12" />
        </StackPanel>
    </ScrollViewer>
</Page>
```

- [ ] **Step 2: Create Buttons tab code-behind**

Create `src/Volco/UI/ButtonsTab.xaml.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Audio;
using Volco.Config;

namespace Volco.UI;

public sealed partial class ButtonsTab : Page
{
    private ConfigManager? _configManager;
    private IAudioManager? _audioManager;
    private bool _isLoading = true;

    public ButtonsTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ValueTuple<ConfigManager, IAudioManager> tuple)
        {
            _configManager = tuple.Item1;
            _audioManager = tuple.Item2;
        }
        if (_configManager == null) return;

        _isLoading = true;
        BuildButtonPanels();
        _isLoading = false;
    }

    private void BuildButtonPanels()
    {
        ButtonsPanel.Children.Clear();
        var devices = _audioManager?.GetOutputDevices() ?? new();

        foreach (var button in _configManager!.Config.Buttons)
        {
            var card = new Border
            {
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(8),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1)
            };

            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = $"Button {button.Index}",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });

            stack.Children.Add(CreateActionRow("Single Press", button.Index, "single", button.SinglePress, devices));
            stack.Children.Add(CreateActionRow("Double Press", button.Index, "double", button.DoublePress, devices));
            stack.Children.Add(CreateActionRow("Long Press", button.Index, "long", button.LongPress, devices));

            card.Child = stack;
            ButtonsPanel.Children.Add(card);
        }
    }

    private StackPanel CreateActionRow(string label, int buttonIndex, string pressTag,
        ButtonActionConfig actionConfig, List<AudioDeviceInfo> devices)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        row.Children.Add(new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center });

        var actionCombo = new ComboBox { Width = 200, Tag = $"{buttonIndex}:{pressTag}" };
        foreach (ButtonActionType actionType in Enum.GetValues<ButtonActionType>())
        {
            var item = new ComboBoxItem { Content = FormatActionName(actionType), Tag = actionType.ToString() };
            actionCombo.Items.Add(item);
            if (actionType == actionConfig.Action)
                actionCombo.SelectedItem = item;
        }
        actionCombo.SelectionChanged += Action_Changed;
        row.Children.Add(actionCombo);

        var deviceCombo = new ComboBox { Width = 200, Tag = $"{buttonIndex}:{pressTag}:device", PlaceholderText = "Select device" };
        foreach (var device in devices)
        {
            var item = new ComboBoxItem { Content = device.Name, Tag = device.Name };
            deviceCombo.Items.Add(item);
            if (device.Name == actionConfig.DeviceId)
                deviceCombo.SelectedItem = item;
        }
        deviceCombo.SelectionChanged += Device_Changed;
        var needsDevice = actionConfig.Action is ButtonActionType.MuteSpecificDevice or ButtonActionType.SetOutputDevice;
        deviceCombo.Visibility = needsDevice ? Visibility.Visible : Visibility.Collapsed;
        row.Children.Add(deviceCombo);

        return row;
    }

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is not ComboBox combo || combo.Tag is not string tag) return;
        if (combo.SelectedItem is not ComboBoxItem selected || selected.Tag is not string actionTag) return;

        var parts = tag.Split(':');
        var buttonIndex = int.Parse(parts[0]);
        var pressType = parts[1];
        var actionType = Enum.Parse<ButtonActionType>(actionTag);

        var buttonConfig = _configManager.Config.Buttons.First(b => b.Index == buttonIndex);
        var actionConfig = pressType switch
        {
            "single" => buttonConfig.SinglePress,
            "double" => buttonConfig.DoublePress,
            "long" => buttonConfig.LongPress,
            _ => throw new ArgumentException($"Unknown press type: {pressType}")
        };
        actionConfig.Action = actionType;

        // Show/hide device picker
        var parent = combo.Parent as StackPanel;
        var deviceCombo = parent?.Children.OfType<ComboBox>().LastOrDefault();
        if (deviceCombo != null)
        {
            var needsDevice = actionType is ButtonActionType.MuteSpecificDevice or ButtonActionType.SetOutputDevice;
            deviceCombo.Visibility = needsDevice ? Visibility.Visible : Visibility.Collapsed;
        }

        _configManager.Save();
    }

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is not ComboBox combo || combo.Tag is not string tag) return;
        if (combo.SelectedItem is not ComboBoxItem selected || selected.Tag is not string deviceName) return;

        var parts = tag.Split(':');
        var buttonIndex = int.Parse(parts[0]);
        var pressType = parts[1];

        var buttonConfig = _configManager.Config.Buttons.First(b => b.Index == buttonIndex);
        var actionConfig = pressType switch
        {
            "single" => buttonConfig.SinglePress,
            "double" => buttonConfig.DoublePress,
            "long" => buttonConfig.LongPress,
            _ => throw new ArgumentException($"Unknown press type: {pressType}")
        };
        actionConfig.DeviceId = deviceName;
        _configManager.Save();
    }

    private static string FormatActionName(ButtonActionType type) => type switch
    {
        ButtonActionType.None => "None",
        ButtonActionType.MuteSpecificDevice => "Mute Specific Device",
        ButtonActionType.MuteCurrentOutput => "Mute Current Output",
        ButtonActionType.MuteCurrentInput => "Mute Current Input",
        ButtonActionType.ToggleOutputDevice => "Toggle Output Device",
        ButtonActionType.SetOutputDevice => "Set Output Device",
        _ => type.ToString()
    };
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Volco/UI/ButtonsTab.xaml src/Volco/UI/ButtonsTab.xaml.cs
git commit -m "feat: add Buttons configuration tab with action and device selection"
```

---

### Task 16: UI — Output Devices Tab

**Files:**
- Create: `src/Volco/UI/OutputDevicesTab.xaml`
- Create: `src/Volco/UI/OutputDevicesTab.xaml.cs`

- [ ] **Step 1: Create Output Devices tab XAML**

Create `src/Volco/UI/OutputDevicesTab.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="Volco.UI.OutputDevicesTab"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <StackPanel Padding="24" Spacing="16">
        <TextBlock Text="Output Device Toggle" Style="{StaticResource SubtitleTextBlockStyle}" />
        <TextBlock Text="Configure the two devices to switch between when using the Toggle Output Device action."
                   TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />

        <StackPanel Spacing="12">
            <StackPanel Spacing="4">
                <TextBlock Text="Device 1" Style="{StaticResource BodyStrongTextBlockStyle}" />
                <ComboBox x:Name="Device1Combo" Width="300" SelectionChanged="Device1_Changed" />
            </StackPanel>

            <StackPanel Spacing="4">
                <TextBlock Text="Device 2" Style="{StaticResource BodyStrongTextBlockStyle}" />
                <ComboBox x:Name="Device2Combo" Width="300" SelectionChanged="Device2_Changed" />
            </StackPanel>
        </StackPanel>
    </StackPanel>
</Page>
```

- [ ] **Step 2: Create Output Devices tab code-behind**

Create `src/Volco/UI/OutputDevicesTab.xaml.cs`:

```csharp
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Audio;
using Volco.Config;

namespace Volco.UI;

public sealed partial class OutputDevicesTab : Page
{
    private ConfigManager? _configManager;
    private IAudioManager? _audioManager;
    private bool _isLoading = true;

    public OutputDevicesTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ValueTuple<ConfigManager, IAudioManager> tuple)
        {
            _configManager = tuple.Item1;
            _audioManager = tuple.Item2;
        }
        if (_configManager == null || _audioManager == null) return;

        _isLoading = true;
        LoadDevices();
        _isLoading = false;
    }

    private void LoadDevices()
    {
        var devices = _audioManager!.GetOutputDevices();
        var config = _configManager!.Config.OutputDevices;

        PopulateCombo(Device1Combo, devices, config.Device1);
        PopulateCombo(Device2Combo, devices, config.Device2);
    }

    private static void PopulateCombo(ComboBox combo, List<AudioDeviceInfo> devices, string selectedName)
    {
        combo.Items.Clear();
        foreach (var device in devices)
        {
            var item = new ComboBoxItem { Content = device.Name, Tag = device.Name };
            combo.Items.Add(item);
            if (device.Name == selectedName)
                combo.SelectedItem = item;
        }
    }

    private void Device1_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (Device1Combo.SelectedItem is ComboBoxItem item && item.Tag is string name)
        {
            _configManager.Config.OutputDevices.Device1 = name;
            _configManager.Save();
        }
    }

    private void Device2_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (Device2Combo.SelectedItem is ComboBoxItem item && item.Tag is string name)
        {
            _configManager.Config.OutputDevices.Device2 = name;
            _configManager.Save();
        }
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Volco/UI/OutputDevicesTab.xaml src/Volco/UI/OutputDevicesTab.xaml.cs
git commit -m "feat: add Output Devices configuration tab"
```

---

### Task 17: App Entry Point — Tray Icon and Wiring

**Files:**
- Modify: `src/Volco/App.xaml`
- Modify: `src/Volco/App.xaml.cs`

- [ ] **Step 1: Update App.xaml for tray icon resource**

Replace the content of `src/Volco/App.xaml` with:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Application
    x:Class="Volco.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:tb="using:H.NotifyIcon">

    <Application.Resources>
        <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
    </Application.Resources>
</Application>
```

- [ ] **Step 2: Implement App.xaml.cs with full wiring**

Replace the content of `src/Volco/App.xaml.cs` with:

```csharp
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Volco.Audio;
using Volco.Communication;
using Volco.Config;
using Volco.Input;
using Volco.Input.Actions;
using Volco.UI;

namespace Volco;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private ConfigManager _configManager = null!;
    private IAudioManager _audioManager = null!;
    private IControllerConnection? _connection;
    private ButtonPressDetector _buttonPressDetector = null!;
    private SliderProcessor _sliderProcessor = null!;
    private SessionMonitor _sessionMonitor = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Config
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Volco", "config.json");
        _configManager = new ConfigManager(configPath);
        _configManager.Load();
        _configManager.ConfigChanged += OnConfigChanged;

        // Audio
        _audioManager = new AudioManager();
        _sessionMonitor = new SessionMonitor(_audioManager);

        // Input processing
        _buttonPressDetector = new ButtonPressDetector();
        _buttonPressDetector.PressDetected += OnButtonPressDetected;
        _sliderProcessor = new SliderProcessor(_audioManager, _configManager, _sessionMonitor);

        // Connection
        StartConnection();

        // Tray icon
        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon();
        _trayIcon.ToolTipText = "Volco";

        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "Open Settings" };
        openItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(openItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) =>
        {
            _connection?.Dispose();
            _sessionMonitor.Dispose();
            _trayIcon?.Dispose();
            Exit();
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextFlyout = menu;
        _trayIcon.LeftClickCommand = new RelayCommand(ShowSettings);
    }

    private void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_configManager, _audioManager);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private void StartConnection()
    {
        _connection?.Dispose();
        var config = _configManager.Config.Connection;
        _connection = config.Type switch
        {
            ConnectionType.Serial => new SerialConnection(config.Serial),
            ConnectionType.Udp => new UdpConnection(config.Udp),
            _ => null
        };

        if (_connection == null) return;

        _connection.MessageReceived += OnMessageReceived;
        _connection.Connect();
    }

    private void OnMessageReceived(object? sender, ControllerMessage msg)
    {
        switch (msg)
        {
            case SliderMessage slider:
                _sliderProcessor.HandleSliderEvent(slider.SliderIndex, slider.Value);
                break;
            case ButtonMessage button:
                _buttonPressDetector.HandleButtonEvent(button.ButtonIndex, button.State);
                break;
        }
    }

    private void OnButtonPressDetected(object? sender, ButtonPressEventArgs e)
    {
        var config = _configManager.Config;
        if (e.ButtonIndex < 0 || e.ButtonIndex >= config.Buttons.Count) return;

        var buttonConfig = config.Buttons[e.ButtonIndex];
        var actionConfig = e.PressType switch
        {
            PressType.Single => buttonConfig.SinglePress,
            PressType.Double => buttonConfig.DoublePress,
            PressType.Long => buttonConfig.LongPress,
            _ => null
        };

        if (actionConfig == null) return;
        var action = ActionFactory.Create(actionConfig, _audioManager, _configManager);
        action?.Execute();
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Restart connection if connection config might have changed
        StartConnection();
    }

    private class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged;
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Volco/Volco.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Volco/App.xaml src/Volco/App.xaml.cs
git commit -m "feat: wire up App entry point with tray icon, connection, and input processing"
```

---

### Task 18: Run All Tests and Final Verification

- [ ] **Step 1: Run the full test suite**

```bash
dotnet test Volco.sln -v n
```
Expected: All tests pass.

- [ ] **Step 2: Verify clean build**

```bash
dotnet build Volco.sln --configuration Release
```
Expected: Build succeeded with 0 errors.

- [ ] **Step 3: Commit any remaining changes**

```bash
git status
```
If there are any uncommitted changes, stage and commit them.

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "chore: final build verification — all tests passing"
```
