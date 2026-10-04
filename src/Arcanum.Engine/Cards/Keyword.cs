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
    /// <summary>Protection from a color (rule 702.16): the same, against sources of that color.</summary>
    ProtectionFromWhite,
    ProtectionFromBlue,
    ProtectionFromBlack,
    ProtectionFromRed,
    ProtectionFromGreen,
    /// <summary>"If you control three or more artifacts, legendaries, and/or Sagas, you have an enduring story for the rest of the game."</summary>
    Storied,
    /// <summary>"If you control ten or more permanents, you get the city's blessing for the rest of the game" (rule 702.131).</summary>
    Ascend,
    /// <summary>Can block or be blocked only by creatures with shadow (rule 702.28).</summary>
    Shadow,
    /// <summary>Landwalk (rule 702.14): can't be blocked as long as the defending player controls a land of that type.</summary>
    Islandwalk,
    Swampwalk,
    Forestwalk,
    Mountainwalk,
    Plainswalk,
    /// <summary>When cast, exile cards from the top until a cheaper nonland card, which may be cast free (rule 702.85).</summary>
    Cascade,
    /// <summary>"Can't be blocked by more than one creature."</summary>
    CantBeBlockedByMoreThanOne,
    /// <summary>Nonbasic landwalk (rule 702.14c): can't be blocked as long as the defending player controls a nonbasic land.</summary>
    NonbasicLandwalk,
    /// <summary>"If this creature would untap during your untap step, remove a +1/+1 counter from it instead. If you do, untap it."</summary>
    UntapsByRemovingCounter,
    /// <summary>"Assigns combat damage equal to its toughness rather than its power" (rule 510.1c).</summary>
    AssignsDamageByToughness,
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
        Keyword.ProtectionFromWhite => "Protection from white",
        Keyword.ProtectionFromBlue => "Protection from blue",
        Keyword.ProtectionFromBlack => "Protection from black",
        Keyword.ProtectionFromRed => "Protection from red",
        Keyword.ProtectionFromGreen => "Protection from green",
        Keyword.MustBeBlocked => "Must be blocked",
        Keyword.CantBeBlockedByMoreThanOne => "Can't be blocked by more than one creature",
        Keyword.NonbasicLandwalk => "Nonbasic landwalk",
        Keyword.UntapsByRemovingCounter => "Untaps only by removing a +1/+1 counter",
        Keyword.AssignsDamageByToughness => "Assigns combat damage equal to its toughness",
        _ => keyword.ToString(),
    };

    /// <summary>The protection keyword for a color letter (W, U, B, R, G).</summary>
    public static Keyword? ProtectionFrom(string color) => color switch
    {
        "W" => Keyword.ProtectionFromWhite, "U" => Keyword.ProtectionFromBlue, "B" => Keyword.ProtectionFromBlack,
        "R" => Keyword.ProtectionFromRed, "G" => Keyword.ProtectionFromGreen, _ => null,
    };

    private static string Normalize(string name) => name.Replace(" ", "").Replace("-", "").Replace("'", "").ToLowerInvariant();
}
