using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Input.Actions;
using Xunit;

namespace Volco.Core.Tests.Input.Actions;

public class ActionValidatorTests
{
    private const string FullPathMessage = "Enter the full path, starting with a drive letter, e.g. C:\\…";

    private static ActionValidationContext Ctx(AudioFlow? flow = AudioFlow.Render, bool fileExists = true)
        => new(new[] { "default", "work" }, _ => flow, _ => fileExists);

    private static DeviceRefConfig Dev(string id, string name = "Speakers") => new() { Id = id, FriendlyName = name };

    private static ButtonActionConfig Script(string path, ButtonActionType type = ButtonActionType.RunScript)
        => new() { Action = type, Command = new CommandConfig { Path = path } };

    private static void AssertOnly(IReadOnlyDictionary<string, string> result, string key, string message)
    {
        var pair = Assert.Single(result);
        Assert.Equal(key, pair.Key);
        Assert.Equal(message, pair.Value);
    }

    [Fact]
    public void MuteSpecificDevice_NoDevice()
        => AssertOnly(ActionValidator.Validate(new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice }, Ctx()),
            "device", "Choose a device.");

    [Fact]
    public void SetOutput_CaptureDevice()
        => AssertOnly(ActionValidator.Validate(
                new ButtonActionConfig { Action = ButtonActionType.SetOutputDevice, Device = Dev("mic") }, Ctx(AudioFlow.Capture)),
            "device", "Choose an output device.");

    [Fact]
    public void Toggle_SameDeviceTwice()
        => AssertOnly(ActionValidator.Validate(
                new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice, Device = Dev("a"), DeviceB = Dev("a", "Other") }, Ctx()),
            "deviceB", "Choose a different device from A.");

    [Fact]
    public void Toggle_DifferentIdsSameName_IsValid()
        => Assert.Empty(ActionValidator.Validate(
            new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice, Device = Dev("a"), DeviceB = Dev("b") }, Ctx()));

    [Fact]
    public void Toggle_MissingB()
        => AssertOnly(ActionValidator.Validate(
                new ButtonActionConfig { Action = ButtonActionType.ToggleOutputDevice, Device = Dev("a") }, Ctx()),
            "deviceB", "Choose a device.");

    [Fact]
    public void GoToPreset_UnknownId()
        => AssertOnly(ActionValidator.Validate(
                new ButtonActionConfig { Action = ButtonActionType.GoToPreset, PresetId = "gone" }, Ctx()),
            "preset", "Choose a mode.");

    [Fact]
    public void OpenProgram_EmptyPath()
        => AssertOnly(ActionValidator.Validate(Script("", ButtonActionType.OpenProgram), Ctx()),
            "path", "Choose the program to open.");

    [Fact]
    public void RunScript_EmptyPath()
        => AssertOnly(ActionValidator.Validate(Script(""), Ctx()), "path", "Choose the script to run.");

    [Fact]
    public void RunScript_RelativePath()
        => AssertOnly(ActionValidator.Validate(Script("scripts\\a.ps1"), Ctx()), "path", FullPathMessage);

    [Fact]
    public void RunScript_EnvVarPath()
        => AssertOnly(ActionValidator.Validate(Script("\"%USERPROFILE%\\x.bat\""), Ctx()), "path", FullPathMessage);

    [Fact]
    public void RunScript_BadChars()
        => AssertOnly(ActionValidator.Validate(Script("C:\\a|b.bat"), Ctx()),
            "path", "The path contains characters Windows doesn’t allow (< > | ? *).");

    [Fact]
    public void RunScript_UnsupportedType()
        => AssertOnly(ActionValidator.Validate(Script("C:\\s\\notes.txt"), Ctx()),
            "path", "Volco runs .bat, .cmd, .ps1, .py, .js and .exe files.");

    [Fact]
    public void Path_Missing()
        => AssertOnly(ActionValidator.Validate(Script("C:\\s\\a.bat"), Ctx(fileExists: false)),
            "path", "This file doesn’t exist.");

    [Fact]
    public void OpenProgram_Url()
        => Assert.Empty(ActionValidator.Validate(Script("steam://rungameid/570", ButtonActionType.OpenProgram), Ctx(fileExists: false)));

    [Fact]
    public void UncPath()
        => Assert.Empty(ActionValidator.Validate(Script("\\\\nas\\share\\a.bat"), Ctx()));

    [Fact]
    public void WorkingDirectory_Relative()
    {
        var action = Script("C:\\s\\a.bat");
        action.Command!.WorkingDirectory = "tools";
        AssertOnly(ActionValidator.Validate(action, Ctx()),
            "workingDirectory", "Enter the folder’s full path, or leave it empty.");
    }

    [Fact]
    public void UnpluggedDevice_IsValid()
        => Assert.Empty(ActionValidator.Validate(
            new ButtonActionConfig { Action = ButtonActionType.MuteSpecificDevice, Device = Dev("gone") }, Ctx(null)));

    [Fact]
    public void Media_NoApp()
        => Assert.Empty(ActionValidator.Validate(new ButtonActionConfig { Action = ButtonActionType.MediaPlayPause }, Ctx()));

    [Fact]
    public void None()
        => Assert.Empty(ActionValidator.Validate(new ButtonActionConfig(), Ctx()));
}
