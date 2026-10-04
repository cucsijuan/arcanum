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
        var install = BoardStyle.MakeButton("Install module from zip…", 16);
        install.CustomMinimumSize = new Vector2(300, 44);
        install.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        install.TooltipText = "Choose a content module's zip file (from the module's releases) to install or update it.";
        install.Pressed += ChooseModuleZip;
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        buttons.AddChild(install);
        buttons.AddChild(refresh);
        data.AddChild(buttons);
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

    private void ChooseModuleZip()
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.zip ; Content module" },
            Title = "Install a content module",
            UseNativeDialog = true,
            CurrentDir = OS.GetSystemDir(OS.SystemDir.Downloads),
        };
        dialog.FileSelected += path =>
        {
            dialog.QueueFree();
            InstallModule(path);
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void InstallModule(string zipPath)
    {
        Arcanum.Data.Modules.ModuleManifest manifest;
        try
        {
            manifest = Arcanum.Data.Modules.ModuleInstaller.InstallFromZip(zipPath, ProjectSettings.GlobalizePath("user://modules"));
        }
        catch (Exception e)
        {
            GD.PushWarning($"Module install failed: {e}");
            var error = new AcceptDialog { Title = "Install failed", DialogText = $"The module couldn't be installed:\n{e.Message}" };
            error.Confirmed += error.QueueFree;
            AddChild(error);
            error.PopupCentered();
            return;
        }
        // Card data is read when the app starts: restart to use the new module.
        var done = new ConfirmationDialog
        {
            Title = "Module installed",
            DialogText = $"{manifest.Name} {manifest.Version} is installed. Restart Arcanum to use it?" +
                         "\n(The first start downloads its card data, which takes a few minutes.)",
            OkButtonText = "Restart now",
            CancelButtonText = "Later",
        };
        done.Confirmed += () =>
        {
            OS.SetRestartOnExit(true);
            GetTree().Quit();
        };
        done.Canceled += done.QueueFree;
        AddChild(done);
        done.PopupCentered();
    }
}
