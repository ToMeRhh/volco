using System.Text;
using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class ScreenTextTests
{
    [Fact]
    public void Sanitize_RemovesHebrewCombiningMarks_KeepsLetters()
        => Assert.Equal("שלום", ScreenText.Sanitize("שָׁלוֹם"));

    [Fact]
    public void Sanitize_KeepsMaqafAndLatin()
        => Assert.Equal("בית־ספר Desk", ScreenText.Sanitize("בית־ספר Desk"));   // U+05BE is punctuation, not a mark

    [Fact]
    public void ToWireBytes_ShortAscii_IsUnchanged()
        => Assert.Equal(Encoding.UTF8.GetBytes("Speakers"), ScreenText.ToWireBytes("Speakers"));

    [Fact]
    public void ToWireBytes_Null_IsEmpty() => Assert.Empty(ScreenText.ToWireBytes(null));

    [Fact]
    public void ToWireBytes_LongAscii_IsCutAt60Bytes()
        => Assert.Equal(60, ScreenText.ToWireBytes(new string('x', 100)).Length);

    [Fact]
    public void ToWireBytes_Hebrew_CutsOnACharacterBoundary()
    {
        var bytes = ScreenText.ToWireBytes(new string('ש', 40));          // 2 bytes each
        Assert.Equal(60, bytes.Length);
        Assert.Equal(new string('ש', 30), Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ToWireBytes_Emoji_NeverSplitsASurrogatePair()
    {
        var text = new string('a', 58) + "🔊";                                  // 58 + 4 bytes
        var bytes = ScreenText.ToWireBytes(text);
        Assert.Equal(58, bytes.Length);
        Assert.Equal(new string('a', 58), Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ToWireBytes_LongMixedHebrewName_CutsOnACharacterBoundary()
    {
        var name = "רמקולים בחדר העבודה (Realtek High Definition Audio) 10:30";
        var bytes = ScreenText.ToWireBytes(name);
        Assert.True(bytes.Length <= 60);
        var decoded = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('�', decoded);
        Assert.StartsWith(decoded, name);
    }
}
