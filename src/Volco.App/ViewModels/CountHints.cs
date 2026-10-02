namespace Volco.App.ViewModels;

/// <summary>The line under the Sliders/Buttons header when the controller's count and the number
/// of cards disagree (spec §6.3). Cards are numbered from 0, as on the cards.</summary>
internal static class CountHints
{
    public static string Text(string noun, int? controllerCount, int cards)
    {
        if (controllerCount is not int n || n == cards) return "";
        var nouns = n == 1 ? noun : noun + "s";
        if (cards < n) return $"Your controller has {n} {nouns}; {cards} {(cards == 1 ? "is" : "are")} set up.";
        return cards - n == 1
            ? $"Your controller has {n} {nouns}; card {n} does nothing."
            : $"Your controller has {n} {nouns}; cards {n}–{cards - 1} do nothing.";
    }
}
