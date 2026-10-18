using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Media;
using Volco.Core.Programs;
using Volco.Core.Runtime;
using Volco.Core.Scripts;

namespace Volco.Core.Input.Actions;

/// <summary>Where a press came from: the button's input number, the gesture, and the mode's
/// name. A Run a script action passes them to the script (custom-actions spec §5.1).</summary>
public sealed record ActionOrigin(int Button, PressType Gesture, string ModeName)
{
    /// <summary>Told why a press did nothing (<see cref="ControllerDisplay.NotRunning"/> and the
    /// like), at once or later from another thread; the button shows it (custom-actions spec §11).</summary>
    public Action<string>? ReportError { get; init; }

    /// <summary>The environment a script run gets: VOLCO_BUTTON, VOLCO_GESTURE, VOLCO_MODE.</summary>
    public IReadOnlyDictionary<string, string> Environment() => new Dictionary<string, string>
    {
        ["VOLCO_BUTTON"] = Button.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["VOLCO_GESTURE"] = Gesture switch { PressType.Double => "double", PressType.Long => "long", _ => "single" },
        ["VOLCO_MODE"] = ModeName,
    };
}

/// <summary>What actions act on. Media, programs and scripts are null where the platform has
/// none; their actions are then not created.</summary>
public sealed record ActionServices(IAudioEngine Audio, IModeController? Modes, IMediaSessions? Media = null,
                                    ProgramOpener? Programs = null, ScriptRunner? Scripts = null);

/// <summary>Play/Pause, Next, Previous or Stop, to the chosen app or the current one.</summary>
public sealed class MediaAction : IButtonAction
{
    private readonly IMediaSessions _media;
    private readonly MediaAppConfig? _app;
    private readonly MediaCommand _command;
    private readonly Action<string>? _reportError;

    public MediaAction(IMediaSessions media, MediaAppConfig? app, MediaCommand command, Action<string>? reportError = null)
    {
        _media = media;
        _app = app;
        _command = command;
        _reportError = reportError;
    }

    public void Execute()
    {
        var id = _app is { Id.Length: > 0 } ? _app.Id : null;
        // From the snapshot, so a missing app is reported at once rather than after a WinRT round trip.
        if (id is null ? _media.CurrentAppId is null
                       : !_media.GetApps().Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            AppLog.Info($"Media {_command}: {(id is null ? "nothing is playing" : $"{id} is not running")}");
            _reportError?.Invoke(id is null ? ControllerDisplay.NoMedia : ControllerDisplay.NotRunning);
            return;
        }
        _media.Send(id, _command).ContinueWith(t =>
        {
            if (!(t.IsCompletedSuccessfully && t.Result)) _reportError?.Invoke(ControllerDisplay.NotAvailable);
        }, TaskContinuationOptions.ExecuteSynchronously);
    }
}

/// <summary>Brings the program to the front, or starts it (off the input thread: the shell may
/// wait on a UAC prompt).</summary>
public sealed class OpenProgramAction : IButtonAction
{
    private readonly ProgramOpener _programs;
    private readonly CommandConfig? _command;
    private readonly Action<string>? _reportError;

    public OpenProgramAction(ProgramOpener programs, CommandConfig? command, Action<string>? reportError = null)
    {
        _programs = programs;
        _command = command;
        _reportError = reportError;
    }

    public void Execute()
    {
        if (_command is null || _command.Path.Trim().Length == 0)
        {
            AppLog.Info("Open a program: no program chosen");
            _reportError?.Invoke(ControllerDisplay.NoFile);
            return;
        }
        var command = _command;
        Task.Run(() =>
        {
            bool opened;
            try { opened = _programs.Open(command); }
            catch (Exception ex)
            {
                AppLog.Error($"Open a program: {command.Path} failed ({ex.GetType().Name}: {ex.Message})");
                opened = false;
            }
            if (!opened) _reportError?.Invoke(ControllerDisplay.CantOpen);
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
