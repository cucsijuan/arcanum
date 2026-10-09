// SPDX-License-Identifier: AGPL-3.0-or-later
// The Magic Origins card scripts of the classic content module about combat, costs, filters and targeting: the same rules
// text and scripts, so these tests check the scripts as players get them.
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;

namespace Arcanum.Engine.Tests;

public static class OriginsCards
{
    private static CardDefinition Make(CardRecord record, string script)
    {
        var (definition, support) = CardFactory.Create(record, CardScriptParser.Parse(script));
        if (support != CardSupport.Full) throw new InvalidOperationException($"{record.Name} isn't supported.");
        return definition;
    }

    private static readonly Dictionary<string, CardDefinition> Cards = new()
    {
        ["Archangel of Tithes"] = Make(new CardRecord
        {
            OracleId = "ff5caed4-0276-476d-8fae-edae2536df7f", Name = "Archangel of Tithes", Layout = "normal", ManaCost = "{1}{W}{W}{W}", TypeLine = "Creature — Angel",
            OracleText = "Flying\nAs long as this creature is untapped, creatures can't attack you or planeswalkers you control unless their controller pays {1} for each of those creatures.\nAs long as this creature is attacking, creatures can't block unless their controller pays {1} for each of those creatures.", Power = "3", Toughness = "5", Loyalty = null,
            Keywords = new string[] { "Flying" },
        }, "{\n  \"name\": \"Archangel of Tithes\",\n  \"attackTax\": \"{1}\",\n  \"attackTaxIf\": \"untapped\",\n  \"blockTax\": \"{1}\",\n  \"blockTaxIf\": \"attacking\"\n}"),
        ["Scrapskin Drake"] = Make(new CardRecord
        {
            OracleId = "910e8269-6001-479a-ab2e-5a935569c33c", Name = "Scrapskin Drake", Layout = "normal", ManaCost = "{2}{U}", TypeLine = "Creature — Zombie Drake",
            OracleText = "Flying (This creature can't be blocked except by creatures with flying or reach.)\nThis creature can block only creatures with flying.", Power = "2", Toughness = "3", Loyalty = null,
            Keywords = new string[] { "Flying" },
        }, "{\n  \"name\": \"Scrapskin Drake\",\n  \"abilities\": [\n    {\n      \"static\": {\n        \"affects\": \"self\",\n        \"keywords\": [\n          \"Can block only creatures with flying\"\n        ]\n      },\n      \"text\": \"This creature can block only creatures with flying.\"\n    }\n  ]\n}"),
        ["Stratus Walk"] = Make(new CardRecord
        {
            OracleId = "eda37caa-d676-460a-804b-3ba9d8a8d044", Name = "Stratus Walk", Layout = "normal", ManaCost = "{1}{U}", TypeLine = "Enchantment — Aura",
            OracleText = "Enchant creature\nWhen this Aura enters, draw a card.\nEnchanted creature has flying. (It can't be blocked except by creatures with flying or reach.)\nEnchanted creature can block only creatures with flying.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] { "Enchant" },
        }, "{\n  \"name\": \"Stratus Walk\",\n  \"abilities\": [\n    {\n      \"trigger\": \"enters\",\n      \"effects\": [\n        {\n          \"draw\": 1\n        }\n      ],\n      \"text\": \"When this Aura enters, draw a card.\"\n    },\n    {\n      \"static\": {\n        \"affects\": \"enchanted\",\n        \"keywords\": [\n          \"Flying\"\n        ]\n      },\n      \"text\": \"Enchanted creature has flying.\"\n    },\n    {\n      \"static\": {\n        \"affects\": \"enchanted\",\n        \"keywords\": [\n          \"Can block only creatures with flying\"\n        ]\n      },\n      \"text\": \"Enchanted creature can block only creatures with flying.\"\n    }\n  ]\n}"),
        ["Orchard Spirit"] = Make(new CardRecord
        {
            OracleId = "b6eb56a7-4dfe-4d51-a1df-55f892c1b014", Name = "Orchard Spirit", Layout = "normal", ManaCost = "{2}{G}", TypeLine = "Creature — Spirit",
            OracleText = "This creature can't be blocked except by creatures with flying or reach.", Power = "2", Toughness = "2", Loyalty = null,
            Keywords = new string[] {  },
        }, "{\n  \"name\": \"Orchard Spirit\",\n  \"cantBeBlockedBy\": {\n    \"without\": [\n      \"Flying\",\n      \"Reach\"\n    ]\n  }\n}"),
        ["Ghirapur Aether Grid"] = Make(new CardRecord
        {
            OracleId = "05d75b10-4f20-4083-af27-f85c6e207fd0", Name = "Ghirapur Aether Grid", Layout = "normal", ManaCost = "{2}{R}", TypeLine = "Enchantment",
            OracleText = "Tap two untapped artifacts you control: This enchantment deals 1 damage to any target.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  },
        }, "{\n  \"name\": \"Ghirapur Aether Grid\",\n  \"abilities\": [\n    {\n      \"cost\": \"tapPermanents:2:artifact\",\n      \"targets\": [\n        \"any\"\n      ],\n      \"effects\": [\n        {\n          \"damage\": 1,\n          \"to\": \"target\"\n        }\n      ],\n      \"text\": \"Tap two untapped artifacts you control: This enchantment deals 1 damage to any target.\"\n    }\n  ]\n}"),
        ["Whirler Rogue"] = Make(new CardRecord
        {
            OracleId = "7060f2c8-fca0-4b7a-bddf-37682f434596", Name = "Whirler Rogue", Layout = "normal", ManaCost = "{2}{U}{U}", TypeLine = "Creature — Human Rogue Artificer",
            OracleText = "When this creature enters, create two 1/1 colorless Thopter artifact creature tokens with flying.\nTap two untapped artifacts you control: Target creature can't be blocked this turn.", Power = "2", Toughness = "2", Loyalty = null,
            Keywords = new string[] {  },
        }, "{\n  \"name\": \"Whirler Rogue\",\n  \"abilities\": [\n    {\n      \"trigger\": \"enters\",\n      \"effects\": [\n        {\n          \"tokens\": 2,\n          \"token\": {\n            \"name\": \"Thopter\",\n            \"types\": \"Artifact Creature — Thopter\",\n            \"power\": 1,\n            \"toughness\": 1,\n            \"keywords\": [\n              \"Flying\"\n            ],\n            \"colors\": []\n          }\n        }\n      ],\n      \"text\": \"When this creature enters, create two 1/1 colorless Thopter artifact creature tokens with flying.\"\n    },\n    {\n      \"cost\": \"tapPermanents:2:artifact\",\n      \"targets\": [\n        \"creature\"\n      ],\n      \"effects\": [\n        {\n          \"pump\": [\n            0,\n            0\n          ],\n          \"what\": \"target\",\n          \"keywords\": [\n            \"Can't be blocked\"\n          ]\n        }\n      ],\n      \"text\": \"Tap two untapped artifacts you control: Target creature can't be blocked this turn.\"\n    }\n  ]\n}"),
        ["Disciple of the Ring"] = Make(new CardRecord
        {
            OracleId = "f06209e6-475e-47e0-b84c-3ec1215633a9", Name = "Disciple of the Ring", Layout = "normal", ManaCost = "{3}{U}{U}", TypeLine = "Creature — Human Wizard",
            OracleText = "{1}, Exile an instant or sorcery card from your graveyard: Choose one —\n• Counter target noncreature spell unless its controller pays {2}.\n• This creature gets +1/+1 until end of turn.\n• Tap target creature.\n• Untap target creature.", Power = "3", Toughness = "4", Loyalty = null,
            Keywords = new string[] {  },
        }, "{\n  \"name\": \"Disciple of the Ring\",\n  \"abilities\": [\n    {\n      \"cost\": \"{1}, exileGraveyardCards:1:instant|sorcery\",\n      \"chooseCount\": 1,\n      \"modes\": [\n        {\n          \"text\": \"Counter target noncreature spell unless its controller pays {2}.\",\n          \"targets\": [\n            {\n              \"kind\": \"spell\",\n              \"filter\": {\n                \"not\": [\n                  \"creature\"\n                ]\n              },\n              \"text\": \"target noncreature spell\"\n            }\n          ],\n          \"effects\": [\n            {\n              \"counter\": \"target\",\n              \"unlessPays\": \"{2}\"\n            }\n          ]\n        },\n        {\n          \"text\": \"This creature gets +1/+1 until end of turn.\",\n          \"effects\": [\n            {\n              \"pump\": [\n                1,\n                1\n              ],\n              \"what\": \"self\"\n            }\n          ]\n        },\n        {\n          \"text\": \"Tap target creature.\",\n          \"targets\": [\n            \"creature\"\n          ],\n          \"effects\": [\n            {\n              \"tap\": \"target\"\n            }\n          ]\n        },\n        {\n          \"text\": \"Untap target creature.\",\n          \"targets\": [\n            \"creature\"\n          ],\n          \"effects\": [\n            {\n              \"untap\": \"target\"\n            }\n          ]\n        }\n      ],\n      \"text\": \"{1}, Exile an instant or sorcery card from your graveyard: Choose one —\\n• Counter target noncreature spell unless its controller pays {2}.\\n• This creature gets +1/+1 until end of turn.\\n• Tap target creature.\\n• Untap target creature.\"\n    }\n  ]\n}"),
        ["Skaab Goliath"] = Make(new CardRecord
        {
            OracleId = "498707db-258f-423a-9ff6-9e294fce84d6", Name = "Skaab Goliath", Layout = "normal", ManaCost = "{5}{U}", TypeLine = "Creature — Zombie Giant",
            OracleText = "As an additional cost to cast this spell, exile two creature cards from your graveyard.\nTrample", Power = "6", Toughness = "9", Loyalty = null,
            Keywords = new string[] { "Trample" },
        }, "{\n  \"name\": \"Skaab Goliath\",\n  \"additionalCost\": {\n    \"exileGraveyard\": 2,\n    \"exileGraveyardFilter\": {\n      \"types\": [\n        \"creature\"\n      ]\n    }\n  }\n}"),
        ["Gilt-Leaf Winnower"] = Make(new CardRecord
        {
            OracleId = "53b99846-3f80-4e9d-ad3c-52a4906b8823", Name = "Gilt-Leaf Winnower", Layout = "normal", ManaCost = "{3}{B}{B}", TypeLine = "Creature — Elf Warrior",
            OracleText = "Menace (This creature can't be blocked except by two or more creatures.)\nWhen this creature enters, you may destroy target non-Elf creature whose power and toughness aren't equal.", Power = "4", Toughness = "3", Loyalty = null,
            Keywords = new string[] { "Menace" },
        }, "{\n  \"name\": \"Gilt-Leaf Winnower\",\n  \"abilities\": [\n    {\n      \"trigger\": \"enters\",\n      \"targets\": [\n        {\n          \"kind\": \"creature\",\n          \"filter\": {\n            \"notSubtype\": \"Elf\",\n            \"powerNotEqualToughness\": true\n          },\n          \"text\": \"target non-Elf creature whose power and toughness aren't equal\"\n        }\n      ],\n      \"effects\": [\n        {\n          \"may\": \"Destroy the target creature?\",\n          \"effects\": [\n            {\n              \"destroy\": \"target\"\n            }\n          ]\n        }\n      ],\n      \"text\": \"When this creature enters, you may destroy target non-Elf creature whose power and toughness aren't equal.\"\n    }\n  ]\n}"),
        ["Kothophed, Soul Hoarder"] = Make(new CardRecord
        {
            OracleId = "85e1791f-f9a0-4e82-baf6-33cff2dcf60b", Name = "Kothophed, Soul Hoarder", Layout = "normal", ManaCost = "{4}{B}{B}", TypeLine = "Legendary Creature — Demon",
            OracleText = "Flying\nWhenever a permanent owned by another player is put into a graveyard from the battlefield, you draw a card and you lose 1 life.", Power = "6", Toughness = "6", Loyalty = null,
            Keywords = new string[] { "Flying" },
        }, "{\n  \"name\": \"Kothophed, Soul Hoarder\",\n  \"abilities\": [\n    {\n      \"trigger\": \"permanentDies\",\n      \"filter\": {\n        \"controller\": \"any\",\n        \"notOwnedByYou\": true\n      },\n      \"effects\": [\n        {\n          \"draw\": 1\n        },\n        {\n          \"loseLife\": 1\n        }\n      ],\n      \"text\": \"Whenever a permanent owned by another player is put into a graveyard from the battlefield, you draw a card and you lose 1 life.\"\n    }\n  ]\n}"),
        ["Sigil of Valor"] = Make(new CardRecord
        {
            OracleId = "b9447f25-f285-4b62-8976-4a49363dfdd7", Name = "Sigil of Valor", Layout = "normal", ManaCost = "{2}", TypeLine = "Artifact — Equipment",
            OracleText = "Whenever equipped creature attacks alone, it gets +1/+1 until end of turn for each other creature you control.\nEquip {1} ({1}: Attach to target creature you control. Equip only as a sorcery.)", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] { "Equip" },
        }, "{\n  \"name\": \"Sigil of Valor\",\n  \"abilities\": [\n    {\n      \"trigger\": \"creatureAttacks\",\n      \"filter\": {\n        \"types\": [\n          \"creature\"\n        ],\n        \"controller\": \"any\",\n        \"attachedToSource\": true\n      },\n      \"when\": {\n        \"attackersExactly\": 1\n      },\n      \"effects\": [\n        {\n          \"pump\": [\n            {\n              \"count\": {\n                \"types\": [\n                  \"creature\"\n                ],\n                \"notTriggered\": true\n              }\n            },\n            {\n              \"count\": {\n                \"types\": [\n                  \"creature\"\n                ],\n                \"notTriggered\": true\n              }\n            }\n          ],\n          \"what\": \"triggered\"\n        }\n      ],\n      \"text\": \"Whenever equipped creature attacks alone, it gets +1/+1 until end of turn for each other creature you control.\"\n    }\n  ]\n}"),
        ["Displacement Wave"] = Make(new CardRecord
        {
            OracleId = "2b3fc609-7b19-441a-ab7e-58ccd70d955b", Name = "Displacement Wave", Layout = "normal", ManaCost = "{X}{U}{U}", TypeLine = "Sorcery",
            OracleText = "Return all nonland permanents with mana value X or less to their owners' hands.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  },
        }, "{\n  \"name\": \"Displacement Wave\",\n  \"spell\": {\n    \"effects\": [\n      {\n        \"bounceAll\": {\n          \"not\": [\n            \"land\"\n          ],\n          \"controller\": \"any\",\n          \"maxManaValueX\": true\n        }\n      }\n    ],\n    \"text\": \"Return all nonland permanents with mana value X or less to their owners' hands.\"\n  }\n}"),
        ["Gaea's Revenge"] = Make(new CardRecord
        {
            OracleId = "26f6cc1e-d85e-45a3-a1ed-7f09d7cfc7df", Name = "Gaea's Revenge", Layout = "normal", ManaCost = "{5}{G}{G}", TypeLine = "Creature — Elemental",
            OracleText = "This spell can't be countered.\nHaste\nThis creature can't be the target of nongreen spells or abilities from nongreen sources.", Power = "8", Toughness = "5", Loyalty = null,
            Keywords = new string[] { "Haste" },
        }, "{\n  \"name\": \"Gaea's Revenge\",\n  \"cantBeTargetedBy\": {\n    \"notColors\": [\n      \"G\"\n    ]\n  }\n}"),
        ["Psychic Rebuttal"] = Make(new CardRecord
        {
            OracleId = "08dd393f-12d8-4801-882c-6d76009b4f12", Name = "Psychic Rebuttal", Layout = "normal", ManaCost = "{1}{U}", TypeLine = "Instant",
            OracleText = "Counter target instant or sorcery spell that targets you.\nSpell mastery — If there are two or more instant and/or sorcery cards in your graveyard, you may copy the spell countered this way. You may choose new targets for the copy.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] { "Spell mastery" },
        }, "{\n  \"name\": \"Psychic Rebuttal\",\n  \"spell\": {\n    \"targets\": [\n      {\n        \"kind\": \"spell\",\n        \"filter\": {\n          \"types\": [\n            \"instant\",\n            \"sorcery\"\n          ],\n          \"targetsYou\": true\n        },\n        \"text\": \"target instant or sorcery spell that targets you\"\n      }\n    ],\n    \"effects\": [\n      {\n        \"counter\": \"target\"\n      },\n      {\n        \"if\": {\n          \"graveyard\": 2,\n          \"filter\": {\n            \"types\": [\n              \"instant\",\n              \"sorcery\"\n            ]\n          }\n        },\n        \"then\": [\n          {\n            \"may\": \"Copy the spell countered this way?\",\n            \"effects\": [\n              {\n                \"copySpell\": \"target\",\n                \"counteredThisWay\": true\n              }\n            ]\n          }\n        ]\n      }\n    ],\n    \"text\": \"Counter target instant or sorcery spell that targets you.\\nSpell mastery — If there are two or more instant and/or sorcery cards in your graveyard, you may copy the spell countered this way. You may choose new targets for the copy.\"\n  }\n}"),
        ["Exquisite Firecraft"] = Make(new CardRecord
        {
            OracleId = "e83c617f-1e07-40db-9b12-ae01122a3e24", Name = "Exquisite Firecraft", Layout = "normal", ManaCost = "{1}{R}{R}", TypeLine = "Sorcery",
            OracleText = "Exquisite Firecraft deals 4 damage to any target.\nSpell mastery — If there are two or more instant and/or sorcery cards in your graveyard, this spell can't be countered.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] { "Spell mastery" },
        }, "{\n  \"name\": \"Exquisite Firecraft\",\n  \"uncounterableIf\": {\n    \"graveyard\": 2,\n    \"filter\": {\n      \"types\": [\n        \"instant\",\n        \"sorcery\"\n      ]\n    }\n  },\n  \"spell\": {\n    \"targets\": [\n      \"any\"\n    ],\n    \"effects\": [\n      {\n        \"damage\": 4,\n        \"to\": \"target\"\n      }\n    ],\n    \"text\": \"Exquisite Firecraft deals 4 damage to any target.\\nSpell mastery — If there are two or more instant and/or sorcery cards in your graveyard, this spell can't be countered.\"\n  }\n}"),
        ["Mizzium Meddler"] = Make(new CardRecord
        {
            OracleId = "48a909b6-e6ee-4148-8b50-b35f11bc065f", Name = "Mizzium Meddler", Layout = "normal", ManaCost = "{2}{U}", TypeLine = "Creature — Vedalken Wizard",
            OracleText = "Flash (You may cast this spell any time you could cast an instant.)\nWhen this creature enters, you may change a target of target spell or ability to this creature.", Power = "1", Toughness = "4", Loyalty = null,
            Keywords = new string[] { "Flash" },
        }, "{\n  \"name\": \"Mizzium Meddler\",\n  \"abilities\": [\n    {\n      \"trigger\": \"enters\",\n      \"targets\": [\n        {\n          \"kind\": \"spellOrAbility\",\n          \"text\": \"target spell or ability\"\n        }\n      ],\n      \"effects\": [\n        {\n          \"changeTarget\": \"target\",\n          \"to\": \"self\"\n        }\n      ],\n      \"text\": \"When this creature enters, you may change a target of target spell or ability to this creature.\"\n    }\n  ]\n}"),
    };

    public static CardDefinition Get(string name) => Cards[name];
}
