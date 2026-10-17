namespace Volco.App.ViewModels.Search;

/// <summary>A fuzzy match: its score and the positions of the matched characters in the original text.</summary>
public readonly record struct FuzzyMatch(double Score, IReadOnlyList<int> Indexes);

/// <summary>Fuzzy search for the action and target dialogs (spec controls-page §6.3).</summary>
public static class FuzzyMatcher
{
    private const string WordSeparators = " /(._-·";
    private const double NameBonus = 5;

    /// <summary>
    /// Every query character (whitespace removed, case-insensitive) must appear in <paramref name="text"/> in order,
    /// taking the first occurrence after the previous match. +1 per character, +2 at a word start,
    /// +3 directly after the previous match, minus 0.01 per character of text. Null when it does not match.
    /// </summary>
    public static FuzzyMatch? Match(string query, string text)
    {
        var indexes = new List<int>();
        double score = 0;
        var prev = -1;
        foreach (var q in query)
        {
            if (char.IsWhiteSpace(q)) continue;
            var found = -1;
            for (var i = prev + 1; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(q)) { found = i; break; }
            }
            if (found < 0) return null;
            score += 1;
            if (found == 0 || WordSeparators.Contains(text[found - 1])) score += 2;
            if (prev >= 0 && found == prev + 1) score += 3;
            indexes.Add(found);
            prev = found;
        }
        if (indexes.Count == 0) return new FuzzyMatch(0, indexes);
        return new FuzzyMatch(score - 0.01 * text.Length, indexes);
    }

    /// <summary>The name match (+5, with highlight indexes), else a match on the secondary text (no indexes), else null.</summary>
    public static FuzzyMatch? MatchItem(string query, string name, string? extra)
    {
        var byName = Match(query, name);
        if (byName is { } n) return n with { Score = n.Score + NameBonus };
        if (extra is not null && Match(query, extra) is { } e) return e with { Indexes = [] };
        return null;
    }
}
