// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Data.CardData;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Data.Scripts;

/// <summary>Abilities parsed from a card script: the spell effect and the permanent's abilities.</summary>
public sealed record CardScript(
    SpellAbility? Spell,
    IReadOnlyList<AbilityDefinition> Abilities,
    TargetSpec? Aura = null,
    bool EntersTapped = false,
    int EntersWithCounters = 0);

/// <summary>
/// Parses card scripts: small JSON documents describing what a card does. Example:
/// <code>
/// { "spell": { "targets": ["any"], "effects": [{ "damage": 2, "to": "target" }] } }
/// { "abilities": [{ "trigger": "enters", "effects": [{ "draw": 1 }], "text": "When this enters, draw a card." }] }
/// { "abilities": [{ "cost": "{T}", "targets": ["any"], "effects": [{ "damage": 1, "to": "target" }] }] }
/// </code>
/// The full vocabulary is documented in docs/card-scripts.md.
/// </summary>
public static class CardScriptParser
{
    public static CardScript Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        SpellAbility? spell = null;
        if (root.TryGetProperty("spell", out var s))
            spell = new SpellAbility { Targets = Targets(s), Effects = Effects(s), Text = Text(s) };

        var abilities = new List<AbilityDefinition>();
        if (root.TryGetProperty("abilities", out var list))
        {
            foreach (var a in list.EnumerateArray())
            {
                if (a.TryGetProperty("static", out var st))
                {
                    abilities.Add(ParseStatic(st) with { Text = Text(a) });
                }
                else if (a.TryGetProperty("trigger", out var trigger))
                {
                    abilities.Add(new TriggeredAbility
                    {
                        Trigger = ParseTrigger(trigger.GetString()!), Targets = Targets(a), Effects = Effects(a), Text = Text(a),
                    });
                }
                else if (a.TryGetProperty("cost", out var cost))
                {
                    abilities.Add(new ActivatedAbility
                    {
                        Cost = ParseCost(cost.GetString()!), Targets = Targets(a), Effects = Effects(a), Text = Text(a),
                        SorcerySpeed = a.TryGetProperty("sorcery", out var sorcery) && sorcery.GetBoolean(),
                    });
                }
                else throw new FormatException("An ability needs a \"trigger\" or a \"cost\".");
            }
        }
        return new CardScript(
            spell,
            abilities,
            root.TryGetProperty("aura", out var aura) ? ParseTarget(aura.GetString()!) : null,
            root.TryGetProperty("entersTapped", out var tapped) && tapped.GetBoolean(),
            root.TryGetProperty("entersWithCounters", out var counters) ? counters.GetInt32() : 0);
    }

    /// <summary>{ "affects": "creatures:you", "other": true, "subtype": "Goblin", "pump": [1, 1], "keywords": ["Flying"] }</summary>
    private static StaticAbility ParseStatic(JsonElement s)
    {
        var scope = s.GetProperty("affects").GetString() switch
        {
            "self" => AffectedScope.Self,
            "creatures:you" => AffectedScope.YourCreatures,
            "creatures:opponents" => AffectedScope.OpponentsCreatures,
            "creatures" => AffectedScope.AllCreatures,
            "enchanted" => AffectedScope.Enchanted,
            "equipped" => AffectedScope.Equipped,
            var unknown => throw new FormatException($"Unknown static scope '{unknown}'."),
        };
        var filter = new AffectedFilter(
            scope,
            s.TryGetProperty("other", out var other) && other.GetBoolean(),
            s.TryGetProperty("subtype", out var subtype) ? subtype.GetString() : null);
        var pt = s.TryGetProperty("pump", out var pump) ? pump.EnumerateArray().Select(x => x.GetInt32()).ToArray() : new[] { 0, 0 };
        return new StaticAbility(filter, pt[0], pt[1], ParseKeywords(s));
    }

    private static IReadOnlyList<Keyword>? ParseKeywords(JsonElement e) =>
        e.TryGetProperty("keywords", out var k)
            ? k.EnumerateArray().Select(x => Keywords.TryParse(x.GetString()!, out var kw) ? kw : throw new FormatException($"Unknown keyword '{x}'.")).ToList()
            : null;

    private static string Text(JsonElement e) => e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

    private static IReadOnlyList<TargetSpec> Targets(JsonElement e) =>
        e.TryGetProperty("targets", out var t) ? t.EnumerateArray().Select(x => ParseTarget(x.GetString()!)).ToList() : Array.Empty<TargetSpec>();

    /// <summary>"any", "creature", "creature:you", "creature:opponent", "player", "opponent", "spell", ...</summary>
    public static TargetSpec ParseTarget(string text)
    {
        var parts = text.Split(':');
        if (parts[0] == "opponent") return new TargetSpec(TargetKind.Player, ControllerFilter.Opponent);
        if (!Enum.TryParse<TargetKind>(parts[0], ignoreCase: true, out var kind)) throw new FormatException($"Unknown target '{text}'.");
        var controller = parts.Length > 1 ? parts[1] switch
        {
            "you" => ControllerFilter.You,
            "opponent" => ControllerFilter.Opponent,
            _ => throw new FormatException($"Unknown target controller '{parts[1]}'."),
        } : ControllerFilter.Any;
        return new TargetSpec(kind, controller);
    }

    /// <summary>"target" (first target), "target2", "you", "opponents", "everyone", "self", "targetController".</summary>
    public static Subject ParseSubject(string? text) => text switch
    {
        null or "you" => Subject.You,
        "target" => Subject.TargetAt(0),
        "opponents" => Subject.EachOpponent,
        "everyone" => new Subject(SubjectKind.EachPlayer),
        "self" => Subject.Self,
        "targetController" => new Subject(SubjectKind.TargetController),
        _ when text.StartsWith("target") && int.TryParse(text[6..], out int n) => Subject.TargetAt(n - 1),
        _ => throw new FormatException($"Unknown subject '{text}'."),
    };

    public static TriggerEvent ParseTrigger(string text) => text switch
    {
        "enters" => TriggerEvent.EntersBattlefield,
        "dies" => TriggerEvent.Dies,
        "attacks" => TriggerEvent.Attacks,
        "upkeep" => TriggerEvent.YourUpkeep,
        "endStep" => TriggerEvent.YourEndStep,
        "combatDamageToPlayer" => TriggerEvent.DealsCombatDamageToPlayer,
        _ => throw new FormatException($"Unknown trigger '{text}'."),
    };

    /// <summary>"{2}{R}, {T}, sacrifice".</summary>
    public static AbilityCost ParseCost(string text)
    {
        var mana = ManaCost.Zero;
        bool tap = false, sacrifice = false;
        foreach (var raw in text.Split(','))
        {
            var part = raw.Trim();
            if (part == "{T}") tap = true;
            else if (part.Equals("sacrifice", StringComparison.OrdinalIgnoreCase)) sacrifice = true;
            else if (part.Length > 0) mana = ManaCost.Parse(part);
        }
        return new AbilityCost(mana, tap, sacrifice);
    }

    private static IReadOnlyList<Effect> Effects(JsonElement e) =>
        e.TryGetProperty("effects", out var list) ? list.EnumerateArray().Select(ParseEffect).ToList() : throw new FormatException("Missing \"effects\".");

    private static Effect ParseEffect(JsonElement e)
    {
        string? Str(string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
        int Int(string name) => e.GetProperty(name).GetInt32();

        if (e.TryGetProperty("damage", out _)) return new DealDamage(Int("damage"), ParseSubject(Str("to") ?? "target"));
        if (e.TryGetProperty("draw", out _)) return new DrawCards(Int("draw"), ParseSubject(Str("who")));
        if (e.TryGetProperty("gainLife", out _)) return new GainLife(Int("gainLife"), ParseSubject(Str("who")));
        if (e.TryGetProperty("loseLife", out _)) return new LoseLife(Int("loseLife"), ParseSubject(Str("who")));
        if (e.TryGetProperty("mill", out _)) return new Mill(Int("mill"), ParseSubject(Str("who")));
        if (Str("destroy") is { } destroy) return new Destroy(ParseSubject(destroy));
        if (Str("exile") is { } exile) return new ExileIt(ParseSubject(exile));
        if (Str("bounce") is { } bounce) return new ReturnToHand(ParseSubject(bounce));
        if (Str("tap") is { } tap) return new TapIt(ParseSubject(tap));
        if (Str("untap") is { } untap) return new UntapIt(ParseSubject(untap));
        if (Str("counter") is { } counter) return new CounterSpell(ParseSubject(counter));
        if (Str("attach") is { } attach) return new AttachSelf(ParseSubject(attach));
        if (e.TryGetProperty("pump", out var pump))
        {
            var pt = pump.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            return new PumpUntilEndOfTurn(pt[0], pt[1], ParseSubject(Str("what") ?? "target"), ParseKeywords(e));
        }
        if (e.TryGetProperty("counters", out _))
        {
            var kind = Str("kind") == "-1/-1" ? CounterKind.MinusOneMinusOne : CounterKind.PlusOnePlusOne;
            return new AddCounters(Int("counters"), ParseSubject(Str("what") ?? "target"), kind);
        }
        if (e.TryGetProperty("tokens", out _)) return new CreateTokens(ParseToken(e.GetProperty("token")), Int("tokens"), ParseSubject(Str("for")));
        throw new FormatException($"Unknown effect {e.GetRawText()}.");
    }

    private static CardDefinition ParseToken(JsonElement t)
    {
        var (supertypes, types, subtypes) = TypeLine.Parse(t.GetProperty("types").GetString()!);
        return new CardDefinition
        {
            Name = t.GetProperty("name").GetString()!,
            Types = types,
            Supertypes = supertypes,
            Subtypes = subtypes,
            Power = t.TryGetProperty("power", out var p) ? p.GetInt32() : null,
            Toughness = t.TryGetProperty("toughness", out var th) ? th.GetInt32() : null,
            Keywords = t.TryGetProperty("keywords", out var k) ? k.EnumerateArray().Select(x => x.GetString()!).ToList() : Array.Empty<string>(),
            IsToken = true,
        };
    }
}
