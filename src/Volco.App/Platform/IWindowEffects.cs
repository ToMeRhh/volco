namespace Volco.App.Platform;

public interface IWindowEffects
{
    /// Make the window ignore all pointer input so clicks reach what is beneath it.
    /// Called once, after the window's platform handle exists. May no-op on a platform
    /// that has not implemented it yet.
    void MakeClickThrough(Avalonia.Controls.Window window);
}
