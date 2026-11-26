// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.Decks;
using Godot;

namespace Arcanum.Client;

/// <summary>A saved deck: user decks are editable; starter decks come from the content module.</summary>
public sealed record DeckInfo(string Name, string FormatId, string Path, bool IsStarter);

/// <summary>
/// User decks as plain-text deck lists under user://decks, with "# name:" and "# format:" header comments.
/// Starter decks from the content module are listed too, read-only.
/// </summary>
public sealed class DeckStore(string folder = "user://decks", bool starters = true)
{
    private string Directory => ProjectSettings.GlobalizePath(folder);

    public List<DeckInfo> List(Arcanum.Data.Modules.ContentModule? module)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var decks = System.IO.Directory.EnumerateFiles(Directory, "*.txt")
            .Select(path => ReadInfo(path, isStarter: false))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (module is not null && starters)
        {
            foreach (var name in module.DeckNames())
            {
                var path = System.IO.Path.Combine(module.Directory, "decks", name + ".txt");
                // Starter decks with a Commander section are commander decks.
                bool commander = module.ReadDeck(name).Split('\n').Any(l => l.Trim().Equals("Commander", StringComparison.OrdinalIgnoreCase));
                decks.Add(new DeckInfo(Pretty(name), commander ? "commander" : "casual", path, IsStarter: true));
            }
        }
        return decks;
    }

    public (DeckList Deck, DeckInfo Info) Load(DeckInfo info) => (DeckList.Parse(TextOf(info)), info);

    /// <summary>A deck's text: a starter deck's as the module read it once, the player's from its file (read again when it changed).</summary>
    private string TextOf(DeckInfo info) =>
        StarterOf(info) is { } starter ? App.Instance.Module!.ReadDeck(starter) : TextOf(info.Path);

    /// <summary>A deck only to look at (a starter deck's list is kept and shared: it must not be changed).</summary>
    public DeckList Peek(DeckInfo info) =>
        StarterOf(info) is { } starter ? App.Instance.Module!.SharedDeck(starter) : Load(info).Deck;

    /// <summary>The module's name of a starter deck; null for a deck of the player's.</summary>
    private static string? StarterOf(DeckInfo info) =>
        info.IsStarter && App.Instance.Module is { } module && System.IO.Path.GetDirectoryName(info.Path) == System.IO.Path.Combine(module.Directory, "decks")
            ? System.IO.Path.GetFileNameWithoutExtension(info.Path)
            : null;

    // The player's decks as last read: opening a file is slow on a console's memory card.
    private readonly Dictionary<string, (DateTime Written, long Length, string Text)> _texts = new(StringComparer.Ordinal);

    /// <summary>The deck files were replaced: what was read of them is forgotten.</summary>
    public void Forget() => _texts.Clear();

    private string TextOf(string path)
    {
        var file = new FileInfo(path);
        if (_texts.TryGetValue(path, out var kept) && file.Exists && kept.Written == file.LastWriteTimeUtc && kept.Length == file.Length) return kept.Text;
        var text = File.ReadAllText(path);
        _texts[path] = (file.LastWriteTimeUtc, file.Length, text);
        return text;
    }

    /// <summary>Saves a deck under its name (overwriting a deck with the same name) and returns its info.</summary>
    public DeckInfo Save(string name, string formatId, DeckList deck, DeckInfo? previous = null)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = System.IO.Path.Combine(Directory, FileName(name));
        if (previous is { IsStarter: false } && previous.Path != path && File.Exists(previous.Path)) File.Delete(previous.Path); // renamed
        _texts.Remove(path);
        File.WriteAllText(path, $"# name: {name}\n# format: {formatId}\n{deck.Export()}");
        return new DeckInfo(name, formatId, path, IsStarter: false);
    }

    public void Delete(DeckInfo info)
    {
        if (info.IsStarter) return;
        _texts.Remove(info.Path);
        if (File.Exists(info.Path)) File.Delete(info.Path);
    }

    private DeckInfo ReadInfo(string path, bool isStarter)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path), format = "casual";
        foreach (var line in TextOf(path).Split('\n', 6).Take(5))
        {
            if (line.StartsWith("# name:")) name = line[7..].Trim();
            else if (line.StartsWith("# format:")) format = line[9..].Trim();
        }
        return new DeckInfo(name, format, path, isStarter);
    }

    private static string FileName(string name)
    {
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_')).Trim();
        return (safe.Length == 0 ? "deck" : safe) + ".txt";
    }

    private static string Pretty(string fileName) =>
        string.Join(' ', fileName.Split('-', '_').Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w)) + " (starter)";
}
