// SPDX-License-Identifier: AGPL-3.0-or-later
using Godot;

namespace Arcanum.Client;

/// <summary>
/// The large pictures some screens are made of (the career's room, store and desk, the tables). Reading one takes long
/// enough on slow devices to freeze the screen that opens, so they are read in the background as the game starts and kept.
/// </summary>
public static class ScenePictures
{
    private static readonly string[] Early = { "room.jpg", "store.jpg", "desk.jpg", "table.jpg", "table2.jpg", "tournament-shield.png" };
    private static readonly Dictionary<string, Texture2D> Kept = new();
    private static readonly HashSet<string> Reading = new();

    /// <summary>Starts reading the career's pictures in the background.</summary>
    public static void ReadAhead()
    {
        foreach (var file in Early)
        {
            var path = "res://ui-art/kit/career/" + file;
            if (ResourceLoader.Exists(path) && ResourceLoader.LoadThreadedRequest(path) == Error.Ok) Reading.Add(path);
        }
    }

    /// <summary>The picture (waiting for it when it's still being read); null when there isn't one.</summary>
    public static Texture2D? Load(string path)
    {
        if (Kept.TryGetValue(path, out var kept)) return kept;
        bool ahead = Reading.Remove(path);
        var picture = ahead ? ResourceLoader.LoadThreadedGet(path) as Texture2D
            : ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        if (ahead && picture is not null) Kept[path] = picture;
        return picture;
    }
}
