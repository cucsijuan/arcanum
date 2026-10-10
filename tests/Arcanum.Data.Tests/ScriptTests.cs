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
    public void ParsesFiltersConditionsAndNewEffects()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [
                { "trigger": "creatureEnters", "filter": { "types": ["creature"], "subtype": "Cat", "other": true },
                  "effects": [{ "scry": 1 }] },
                { "trigger": "endStep", "if": "raid", "effects": [{ "tokens": 1, "token": "Treasure" }] },
                { "trigger": "castSpell", "filter": { "types": ["instant", "sorcery"] },
                  "effects": [{ "may": "Surveil 1?", "effects": [{ "surveil": 1 }] }] },
                { "cost": "{T}", "targets": ["creature:you", "creature:opponent"],
                  "effects": [{ "fight": "target", "with": "target2" },
                              { "if": { "control": { "types": ["creature"], "minPower": 4 } }, "then": [{ "draw": 1 }], "else": [{ "discard": 1 }] }] },
            ] }
            """);
        var enters = Assert.IsType<TriggeredAbility>(script.Abilities[0]);
        Assert.Equal(TriggerEvent.CreatureEnters, enters.Trigger);
        Assert.Equal(new ObjectFilter(CardType.Creature, Subtype: "Cat", Other: true), enters.Filter);
        Assert.Equal(1, Assert.IsType<Scry>(enters.Effects[0]).Count);

        var raid = Assert.IsType<TriggeredAbility>(script.Abilities[1]);
        Assert.IsType<AttackedThisTurn>(raid.Condition);
        Assert.Same(PredefinedTokens.Treasure, Assert.IsType<CreateTokens>(raid.Effects[0]).Token);

        var cast = Assert.IsType<TriggeredAbility>(script.Abilities[2]);
        Assert.Equal(CardType.Instant | CardType.Sorcery, cast.Filter!.Types);
        var may = Assert.IsType<MayDo>(cast.Effects[0]);
        Assert.IsType<Surveil>(Assert.Single(may.Effects));

        var fight = Assert.IsType<ActivatedAbility>(script.Abilities[3]);
        Assert.Equal(new Fight(Subject.TargetAt(0), Subject.TargetAt(1)), fight.Effects[0]);
        var branch = Assert.IsType<IfThen>(fight.Effects[1]);
        Assert.Equal(4, Assert.IsType<YouControl>(branch.Condition).Filter.MinPower);
        Assert.IsType<Discard>(Assert.Single(branch.Else!));
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

public class TokenImageTests
{
    private const string Line = """
        {"oracle_id":"o-t","name":"Muster Captain","layout":"normal","mana_cost":"{5}","type_line":"Creature — Human","oracle_text":"{5}: Create a 2/2 red Soldier creature token.","power":"1","toughness":"1","games":["paper"],"all_parts":[{"component":"combo_piece","id":"self","name":"Muster Captain","type_line":"Creature — Human"},{"component":"token","id":"tok-soldier-red","name":"Soldier","type_line":"Token Creature — Soldier"},{"component":"token","id":"tok-goblin","name":"Goblin","type_line":"Token Creature — Goblin"}]}
        """;

    private static CardRecord Record()
    {
        var records = OracleJsonl.Import(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Line))).ToList();
        return Assert.Single(records);
    }

    [Fact]
    public void RelatedTokensAreImportedAndSurviveTheCompactStore()
    {
        var record = Record();
        Assert.Equal(2, record.RelatedTokens.Count);
        var ms = new MemoryStream();
        OracleJsonl.WriteCompact(new[] { record }, ms);
        ms.Position = 0;
        var back = Assert.Single(OracleJsonl.ReadCompact(ms));
        Assert.Equal(record.RelatedTokens, back.RelatedTokens);
    }

    [Fact]
    public void CreatedTokensGetTheExactImageOfTheMatchingRelatedToken()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [{ "cost": "{5}", "effects": [{ "tokens": 1, "token": { "name": "Soldier", "types": "Creature — Soldier", "power": 2, "toughness": 2, "colors": ["R"] } }] }] }
            """);
        var (definition, _) = CardFactory.Create(Record(), script);
        var create = Assert.IsType<CreateTokens>(Assert.Single(definition.Abilities).Effects[0]);
        Assert.Equal("tok-soldier-red", create.Token.ImageKey);
        Assert.Equal(new[] { "R" }, create.Token.Colors);
    }

    [Fact]
    public void TokensWithoutARelatedPrintingHaveNoImageKey()
    {
        var script = CardScriptParser.Parse("""
            { "abilities": [{ "cost": "{5}", "effects": [{ "tokens": 1, "token": { "name": "Bird", "types": "Creature — Bird", "power": 1, "toughness": 1 } }] }] }
            """);
        var (definition, _) = CardFactory.Create(Record(), script);
        var create = Assert.IsType<CreateTokens>(Assert.Single(definition.Abilities).Effects[0]);
        Assert.Null(create.Token.ImageKey); // the client shows a text frame rather than guessing by name
    }
    [Fact]
    public void ParsesTheMagic2010CombatVocabulary()
    {
        var keywords = CardScriptParser.Parse("""
            { "abilities": [
                { "static": { "affects": "self", "keywords": ["Can't attack alone", "Can't block alone", "Can block any number of creatures", "All creatures able to block it do so"] }, "text": "…" },
            ] }
            """);
        Assert.Equal(new[] { Keyword.CantAttackAlone, Keyword.CantBlockAlone, Keyword.CanBlockAnyNumber, Keyword.Lure },
            Assert.IsType<StaticAbility>(Assert.Single(keywords.Abilities)).GrantedKeywords);

        var triggers = CardScriptParser.Parse("""
            { "abilities": [
                { "trigger": "blocksOrBlockedBy", "effects": [{ "damage": 3, "to": "triggered" }], "text": "…" },
                { "trigger": "blocksCreature", "effects": [{ "skipNextUntap": "triggered" }], "text": "…" },
                { "cost": "{T}", "targets": ["creature:opponent"], "effects": [{ "attacksYouThisTurn": "target" }], "text": "…" },
                { "cost": "{T}", "targets": ["creature"], "effects": [{ "tapAllToDamage": { "types": ["creature"], "subtype": "Wolf" }, "to": "target" }], "text": "…" },
                { "cost": "{T}", "targets": [{ "kind": "creature", "controller": "you", "filter": { "toughnessLessThanPower": true } }],
                  "effects": [{ "atNextEndStepAbout": "target", "effects": [{ "destroy": "triggered" }] }], "text": "…" },
            ] }
            """);
        Assert.Equal(TriggerEvent.BlocksOrBecomesBlockedByCreature, Assert.IsType<TriggeredAbility>(triggers.Abilities[0]).Trigger);
        var frost = Assert.IsType<TriggeredAbility>(triggers.Abilities[1]);
        Assert.Equal(TriggerEvent.BlocksCreature, frost.Trigger);
        Assert.Equal(new SkipNextUntap(Subject.Triggered), frost.Effects[0]);
        Assert.Equal(new AttacksYouThisTurn(Subject.TargetAt(0)), triggers.Abilities[2].Effects[0]);
        var pack = Assert.IsType<TapAllToDamage>(triggers.Abilities[3].Effects[0]);
        Assert.Equal("Wolf", pack.Filter.Subtype);
        Assert.Equal(Subject.TargetAt(0), pack.Target);
        Assert.True(triggers.Abilities[4].Targets[0].Filter!.ToughnessLessThanSourcePower);

        var sleep = CardScriptParser.Parse("""
            { "spell": { "targets": ["player"], "effects": [
                { "tap": { "each": { "types": ["creature"] }, "controlledBy": "target" } },
                { "skipNextUntap": { "each": { "types": ["creature"] }, "controlledBy": "target" }, "player": "target" } ] } }
            """);
        var skip = Assert.IsType<SkipNextUntap>(sleep.Spell!.Effects[1]);
        Assert.True(skip.What.ControlledByTarget);
        Assert.Equal(Subject.TargetAt(0), skip.Player);

        var serpent = CardScriptParser.Parse("""
            { "cantAttackUnlessDefenderControls": { "types": ["land"], "subtype": "Island" }, "protectionFromSubtypes": ["Demon", "Dragon"],
              "aura": { "kind": "creature", "filter": { "tapped": true } } }
            """);
        var definition = serpent.ApplyTo(new CardDefinition { Name = "Test", Types = CardType.Creature });
        Assert.Equal("Island", definition.CantAttackUnlessDefenderControls!.Subtype);
        Assert.Equal(new[] { "Demon", "Dragon" }, definition.ProtectionFromSubtypes);
        Assert.True(serpent.Aura!.Filter!.Tapped);
    }

    [Fact]
    public void RenownNeedsTheScriptThatImplementsIt()
    {
        var record = new CardRecord
        {
            OracleId = "o-r", Name = "Topan Freeblade", Layout = "normal", ManaCost = "{1}{W}", TypeLine = "Creature — Human Soldier",
            OracleText = "Vigilance\nRenown 1 (When this creature deals combat damage to a player, if it isn't renowned, put a +1/+1 counter on it and it becomes renowned.)",
            Power = "2", Toughness = "2", Keywords = new[] { "Vigilance", "Renown" },
        };
        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(record).Support);
        var script = CardScriptParser.Parse("""
            { "abilities": [{ "trigger": "combatDamageToPlayer", "if": { "not": "renowned" },
                              "effects": [{ "counters": 1, "what": "self" }, { "becomeRenowned": "self" }] }] }
            """);
        Assert.Equal(CardSupport.Full, CardFactory.Create(record, script).Support);
        // A script that leaves out the renown trigger doesn't make it supported.
        var noRenown = CardScriptParser.Parse("""{ "abilities": [{ "cost": "{1}", "effects": [{ "pump": [1, 1], "what": "self" }] }] }""");
        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(record, noRenown).Support);
    }

    [Fact]
    public void ACardWhoseScriptLeavesOutItsOwnProtectionIsNotSupported()
    {
        CardRecord Record(string text) => new()
        {
            OracleId = "o-p", Name = "Goblin Driver", Layout = "normal", ManaCost = "{1}{R}", TypeLine = "Creature — Goblin Warrior",
            OracleText = text, Power = "1", Toughness = "2", Keywords = new[] { "Protection" },
        };
        const string attack = """{ "abilities": [{ "trigger": "attacks", "effects": [{ "pump": [2, 0], "what": "self" }] }] }""";
        const string withProtection = """
            { "abilities": [{ "static": { "affects": "self", "keywords": ["Protection from blue"] } },
                            { "trigger": "attacks", "effects": [{ "pump": [2, 0], "what": "self" }] }] }
            """;
        var own = Record("Protection from blue\nWhenever this creature attacks, it gets +2/+0 until end of turn.");
        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(own, CardScriptParser.Parse(attack)).Support);
        Assert.Equal(CardSupport.Full, CardFactory.Create(own, CardScriptParser.Parse(withProtection)).Support);

        // Giving protection to something else isn't the card's own protection.
        var granting = Record("{T}: Another target creature you control gains protection from the card type of your choice until end of turn.");
        var grant = CardScriptParser.Parse("""
            { "abilities": [{ "cost": "{T}", "targets": [{ "kind": "creature", "controller": "you", "filter": { "other": true } }],
                              "effects": [{ "protectionFromChosenType": "target" }] }] }
            """);
        Assert.Equal(CardSupport.Full, CardFactory.Create(granting, grant).Support);
    }
}
