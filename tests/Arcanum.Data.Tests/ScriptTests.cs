// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Data.Tests;

public class ScriptTests
{
    [Fact]
    public void ParsesSpellWithTargetAndDamage()
    {
        var script = CardScriptParser.Parse("""{ "spell": { "targets": ["any"], "effects": [{ "damage": 2, "to": "target" }] } }""");
        var spell = Assert.IsType<SpellAbility>(script.Spell);
        Assert.Equal(TargetKind.Any, Assert.Single(spell.Targets).Kind);
        var damage = Assert.IsType<DealDamage>(Assert.Single(spell.Effects));
        Assert.Equal(2, damage.Amount);
        Assert.Equal(SubjectKind.Target, damage.To.Kind);
    }

    [Fact]
    public void ParsesTriggeredAndActivatedAbilities()
    {
        var script = CardScriptParser.Parse("""
            {
              // comments and trailing commas are allowed
              "abilities": [
                { "trigger": "enters", "effects": [{ "draw": 1 }], "text": "When this enters, draw a card." },
                { "cost": "{1}{R}, {T}, sacrifice", "targets": ["creature:opponent"], "effects": [{ "damage": 2 }] },
              ]
            }
            """);
        var trigger = Assert.IsType<TriggeredAbility>(script.Abilities[0]);
        Assert.Equal(TriggerEvent.EntersBattlefield, trigger.Trigger);
        Assert.Equal(Subject.You, Assert.IsType<DrawCards>(trigger.Effects[0]).Who);

        var activated = Assert.IsType<ActivatedAbility>(script.Abilities[1]);
        Assert.Equal(ManaCost.Parse("{1}{R}"), activated.Cost.Mana);
        Assert.True(activated.Cost.Tap);
        Assert.True(activated.Cost.SacrificeSelf);
        Assert.Equal(new TargetSpec(TargetKind.Creature, ControllerFilter.Opponent), activated.Targets[0]);
    }

    [Fact]
    public void ParsesPumpCountersAndTokens()
    {
        var script = CardScriptParser.Parse("""
            { "spell": { "effects": [
                { "pump": [3, 3], "what": "target", "keywords": ["Trample"] },
                { "counters": 2, "what": "self" },
                { "tokens": 2, "token": { "name": "Soldier", "types": "Creature — Soldier", "power": 1, "toughness": 1 } }
            ] } }
            """);
        var effects = script.Spell!.Effects;
        var pump = Assert.IsType<PumpUntilEndOfTurn>(effects[0]);
        Assert.Equal(new[] { Keyword.Trample }, pump.Keywords);
        Assert.Equal(2, Assert.IsType<AddCounters>(effects[1]).Count);
        var tokens = Assert.IsType<CreateTokens>(effects[2]);
        Assert.Equal(CardType.Creature, tokens.Token.Types);
        Assert.True(tokens.Token.IsToken);
    }

    [Theory]
    [InlineData("""{ "spell": { "effects": [{ "explode": 1 }] } }""")]
    [InlineData("""{ "spell": { "targets": ["planet"], "effects": [{ "draw": 1 }] } }""")]
    [InlineData("""{ "abilities": [{ "effects": [{ "draw": 1 }] }] }""")]
    public void RejectsUnknownVocabulary(string json) => Assert.ThrowsAny<FormatException>(() => CardScriptParser.Parse(json));

    [Fact]
    public void ScriptMakesACardSupported()
    {
        var record = new CardRecord
        {
            OracleId = "o-x", Name = "Ember Sage", Layout = "normal", ManaCost = "{1}{R}", TypeLine = "Creature — Human",
            OracleText = "When this creature enters, it deals 1 damage to any target.", Power = "1", Toughness = "1",
        };
        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(record).Support);
        var script = CardScriptParser.Parse("""{ "abilities": [{ "trigger": "enters", "targets": ["any"], "effects": [{ "damage": 1 }] }] }""");
        var (definition, support) = CardFactory.Create(record, script);
        Assert.Equal(CardSupport.Full, support);
        Assert.IsType<TriggeredAbility>(Assert.Single(definition.Abilities));
    }

    [Theory]
    [InlineData("{T}: Add {G}.", new[] { ManaType.Green })]
    [InlineData("{T}: Add {R} or {G}.", new[] { ManaType.Red, ManaType.Green })]
    [InlineData("{T}: Add one mana of any color.", new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green })]
    public void ManaAbilitiesComeFromRulesText(string text, ManaType[] expected)
    {
        var record = new CardRecord
        {
            OracleId = "o-m", Name = "Grove Tender", Layout = "normal", ManaCost = "{G}", TypeLine = "Creature — Elf",
            OracleText = text, Power = "1", Toughness = "1",
        };
        var (definition, support) = CardFactory.Create(record);
        Assert.Equal(expected, definition.TapForMana);
        Assert.Equal(CardSupport.Full, support);
    }
}

public class StaticScriptTests
{
    [Fact]
    public void ParsesStaticAbilitiesAndAuras()
    {
        var script = CardScriptParser.Parse("""
            {
              "aura": "creature",
              "entersWithCounters": 1,
              "abilities": [
                { "static": { "affects": "enchanted", "pump": [2, 0], "keywords": ["Trample"] }, "text": "Enchanted creature gets +2/+0 and has trample." },
                { "static": { "affects": "creatures:you", "other": true, "subtype": "Goblin", "pump": [1, 1] } }
              ]
            }
            """);
        Assert.Equal(new TargetSpec(TargetKind.Creature), script.Aura);
        Assert.Equal(1, script.EntersWithCounters);
        var aura = Assert.IsType<StaticAbility>(script.Abilities[0]);
        Assert.Equal(AffectedScope.Enchanted, aura.Affects.Scope);
        Assert.Equal(new[] { Keyword.Trample }, aura.GrantedKeywords);
        var lord = Assert.IsType<StaticAbility>(script.Abilities[1]);
        Assert.True(lord.Affects.Other);
        Assert.Equal("Goblin", lord.Affects.Subtype);
    }

    private static CardRecord Record(string name, string type, string text, string? p = null, string? t = null) => new()
    {
        OracleId = "o-" + name, Name = name, Layout = "normal", ManaCost = "{1}", TypeLine = type, OracleText = text, Power = p, Toughness = t,
    };

    [Fact]
    public void EquipAndEnchantAreReadFromRulesText()
    {
        var (sword, _) = CardFactory.Create(Record("Test Blade", "Artifact — Equipment", "Equipped creature gets +1/+1.\nEquip {2}"));
        var equip = Assert.IsType<ActivatedAbility>(Assert.Single(sword.Abilities));
        Assert.True(equip.SorcerySpeed);
        Assert.Equal(ManaCost.Parse("{2}"), equip.Cost.Mana);
        Assert.IsType<AttachSelf>(Assert.Single(equip.Effects));

        var (aura, _) = CardFactory.Create(Record("Test Aura", "Enchantment — Aura", "Enchant creature you control\nEnchanted creature gets +1/+1."));
        Assert.Equal(new TargetSpec(TargetKind.Creature, ControllerFilter.You), aura.EnchantTarget);
    }

    [Fact]
    public void EntersTappedLandsAreFullySupported()
    {
        var (land, support) = CardFactory.Create(Record("Test Vale", "Land", "This land enters tapped.\n{T}: Add {G} or {W}."));
        Assert.True(land.EntersTapped);
        Assert.Equal(new[] { ManaType.Green, ManaType.White }, land.TapForMana);
        Assert.Equal(CardSupport.Full, support);
    }
}
