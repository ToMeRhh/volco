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

    public GeneralPage(GeneralViewModel viewModel) : this()
    {
        DataContext = viewModel;
        if (viewModel.ShowUpdates)
        {
            Updates = new UpdatesPanel(viewModel.ConfigManager);
            this.FindControl<ContentControl>("UpdatesHost")!.Content = Updates;
        }
    }

    /// <summary>The Updates panel, when this is a release build.</summary>
    internal UpdatesPanel? Updates { get; }
}
