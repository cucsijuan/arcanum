// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>
/// What every card search box shares: reading what was typed in the card search language (<see cref="CardSearch"/>),
/// a tooltip that explains it, and the terms that were ignored (shown in the tooltip, the text turning amber).
/// </summary>
public static class CardSearchBox
{
    public const string Placeholder = "Search…";

    public const string Help =
        "Loose words search card names. Keywords search the rest:\n" +
        "  t:goblin  type · o:\"draw a card\"  rules text (~ is the card's name) · kw:flying  keyword\n" +
        "  c:rg  colors (c>=uw, c=2, c:m multicolor, c:c colorless) · id:esper  color identity\n" +
        "  m:2WW  mana cost · mv<=3  mana value · pow>=4 tou<pow loy=3 · produces:g · devotion:ggg\n" +
        "  r>=rare  rarity · s:fdn  set · cn<50 · st:core · in:rare · date>=2020 · year<2010\n" +
        "  f:commander  legal · banned: restricted: · is:commander is:permanent is:vanilla is:foil is:supported...\n" +
        "  !\"exact name\" · -t:creature  not · t:elf or t:goblin · (a or b) · o:/regex/ · order:mv direction:desc";

    /// <summary>Reads the search in the box and shows its warnings on it.</summary>
    public static CardFilter Read(LineEdit box, CardDatabase cards)
    {
        var filter = cards.Filter(box.Text);
        Show(box, filter);
        return filter;
    }

    /// <summary>Shows a search's warnings on its box (in the tooltip, the text turning amber), or just the help.</summary>
    public static void Show(LineEdit box, CardFilter filter)
    {
        box.TooltipText = filter.Warnings.Count == 0 ? Help : string.Join("\n", filter.Warnings) + "\n\n" + Help;
        if (filter.Warnings.Count > 0) box.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.3f));
        else box.RemoveThemeColorOverride("font_color");
    }
}
