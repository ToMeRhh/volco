using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Inspector;
using Volco.App.Views.Stage;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;

namespace Volco.App.Tests.Shell;

/// <summary>Going back to the Controller page from General is quick (user, 2026-10-22): every
/// control of the page is styled anew when it returns, so the page holds no unseen copy of an
/// inspector (a copy per tile made the return several times slower), and returning measures nothing.</summary>
public sealed class ControllerPageReturnTests : IDisposable
{
    public ControllerPageReturnTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(ControllerPresets.ApplyTo(new ControllerConfig(), Diy));

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ReturningToTheControllerPageMeasuresNothing()
    {
        using var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, 5).Select(i => new SliderConfig { Index = i }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        using var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var window = new SettingsWindow(vm, rig.Runtime) { Width = 1400, Height = 900, AllowClose = true };
        window.Show();
        try
        {
            window.ShowPage("controller");
            rig.Connection!.SetStatus(TransportStatus.Open);
            rig.Connection.RaiseMessage(new HelloMessage(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored,
                ControllerConfigCodec.Crc32(DiyBlob), 5, new[] { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair }));
            for (var i = 0; i < 500 && !vm.Controller.CanEdit; i++) { Settle(window); Thread.Sleep(10); }
            Assert.True(vm.Controller.CanEdit);
            vm.Controls.Select(vm.Controls.Sliders.Items[0]);
            Settle(window);
            var page = Assert.IsType<StagePage>(window.CurrentPage);
            Assert.True(page.InspectorTopHeight > 0);
            Assert.True(page.ProbeMeasurements >= 8, $"{page.ProbeMeasurements} measurements");   // every tile once

            var before = page.ProbeMeasurements;
            for (var i = 0; i < 3; i++)
            {
                window.ShowPage("general");
                Settle(window);
                window.ShowPage("controller");
                Settle(window);
                Assert.DoesNotContain(page.GetVisualDescendants().OfType<TileInspector>(), t => t.IsProbe);
            }
            Assert.Equal(before, page.ProbeMeasurements);
        }
        finally
        {
            window.Close();
        }
    }
}
