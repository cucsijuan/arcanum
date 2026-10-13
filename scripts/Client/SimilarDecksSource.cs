// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Data.Modules;
using Arcanum.Engine.Cards;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace Arcanum.Client;

/// <summary>
/// Finds community decks like a deck of a 60-card format through the module's deck search source: decks of the format
/// playing the deck's main cards (the most viewed format decks for an empty deck), read and ranked by how many of the
/// deck's cards they play. Every page is kept in user://deck_search for the days the module asks; when the source
/// can't be reached an older copy is used.
/// </summary>
public static class SimilarDecksSource
{
    private const string Folder = "user://deck_search";

    /// <summary>The deck's cards searched for, and how many decks are read.</summary>
    private const int KeyCards = 6, DecksRead = 24;

    /// <summary>The fewest cards (basic lands aside) a deck must share with the player's to be taken as like it.</summary>
    private const int MinShared = 2;

    /// <summary>The largest deck (with its sideboard) taken as a deck of a 60-card format.</summary>
    private const int MaxSize = 90;

    /// <summary>Requests under way at once at most.</summary>
    private const int Parallel = 3;

    private static HttpClient? _http;
    private static DateTime _nextRequest;
    private static readonly object Clock = new();
    private static readonly SemaphoreSlim Gate = new(Parallel, Parallel);

    /// <summary>Whether the module can find decks of the format (by this game's format id).</summary>
    public static bool Available(ContentModule? module, string formatId) => module?.Sources.DeckSearch?.Formats.ContainsKey(formatId) == true;

    /// <summary>
    /// The decks like <paramref name="deck"/>, the most alike first, each with at least <paramref name="minSize"/> main
    /// cards; null when the source can't be used.
    /// </summary>
    public static async Task<IReadOnlyList<SimilarDeck>?> FindAsync(ContentModule module, string formatId, DeckList deck, Func<string, bool> isBasicLand, int minSize,
        string colors, Func<string, int>? rank = null)
    {
        if (module.Sources.DeckSearch is not { } source || !source.Formats.TryGetValue(formatId, out var format)) return null;
        var keys = SimilarDecks.KeyCards(deck, isBasicLand, KeyCards, rank);
        var urls = keys.Count == 0
            ? new List<string> { source.PopularTemplate.Replace("{format}", Uri.EscapeDataString(format)) }
            : keys.Select(k => source.SearchTemplate.Replace("{format}", Uri.EscapeDataString(format)).Replace("{card}", Uri.EscapeDataString(k))).ToList();
        // How many of the searches found each deck: the decks playing more of the key cards are read first.
        var found = new Dictionary<long, (FoundDeck Deck, int Hits)>();
        var pages = await Task.WhenAll(urls.Select(url => FetchAsync(module, source, url)));
        foreach (var (url, json) in urls.Zip(pages))
        {
            if (json is null) continue;
            List<FoundDeck> page;
            try { page = SimilarDecks.ParseSearch(json); }
            catch (System.Text.Json.JsonException e) { GD.PushWarning($"Deck search {url}: {e.Message}"); continue; }
            foreach (var d in page.Where(d => d.Size >= minSize && d.Size <= MaxSize && SimilarDecks.FitsColors(d, colors)))
                found[d.Id] = (d, found.GetValueOrDefault(d.Id).Hits + 1);
        }
        if (found.Count == 0) return urls.Count > 0 ? Array.Empty<SimilarDeck>() : null;
        var decks = new List<SimilarDeck>();
        var candidates = found.Values.OrderByDescending(f => f.Hits).ThenByDescending(f => f.Deck.Views).Take(DecksRead).Select(f => f.Deck).ToList();
        var lists = await Task.WhenAll(candidates.Select(c => FetchAsync(module, source, source.DeckTemplate.Replace("{id}", c.Id.ToString()))));
        foreach (var (candidate, json) in candidates.Zip(lists))
        {
            if (json is null) continue;
            DeckList? list;
            try { list = SimilarDecks.ParseDeck(json); }
            catch (System.Text.Json.JsonException e) { GD.PushWarning($"Deck {candidate.Id}: {e.Message}"); continue; }
            if (list is null || list.Main.Sum(e => e.Count) < minSize) continue;
            int shared = SimilarDecks.SharedCards(deck, list, isBasicLand);
            if (keys.Count > 0 && shared < Math.Min(MinShared, keys.Count)) continue; // it only happens to play one of the cards searched for
            decks.Add(new SimilarDeck(candidate, list, shared));
        }
        return decks.OrderByDescending(d => d.Shared).ThenByDescending(d => d.Found.Views).ToList();
    }

    /// <summary>The decks found for the deck when the suggestions were last asked for, by format.</summary>
    private static readonly Dictionary<string, IReadOnlyList<SimilarDeck>> Found = new();

    /// <summary>
    /// Suggestions for <paramref name="deck"/> from the decks like it (all of them, or the one whose id is
    /// <paramref name="variant"/>); the decks are looked for again when <paramref name="refind"/> is set or the format's
    /// were never found. Null when the source can't be used or no deck is like it.
    /// </summary>
    public static async Task<CommanderInsights?> SuggestAsync(ContentModule module, CardDatabase cards, string formatId, int minSize, DeckList deck, string? variant, bool refind)
    {
        if (refind || !Found.ContainsKey(formatId))
        {
            var decks = await FindAsync(module, formatId, deck, name => IsBasicLand(cards, name), minSize, Colors(cards, deck), name => Rarity(cards, name));
            if (decks is null) return null;
            Found[formatId] = decks;
        }
        var all = Found[formatId];
        if (all.Count == 0) return null;
        var chosen = variant is null ? all : all.Where(d => d.Found.Id.ToString() == variant).ToList();
        return SimilarDecks.Suggestions(deck, chosen, name => IsBasicLand(cards, name), name => TypeKey(cards, name), all);
    }

    /// <summary>The decks found last for the format (to show how many of their cards a player owns), empty before.</summary>
    public static IReadOnlyList<SimilarDeck> LastFound(string formatId) => Found.GetValueOrDefault(formatId) ?? Array.Empty<SimilarDeck>();

    /// <summary>The average of the decks found last (or the deck whose id is <paramref name="variant"/>), null before any.</summary>
    public static DeckList? Average(string formatId, string? variant, int size)
    {
        var all = LastFound(formatId);
        if (variant is not null) return all.FirstOrDefault(d => d.Found.Id.ToString() == variant)?.List;
        return all.Count == 0 ? null : SimilarDecks.Average(all, size);
    }

    /// <summary>The colors of the deck's main cards (their color identities: W, U, B, R, G).</summary>
    private static string Colors(CardDatabase cards, DeckList deck)
    {
        var colors = deck.Main.SelectMany(e => cards.Find(e.Name)?.Definition.ColorIdentity ?? Array.Empty<string>()).ToHashSet();
        return string.Concat("WUBRG".Where(c => colors.Contains(c.ToString())));
    }

    /// <summary>A card's rarest printing: 0 common, 1 uncommon, 2 rare, 3 mythic.</summary>
    private static int Rarity(CardDatabase cards, string name) =>
        cards.Find(name)?.Record.Printings.Select(p => p.Rarity switch { "mythic" => 3, "rare" => 2, "uncommon" => 1, _ => 0 }).DefaultIfEmpty(0).Max() ?? 0;

    private static bool IsBasicLand(CardDatabase cards, string name) =>
        cards.Find(name) is { } card && (card.Definition.Supertypes & Supertype.Basic) != 0 && card.Definition.Is(CardType.Land);

    private static string? TypeKey(CardDatabase cards, string name) => cards.Find(name)?.Definition switch
    {
        null => null,
        var d when d.Is(CardType.Land) => "land",
        var d when d.Is(CardType.Creature) => "creature",
        var d when d.Is(CardType.Planeswalker) => "planeswalker",
        var d when d.Is(CardType.Battle) => "battle",
        var d when d.Is(CardType.Instant) => "instant",
        var d when d.Is(CardType.Sorcery) => "sorcery",
        var d when d.Is(CardType.Artifact) => "artifact",
        var d when d.Is(CardType.Enchantment) => "enchantment",
        _ => null,
    };

    private static async Task<string?> FetchAsync(ContentModule module, DeckSearchSource source, string url)
    {
        var folder = ProjectSettings.GlobalizePath(Folder);
        var file = Path.Combine(folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".json");
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(source.CacheDays))
            return await File.ReadAllTextAsync(file);
        await Gate.WaitAsync();
        try
        {
            // Requests start spaced as the module asks, not to load the source.
            DateTime start;
            lock (Clock)
            {
                start = _nextRequest > DateTime.UtcNow ? _nextRequest : DateTime.UtcNow;
                _nextRequest = start.AddMilliseconds(source.MinIntervalMs);
            }
            if (start > DateTime.UtcNow) await Task.Delay(start - DateTime.UtcNow);
            _http ??= CreateClient(module.Sources.UserAgent);
            using var response = await _http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(file, json);
                return json;
            }
            if (response.StatusCode is not (System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound))
                GD.PushWarning($"Deck search: {url} answered {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            GD.PushWarning($"Deck search: {url}: {e.Message}");
        }
        finally
        {
            Gate.Release();
        }
        return File.Exists(file) ? await File.ReadAllTextAsync(file) : null;
    }

    private static HttpClient CreateClient(string userAgent)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
