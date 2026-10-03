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
    Hexproof,
    Shroud,
    /// <summary>Can be cast any time its controller could cast an instant (rule 702.8).</summary>
    Flash,
    /// <summary>"Whenever you cast a noncreature spell, this creature gets +1/+1 until end of turn" (rule 702.108).</summary>
    Prowess,
    /// <summary>"This object is every creature type" (rule 702.73).</summary>
    Changeling,
    /// <summary>"This creature can't block." (not a printed keyword; used by static abilities).</summary>
    CantBlock,
    /// <summary>"This creature can't be blocked."</summary>
    CantBeBlocked,
    /// <summary>"This creature can't attack."</summary>
    CantAttack,
    /// <summary>"This permanent doesn't untap during its controller's untap step."</summary>
    DoesntUntap,
    /// <summary>"Must be blocked this turn if able."</summary>
    MustBeBlocked,
    /// <summary>Protection from everything (rule 702.16): can't be targeted, blocked, damaged, enchanted or equipped.</summary>
    ProtectionFromEverything,
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
        Keyword.CantBlock => "Can't block",
        Keyword.CantBeBlocked => "Can't be blocked",
        Keyword.CantAttack => "Can't attack",
        Keyword.DoesntUntap => "Doesn't untap",
        Keyword.ProtectionFromEverything => "Protection from everything",
        Keyword.MustBeBlocked => "Must be blocked",
        _ => keyword.ToString(),
    };

    private static string Normalize(string name) => name.Replace(" ", "").Replace("-", "").Replace("'", "").ToLowerInvariant();
}
