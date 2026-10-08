using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Media;
using Volco.Core.Programs;
using Volco.Core.Runtime;
using Volco.Core.Scripts;

namespace Volco.Core.Input.Actions;

/// <summary>Where a press came from: the button's input number, the gesture, and the preset's
/// name. A Run a script action passes them to the script (custom-actions spec §5.1).</summary>
public sealed record ActionOrigin(int Button, PressType Gesture, string PresetName)
{
    /// <summary>The environment a script run gets: VOLCO_BUTTON, VOLCO_GESTURE, VOLCO_PRESET.</summary>
    public IReadOnlyDictionary<string, string> Environment() => new Dictionary<string, string>
    {
        ["VOLCO_BUTTON"] = Button.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["VOLCO_GESTURE"] = Gesture switch { PressType.Double => "double", PressType.Long => "long", _ => "single" },
        ["VOLCO_PRESET"] = PresetName,
    };
}

/// <summary>What actions act on. Media, programs and scripts are null where the platform has
/// none; their actions are then not created.</summary>
public sealed record ActionServices(IAudioEngine Audio, IPresetController? Presets, IMediaSessions? Media = null,
                                    ProgramOpener? Programs = null, ScriptRunner? Scripts = null);

/// <summary>Play/Pause, Next, Previous or Stop, to the chosen app or the current one.</summary>
public sealed class MediaAction : IButtonAction
{
    private readonly IMediaSessions _media;
    private readonly MediaAppConfig? _app;
    private readonly MediaCommand _command;

    public MediaAction(IMediaSessions media, MediaAppConfig? app, MediaCommand command)
    {
        _media = media;
        _app = app;
        _command = command;
    }

    public void Execute() => _media.Send(_app is { Id.Length: > 0 } ? _app.Id : null, _command);
}

/// <summary>Brings the program to the front, or starts it (off the input thread: the shell may
/// wait on a UAC prompt).</summary>
public sealed class OpenProgramAction : IButtonAction
{
    private readonly ProgramOpener _programs;
    private readonly CommandConfig? _command;

    public OpenProgramAction(ProgramOpener programs, CommandConfig? command)
    {
        _programs = programs;
        _command = command;
    }

    public void Execute()
    {
        if (_command is null || _command.Path.Trim().Length == 0)
        {
            AppLog.Info("Open a program: no program chosen");
            return;
        }
        var command = _command;
        Task.Run(() =>
        {
            try { _programs.Open(command); }
            catch (Exception ex) { AppLog.Error($"Open a program: {command.Path} failed ({ex.GetType().Name}: {ex.Message})"); }
        });
    }
}

/// <summary>Runs the action's script with the press's environment.</summary>
public sealed class RunScriptAction : IButtonAction
{
    private readonly ScriptRunner _scripts;
    private readonly ButtonActionConfig _action;
    private readonly IReadOnlyDictionary<string, string> _environment;

    public RunScriptAction(ScriptRunner scripts, ButtonActionConfig action, ActionOrigin? origin)
    {
        _scripts = scripts;
        _action = action;
        _environment = origin?.Environment() ?? new Dictionary<string, string>();
    }

    public void Execute() => _scripts.Run(_action, _environment);
}
