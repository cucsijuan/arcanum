// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Client;
using Arcanum.UI.Board;
using Godot;

namespace Arcanum.UI.Menu;

/// <summary>Sandbox, card data maintenance and about/license information.</summary>
public partial class Extras : Control
{
    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MenuKit.AddBackdrop(this);
        MenuKit.AddHeader(this, "Extras");

        var root = new VBoxContainer { AnchorRight = 1, AnchorBottom = 1, OffsetLeft = 140, OffsetTop = 110, OffsetRight = -140, OffsetBottom = -40 };
        root.AddThemeConstantOverride("separation", 20);
        AddChild(root);

        var data = new VBoxContainer();
        data.AddThemeConstantOverride("separation", 10);
        data.AddChild(MenuKit.SectionTitle("Card data"));
        var module = App.Instance.Module;
        data.AddChild(MenuKit.Hint(module is null
            ? "No content module installed. Arcanum ships no card content; install a content module to play with real cards."
            : $"Content module: {module.Manifest.Name} {module.Manifest.Version} ({module.Manifest.License}). {App.Instance.ContentStatus}."));
        var refresh = BoardStyle.MakeButton("Download card data again", 16);
        refresh.CustomMinimumSize = new Vector2(300, 44);
        refresh.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        refresh.Disabled = module is null;
        refresh.Pressed += () =>
        {
            var dir = ProjectSettings.GlobalizePath($"user://card_data/{module!.Manifest.Id}");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            MenuKit.Toast(this, "Card data will be downloaded again on the next start.");
        };
        data.AddChild(refresh);
        root.AddChild(MenuKit.Card(data));

        var sandbox = new VBoxContainer();
        sandbox.AddThemeConstantOverride("separation", 10);
        sandbox.AddChild(MenuKit.SectionTitle("Sandbox"));
        sandbox.AddChild(MenuKit.Hint("A prepared board with attachments, static abilities, targeted spells and activated abilities, for trying rules quickly."));
        var start = BoardStyle.MakeButton("Open sandbox", 16);
        start.CustomMinimumSize = new Vector2(300, 44);
        start.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        start.Disabled = App.Instance.Cards is null;
        start.Pressed += () => App.Instance.GoTo(App.PlaySetupScene);
        sandbox.AddChild(MenuKit.Hint("Choose decks on the Play screen and enable \"Start from the sandbox board\"."));
        sandbox.AddChild(start);
        root.AddChild(MenuKit.Card(sandbox));

        var about = new VBoxContainer();
        about.AddThemeConstantOverride("separation", 10);
        about.AddChild(MenuKit.SectionTitle("About"));
        about.AddChild(MenuKit.Hint(
            "Arcanum is free software under the GNU Affero General Public License v3.0 or later. You can get, study, " +
            "modify and share its source code; if you run a modified version as a network service, you must offer its " +
            "source to its users too."));
        about.AddChild(MenuKit.Hint(
            "Arcanum is unofficial, non-commercial fan software, not affiliated with or endorsed by any game publisher. " +
            "Card names, rules text and images belong to their respective owners and are downloaded at runtime."));
        var source = BoardStyle.MakeButton("Source code", 16);
        source.CustomMinimumSize = new Vector2(300, 44);
        source.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        source.Pressed += () => OS.ShellOpen(App.SourceUrl);
        about.AddChild(source);
        root.AddChild(MenuKit.Card(about));
    }
}
