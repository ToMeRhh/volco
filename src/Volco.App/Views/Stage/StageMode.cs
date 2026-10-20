namespace Volco.App.Views.Stage;

/// <summary>Which page the stage decorates (spec docs/design/hardware-page.md §3.3): the same
/// tiles, with their actions (Controls) or their wiring (Hardware).</summary>
public enum StageMode
{
    Controls,
    Hardware,
}
