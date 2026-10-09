// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Scripts;

/// <summary>Vocabulary for the effects in <see cref="OriginsEffect"/> (see "Sweeps, casting while resolving and naming" in docs/card-scripts.md).</summary>
public static partial class CardScriptParser
{
    /// <summary>
    /// { "shuffleIntoLibraries": "everyone", "hand": true, "graveyard": true, "permanents": true, "drawThatMany": true },
    /// { "eachPutsFromHand": filter, "who": "everyone" },
    /// { "revealTopCastFree": 7, "of": "target", "filter": f, "casts": 1, "moreCastsIf": condition, "moreCasts": 2, "rest": "graveyard" },
    /// { "nameAndExile": "target", "nameFilter": f }. Null when the effect isn't one of these.
    /// </summary>
    private static Effect? ParseOriginsEffect(JsonElement e)
    {
        bool Flag(string name) => e.TryGetProperty(name, out var v) && v.GetBoolean();
        Subject Who(string name, string fallback) => e.TryGetProperty(name, out var v) ? ParseSubject(v) : ParseSubject(fallback);

        if (e.TryGetProperty("shuffleIntoLibraries", out var sil))
            return new ShuffleIntoLibraries(ParseSubject(sil), Flag("hand"), Flag("graveyard"), Flag("permanents")) { DrawThatMany = Flag("drawThatMany") };
        if (e.TryGetProperty("eachPutsFromHand", out var eph))
            return new EachPutsFromHand(Who("who", "everyone"), ParseFilter(eph, ControllerFilter.Any));
        if (e.TryGetProperty("revealTopCastFree", out var rtc))
            return new RevealTopCastFree(Who("of", "you"), rtc.GetInt32(),
                e.TryGetProperty("filter", out var rtf) ? ParseFilter(rtf, ControllerFilter.Any) : ObjectFilter.Anything,
                e.TryGetProperty("casts", out var casts) ? casts.GetInt32() : 1)
            {
                MoreCastsIf = e.TryGetProperty("moreCastsIf", out var mci) ? ParseCondition(mci) : null,
                MoreCasts = e.TryGetProperty("moreCasts", out var mc) ? mc.GetInt32() : 0,
                RestToGraveyard = e.TryGetProperty("rest", out var rest) && rest.GetString() == "graveyard",
            };
        if (e.TryGetProperty("nameAndExile", out var nae))
            return new NameThenExileFromAllZones(ParseSubject(nae),
                e.TryGetProperty("nameFilter", out var nf) ? ParseFilter(nf, ControllerFilter.Any) : ObjectFilter.Anything);
        return null;
    }
}
