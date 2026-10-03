// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Cards;

/// <summary>Keyword abilities the engine implements directly (rule 702).</summary>
public enum Keyword
{
    Flying,
    Reach,
    Vigilance,
    Haste,
    Defender,
    Menace,
    Trample,
    Deathtouch,
    Lifelink,
    FirstStrike,
    DoubleStrike,
    Indestructible,
}

public static class Keywords
{
    private static readonly Dictionary<string, Keyword> ByName = Enum.GetValues<Keyword>()
        .ToDictionary(k => Normalize(k.ToString()), k => k);

    /// <summary>Printed names of supported keywords ("First strike", ...), for card support checks.</summary>
    public static IReadOnlyCollection<string> SupportedNames { get; } = Enum.GetValues<Keyword>().Select(DisplayName).ToList();

    public static bool TryParse(string name, out Keyword keyword) => ByName.TryGetValue(Normalize(name), out keyword);

    public static IReadOnlySet<Keyword> ParseAll(IEnumerable<string> names)
    {
        var set = new HashSet<Keyword>();
        foreach (var name in names)
            if (TryParse(name, out var k)) set.Add(k);
        return set;
    }

    public static string DisplayName(Keyword keyword) => keyword switch
    {
        Keyword.FirstStrike => "First strike",
        Keyword.DoubleStrike => "Double strike",
        _ => keyword.ToString(),
    };

    private static string Normalize(string name) => name.Replace(" ", "").Replace("-", "").ToLowerInvariant();
}
