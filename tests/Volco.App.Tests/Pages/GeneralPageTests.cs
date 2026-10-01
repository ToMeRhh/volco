using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Volco.App.Views.Pages;
using Volco.Core.Diagnostics;
using Xunit;

namespace Volco.App.Tests.Pages;

public class GeneralPageTests
{
    [AvaloniaFact]
    public void GeneralPage_DebugToggle_WritesConfigAndAppLog()
    {
        using var rig = new TestRig();
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var page = new GeneralPage(vm.General);
        new Window { Content = page }.Show();
        var toggle = page.FindControl<ToggleSwitch>("DebugToggle")!;
        try
        {
            Assert.False(rig.Config.Config.DebugLogging);

            toggle.IsChecked = true;

            Assert.True(rig.Config.Config.DebugLogging);
            Assert.True(AppLog.IsDebugEnabled);

            toggle.IsChecked = false;

            Assert.False(rig.Config.Config.DebugLogging);
            Assert.False(AppLog.IsDebugEnabled);
        }
        finally { AppLog.IsDebugEnabled = false; }
    }
}
