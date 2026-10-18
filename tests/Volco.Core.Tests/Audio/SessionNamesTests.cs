using Volco.Core.Audio;
using Xunit;

namespace Volco.Core.Tests.Audio;

public class SessionNamesTests
{
    [Fact]
    public void Pick_SessionNameFirst()
    {
        Assert.Equal("Spotify Music", SessionNames.Pick("  Spotify Music ", "Spotify", "Spotify.exe"));
    }

    [Fact]
    public void Pick_SkipsResourceStrings()
    {
        Assert.Equal("Discord", SessionNames.Pick(@"@%SystemRoot%\x.dll,-1", "Discord", "Discord.exe"));
    }

    [Fact]
    public void Pick_DescriptionThenImageName()
    {
        Assert.Equal("Google Chrome", SessionNames.Pick("", "Google Chrome", "chrome.exe"));
        Assert.Equal("spotify", SessionNames.Pick("", "  ", "spotify.exe"));
        Assert.Equal("Spotify", SessionNames.Pick("", null, "Spotify.exe"));
    }

    [Fact]
    public void IconSource_SessionPathElseExe()
    {
        Assert.Equal(@"C:\Apps\app.dll,-3", SessionNames.IconSource(@"C:\Apps\app.dll,-3", @"C:\Apps\app.exe"));
        Assert.Equal(@"C:\Apps\app.exe", SessionNames.IconSource("", @"C:\Apps\app.exe"));
        Assert.Equal(@"C:\Apps\app.exe", SessionNames.IconSource(@"@%SystemRoot%\x.dll,-1", @"C:\Apps\app.exe"));
    }

    [Fact]
    public void IconSource_NoPath_Empty()
    {
        Assert.Equal("", SessionNames.IconSource("", null));
        Assert.Equal("", SessionNames.IconSource(@"@%SystemRoot%\x.dll,-1", ""));
    }
}
