using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Updates;

namespace Volco.App.ViewModels;

/// <summary>
/// The sidebar's About lines, above the connection state: the app's version (with the build
/// details as its tooltip on non-release builds), a link to the project on GitHub, and the
/// third-party notices dialog.
/// </summary>
public partial class AboutViewModel : ObservableObject
{
    /// <summary>Whether the third-party notices dialog is open.</summary>
    [ObservableProperty]
    private bool _isNoticesOpen;

    public AboutViewModel(bool isRelease)
    {
        VersionText = $"Volco {BuildInfo.FormatVersion(isRelease, BuildInfo.Version, BuildInfo.Commit)}";
        BuildDetails = isRelease ? null : BuildInfo.BuildDetails;
    }

    public AboutViewModel() : this(BuildInfo.IsRelease) { }

    /// <summary>"Volco v1.2.3" on releases, "Volco dev a1b2c3d" on other builds.</summary>
    public string VersionText { get; }

    /// <summary>
    /// Branch, commit, build time and origin, one per line, on non-release builds; null on
    /// releases, which show just the version.
    /// </summary>
    public string? BuildDetails { get; }

    /// <summary>The project's GitHub page.</summary>
    public Uri RepoUri { get; } = new(BuildInfo.RepoUrl);

    [RelayCommand]
    private void OpenNotices() => IsNoticesOpen = true;

    [RelayCommand]
    private void CloseNotices() => IsNoticesOpen = false;
}
