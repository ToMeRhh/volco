using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Volco.App.Controls;
using Volco.App.ViewModels;

namespace Volco.App.Views.Pages;

public partial class GeneralPage : UserControl
{
    public GeneralPage()
    {
        AvaloniaXamlLoader.Load(this);
        var expander = this.FindControl<Expander>("NoticesExpander")!;
        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty || !expander.IsExpanded) return;
            var box = this.FindControl<TextBox>("NoticesBox")!;
            if (string.IsNullOrEmpty(box.Text)) box.Text = LoadNotices();   // ~45 KB: loaded on first open
        };
    }

    /// <summary>THIRD-PARTY-NOTICES.txt and the Arimo OFL, embedded from the repository.</summary>
    internal static string LoadNotices()
    {
        static string Read(string name)
        {
            using var s = AssetLoader.Open(new Uri($"avares://Volco.App/Assets/Notices/{name}"));
            return new StreamReader(s).ReadToEnd();
        }
        return Read("THIRD-PARTY-NOTICES.txt") + "\n\n" + new string('=', 78) + "\nArimo — SIL Open Font License 1.1\n"
               + new string('=', 78) + "\n\n" + Read("OFL.txt");
    }

    public GeneralPage(GeneralViewModel viewModel, DevicesViewModel? devices = null) : this()
    {
        DataContext = viewModel;
        if (devices is not null)
        {
            Devices = new DevicesSection(devices);
            this.FindControl<ContentControl>("DevicesHost")!.Content = Devices;
        }
        if (viewModel.ShowUpdates)
        {
            Updates = new UpdatesPanel();
            this.FindControl<ContentControl>("UpdatesHost")!.Content = Updates;
        }
    }

    /// <summary>The Updates panel, when this is a release build.</summary>
    internal UpdatesPanel? Updates { get; }

    /// <summary>The Devices section, when the page was given its view model.</summary>
    internal DevicesSection? Devices { get; }
}
