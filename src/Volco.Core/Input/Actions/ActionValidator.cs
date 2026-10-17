using System.Text.RegularExpressions;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Programs;

namespace Volco.Core.Input.Actions;

/// <summary>What <see cref="ActionValidator"/> needs to know about the world.</summary>
/// <param name="PresetIds">The ids of the existing presets.</param>
/// <param name="DeviceFlow">The flow of a plugged-in device; null for one that isn't plugged in.</param>
/// <param name="FileExists">Whether a file exists on this PC.</param>
public sealed record ActionValidationContext(
    IReadOnlyCollection<string> PresetIds,
    Func<DeviceRefConfig, AudioFlow?> DeviceFlow,
    Func<string, bool> FileExists)
{
    /// <summary>Only the rules that need no system: every device is unplugged, every file exists.</summary>
    public static ActionValidationContext Structural(AppConfig config)
        => new(config.Presets.Select(p => p.Id).ToList(), _ => null, _ => true);
}

/// <summary>The rules for a button action's settings, shared by the UI (to mark fields) and the
/// runtime. An unplugged device is not a problem here.</summary>
public static partial class ActionValidator
{
    public const string Device = "device";
    public const string DeviceB = "deviceB";
    public const string Preset = "preset";
    public const string Path = "path";
    public const string WorkingDirectory = "workingDirectory";

    private static readonly string[] ScriptExtensions = { ".bat", ".cmd", ".ps1", ".py", ".js", ".exe" };

    [GeneratedRegex(@"^([A-Za-z]:\\|\\\\[^\\]+\\[^\\]+)")]
    private static partial Regex FullPath();

    /// <summary>Field key to message, one message per field (the first rule that fails); empty means valid.</summary>
    public static IReadOnlyDictionary<string, string> Validate(ButtonActionConfig action, ActionValidationContext ctx)
    {
        var problems = new Dictionary<string, string>();
        switch (action.Action)
        {
            case ButtonActionType.MuteSpecificDevice:
                if (!IsChosen(action.Device)) problems[Device] = "Choose a device.";
                break;

            case ButtonActionType.SetOutputDevice:
                CheckOutput(action.Device, Device, ctx, problems);
                break;

            case ButtonActionType.ToggleOutputDevice:
                CheckOutput(action.Device, Device, ctx, problems);
                if (!IsChosen(action.DeviceB)) problems[DeviceB] = "Choose a device.";
                else if (IsChosen(action.Device) && Same(action.Device!, action.DeviceB!))
                    problems[DeviceB] = "Choose a different device from A.";
                else CheckOutput(action.DeviceB, DeviceB, ctx, problems);
                break;

            case ButtonActionType.GoToPreset:
                if (action.PresetId is null || !ctx.PresetIds.Contains(action.PresetId))
                    problems[Preset] = "Choose a mode.";
                break;

            case ButtonActionType.OpenProgram:
            case ButtonActionType.RunScript:
                CheckCommand(action, ctx, problems);
                break;
        }
        return problems;
    }

    private static bool IsChosen(DeviceRefConfig? device)
        => device is not null && (device.Id.Length > 0 || device.FriendlyName.Length > 0);

    private static bool Same(DeviceRefConfig a, DeviceRefConfig b)
        => a.Id.Length > 0 && a.Id == b.Id
           || a.FriendlyName.Length > 0 && string.Equals(a.FriendlyName, b.FriendlyName, StringComparison.OrdinalIgnoreCase);

    private static void CheckOutput(DeviceRefConfig? device, string key, ActionValidationContext ctx,
                                    Dictionary<string, string> problems)
    {
        if (!IsChosen(device)) problems[key] = "Choose a device.";
        else if (ctx.DeviceFlow(device!) == AudioFlow.Capture) problems[key] = "Choose an output device.";
    }

    private static string Unquote(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
        return text;
    }

    private static void CheckCommand(ButtonActionConfig action, ActionValidationContext ctx,
                                     Dictionary<string, string> problems)
    {
        var isScript = action.Action == ButtonActionType.RunScript;
        var path = Unquote(action.Command?.Path ?? "");
        if (path.Length == 0)
            problems[Path] = isScript ? "Choose the script to run." : "Choose the program to open.";
        else if (!CommandPath.IsUrl(path))
        {
            if (!FullPath().IsMatch(path))
                problems[Path] = "Enter the full path, starting with a drive letter, e.g. C:\\…";
            else if (path[2..].IndexOfAny(new[] { '<', '>', '|', '?', '*' }) >= 0)
                problems[Path] = "The path contains characters Windows doesn’t allow (< > | ? *).";
            else if (isScript && Array.IndexOf(ScriptExtensions, CommandPath.Extension(path)) < 0)
                problems[Path] = "Volco runs .bat, .cmd, .ps1, .py, .js and .exe files.";
            else if (!ctx.FileExists(path))
                problems[Path] = "This file doesn’t exist.";
        }

        var directory = Unquote(action.Command?.WorkingDirectory ?? "");
        if (directory.Length > 0 && !FullPath().IsMatch(directory))
            problems[WorkingDirectory] = "Enter the folder’s full path, or leave it empty.";
    }
}
