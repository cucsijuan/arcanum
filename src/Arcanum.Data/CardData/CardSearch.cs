// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Arcanum.Data.CardData;

/// <summary>
/// The card search language every card search box uses: loose words search names; keyword terms (<c>t:goblin</c>,
/// <c>c&gt;=uw</c>, <c>mv&lt;=3</c>, <c>o:"draw a card"</c>, <c>is:commander</c>, <c>s:fdn</c>, <c>f:commander</c>...)
/// search the rest. Terms are all required; <c>or</c> between them and parentheses group them, a leading <c>-</c>
/// negates one, <c>!name</c> is an exact name and <c>/.../</c> a regular expression (names, types and rules text).
/// Terms the game has no data for (artists, prices...) or doesn't know are ignored, with a warning.
/// </summary>
public static class CardSearch
{
    /// <summary>Reads a search; <paramref name="findSet"/> gives set release dates (<c>date&gt;fdn</c>).</summary>
    /// <param name="extra">Number terms a screen adds (a career's prices and copies), by keyword.</param>
    public static CardFilter Parse(string text, Func<string, SetInfo?>? findSet = null, SearchTerms? extra = null)
    {
        var parser = new Parser(Lex(text ?? ""), findSet, extra);
        var root = parser.ParseAll();
        return new CardFilter(root, parser.Warnings, parser.Order, parser.Descending);
    }

    // ---------------------------------------------------------------- tokens

    private abstract record Token;
    private sealed record Open : Token;
    private sealed record Close : Token;
    private sealed record OrWord : Token;
    private sealed record Negate : Token;
    private sealed record Word(string Text) : Token;

    private static List<Token> Lex(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '(') { tokens.Add(new Open()); i++; continue; }
            if (ch == ')') { tokens.Add(new Close()); i++; continue; }
            if (ch == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                tokens.Add(new Negate());
                i++;
                continue;
            }
            int start = i;
            char? quote = null; // inside "..." or /.../
            while (i < text.Length)
            {
                char c = text[i];
                if (quote is { } q)
                {
                    if (c == '\\' && q == '/' && i + 1 < text.Length) { i += 2; continue; }
                    if (c == q) quote = null;
                    i++;
                    continue;
                }
                if (char.IsWhiteSpace(c) || c == ')' || (c == '(' && i > start)) break;
                if (c == '"') quote = '"';
                // A regular expression starts right after a keyword's operator: t:/.../
                else if (c == '/' && i > start && ":=<>".Contains(text[i - 1])) quote = '/';
                i++;
            }
            var word = text[start..i];
            tokens.Add(word.Equals("or", StringComparison.OrdinalIgnoreCase) ? new OrWord() : new Word(word));
        }
        return tokens;
    }

    // ---------------------------------------------------------------- parsing

    private sealed class Parser(List<Token> tokens, Func<string, SetInfo?>? findSet, SearchTerms? extra)
    {
        private int _at;
        public List<string> Warnings { get; } = new();
        public string? Order { get; private set; }
        public bool Descending { get; private set; }

        public Node ParseAll()
        {
            var node = ParseOr();
            while (_at < tokens.Count) // a stray ")": skip it and go on
            {
                _at++;
                Warnings.Add("An unmatched \")\" was ignored.");
                node = new All(new[] { node, ParseOr() });
            }
            return node;
        }

        private Node ParseOr()
        {
            var options = new List<Node> { ParseAnd() };
            while (_at < tokens.Count && tokens[_at] is OrWord)
            {
                _at++;
                options.Add(ParseAnd());
            }
            return options.Count == 1 ? options[0] : new Any(options);
        }

        private Node ParseAnd()
        {
            var terms = new List<Node>();
            while (_at < tokens.Count && tokens[_at] is not (OrWord or Close))
                terms.Add(ParseUnary());
            return terms.Count == 1 ? terms[0] : new All(terms);
        }

        private Node ParseUnary()
        {
            var token = tokens[_at++];
            switch (token)
            {
                case Negate:
                    return _at < tokens.Count && tokens[_at] is not (OrWord or Close) ? new Not(ParseUnary()) : Node.True;
                case Open:
                    var inner = ParseOr();
                    if (_at < tokens.Count && tokens[_at] is Close) _at++;
                    else Warnings.Add("A \"(\" was never closed.");
                    return inner;
                case Word word:
                    return Term(word.Text);
                default:
                    return Node.True;
            }
        }

        private static readonly Regex KeywordTerm = new(@"^([a-zA-Z]+)(!=|<=|>=|:|=|<|>)(.*)$", RegexOptions.Compiled);

        private Node Term(string text)
        {
            if (text.StartsWith('!') && text.Length > 1)
            {
                string exact = Unquote(text[1..]);
                return new Is(c => Names(c.Entry).Any(n => n.Equals(exact, StringComparison.OrdinalIgnoreCase)));
            }
            var m = KeywordTerm.Match(text);
            if (!m.Success)
            {
                string loose = Unquote(text);
                return new Is(c => Names(c.Entry).Any(n => Has(n, loose)));
            }
            string key = m.Groups[1].Value.ToLowerInvariant(), op = m.Groups[2].Value, value = m.Groups[3].Value;
            try
            {
                return Keyword(key, op, value) ?? Ignore($"\"{key}\" isn't a search keyword the game knows: {text} was ignored.");
            }
            catch (SearchError e)
            {
                return Ignore($"{text} was ignored: {e.Message}");
            }
        }

        private Node Ignore(string warning)
        {
            Warnings.Add(warning);
            return Node.True;
        }

        private Node? Keyword(string key, string op, string raw)
        {
            string value = Unquote(raw);
            string lower = value.ToLowerInvariant();
            if (extra?.Numbers.GetValueOrDefault(key) is { } number)
            {
                if (!double.TryParse(value.TrimStart('$'), NumberStyles.Float, CultureInfo.InvariantCulture, out double wanted))
                    throw new SearchError($"\"{value}\" isn't a number.");
                return new Is(c => number(c.Entry, c.Printing, c.Item) is not { } have || Compare(have, op, wanted), printing: true);
            }
            switch (key)
            {
                case "c" or "color" or "colour":
                    return ColorTerm(op, lower, c => ColorsOf(c.Entry.Record), identity: false);
                case "id" or "identity" or "ci" or "commander" or "cmd":
                    return ColorTerm(op, lower, c => c.Entry.Record.ColorIdentity.Select(x => char.ToUpperInvariant(x[0])).ToHashSet(), identity: true);

                case "t" or "type":
                    return TextTerm(raw, c => TypeLines(c.Entry.Record));
                case "o" or "oracle" or "fo" or "fulloracle":
                    return TextTerm(raw, c => OracleTexts(c.Entry), nameStandsFor: true);
                case "name" or "n":
                    return TextTerm(raw, c => Names(c.Entry));
                case "kw" or "keyword":
                    return new Is(c => c.Entry.Record.Keywords.Any(k => k.Equals(value, StringComparison.OrdinalIgnoreCase)
                                                                       || k.StartsWith(value + " ", StringComparison.OrdinalIgnoreCase)));

                case "m" or "mana":
                    return ManaTerm(op, value);
                case "mv" or "cmc" or "manavalue":
                    if (lower is "even" or "odd") return new Is(c => c.Entry.ManaValue % 2 == (lower == "even" ? 0 : 1));
                    return NumberTerm(op, value, c => c.Entry.ManaValue);
                case "devotion":
                    return DevotionTerm(op, value);
                case "produces":
                    return ColorTerm(op, lower, c => c.Entry.Record.ProducedMana.Select(x => char.ToUpperInvariant(x[0])).ToHashSet(), identity: false, producing: true);

                case "pow" or "power":
                    return NumberTerm(op, value, c => Stat(c.Entry.Record.Power ?? c.Entry.Record.Faces.FirstOrDefault(f => f.Power is not null)?.Power));
                case "tou" or "toughness":
                    return NumberTerm(op, value, c => Stat(c.Entry.Record.Toughness ?? c.Entry.Record.Faces.FirstOrDefault(f => f.Toughness is not null)?.Toughness));
                case "pt" or "powtou":
                    return NumberTerm(op, value, c => Stat(c.Entry.Record.Power ?? c.Entry.Record.Faces.FirstOrDefault(f => f.Power is not null)?.Power) is { } p
                                                      && Stat(c.Entry.Record.Toughness ?? c.Entry.Record.Faces.FirstOrDefault(f => f.Toughness is not null)?.Toughness) is { } t ? p + t : null);
                case "loy" or "loyalty":
                    return NumberTerm(op, value, c => Stat(c.Entry.Record.Loyalty ?? c.Entry.Record.Faces.FirstOrDefault(f => f.Loyalty is not null)?.Loyalty));

                case "is":
                    return IsTerm(lower);
                case "not":
                    return IsTerm(lower) is { } positive ? new Not(positive) : null;
                case "has":
                    return lower switch
                    {
                        "indicator" => new Is(c => c.Entry.Record.Faces.Any(f => f.ManaCost.Length == 0 && f.Colors.Count > 0) && c.Entry.Record.Faces.Count > 1),
                        _ => throw new SearchError($"the game has no data for has:{lower}."),
                    };

                case "r" or "rarity":
                    return RarityTerm(op, lower);
                case "s" or "e" or "set" or "edition":
                    return new Is(c => c.Printing is { } p && p.Set.Equals(value, StringComparison.OrdinalIgnoreCase), printing: true);
                case "cn" or "number":
                    return CollectorNumberTerm(op, value);
                case "st" or "settype":
                    return new Is(c => c.Printing is { } p && SetTypeIs(p, lower), printing: true);
                case "in":
                    return InTerm(lower);
                case "f" or "format" or "legal":
                    return new Is(c => c.Entry.Record.Legalities.TryGetValue(lower, out var s) && s is "legal" or "restricted");
                case "banned":
                    return new Is(c => c.Entry.Record.Legalities.TryGetValue(lower, out var s) && s == "banned");
                case "restricted":
                    return new Is(c => c.Entry.Record.Legalities.TryGetValue(lower, out var s) && s == "restricted");
                case "game":
                    return lower == "paper" ? Node.True : Node.False;
                case "lang" or "language":
                    return lower is "en" or "english" or "any" ? Node.True : Node.False;

                case "year":
                    return DateTerm(op, value, yearOnly: true);
                case "date":
                    return DateTerm(op, value, yearOnly: false);
                case "prints" or "paperprints":
                    return NumberTerm(op, value, c => c.Entry.Record.Printings.Count);
                case "sets" or "papersets":
                    return NumberTerm(op, value, c => c.Entry.Record.Printings.Select(p => p.Set.ToLowerInvariant()).Distinct().Count());
                case "illustrations":
                    return NumberTerm(op, value, c => c.Entry.Record.Printings.Select(p => p.Illustration).OfType<string>().Distinct().Count());

                case "order" or "sort":
                    if (!Orders.Contains(lower)) throw new SearchError($"the game can't sort by {lower}.");
                    Order = lower;
                    return Node.True;
                case "direction" or "dir":
                    Descending = lower == "desc";
                    return Node.True;
                case "unique" or "display" or "prefer" or "include":
                    return Node.True; // how results are shown elsewhere: nothing to filter

                case "usd" or "eur":
                    return PriceTerm(op, value, euros: key == "eur");
                case "border":
                    return new Is(c => c.Printing is { } p && p.Border.Equals(lower, StringComparison.OrdinalIgnoreCase), printing: true);
                case "frame":
                    // A frame edition ("frame:2015") or a frame effect ("frame:showcase").
                    return new Is(c => c.Printing is { } p && (p.Frame.Equals(lower, StringComparison.OrdinalIgnoreCase)
                                                               || p.FrameEffects.Contains(lower, StringComparer.OrdinalIgnoreCase)), printing: true);

                case "a" or "artist" or "artists" or "ft" or "flavor" or "wm" or "watermark" or "stamp"
                    or "tix" or "cheapest" or "cube" or "otag" or "oracletag" or "function" or "atag" or "arttag"
                    or "art" or "edhrec" or "edhrecrank" or "b" or "block" or "g" or "group" or "new":
                    throw new SearchError($"the game has no data for {key}:.");
            }
            return null;
        }

        // ------------------------------------------------------------ colors

        private static readonly Dictionary<string, string> ColorNames = new()
        {
            ["white"] = "W", ["blue"] = "U", ["black"] = "B", ["red"] = "R", ["green"] = "G",
            ["azorius"] = "WU", ["dimir"] = "UB", ["rakdos"] = "BR", ["gruul"] = "RG", ["selesnya"] = "GW",
            ["orzhov"] = "WB", ["izzet"] = "UR", ["golgari"] = "BG", ["boros"] = "RW", ["simic"] = "GU",
            ["bant"] = "GWU", ["esper"] = "WUB", ["grixis"] = "UBR", ["jund"] = "BRG", ["naya"] = "RGW",
            ["abzan"] = "WBG", ["jeskai"] = "URW", ["sultai"] = "BGU", ["mardu"] = "RWB", ["temur"] = "GUR",
            ["silverquill"] = "WB", ["prismari"] = "UR", ["witherbloom"] = "BG", ["lorehold"] = "RW", ["quandrix"] = "GU",
            ["chaos"] = "UBRG", ["aggression"] = "BRGW", ["altruism"] = "RGWU", ["growth"] = "GWUB", ["artifice"] = "WUBR",
            ["rainbow"] = "WUBRG", ["fivecolor"] = "WUBRG",
        };

        private static Node ColorTerm(string op, string value, Func<Context, HashSet<char>> colorsOf, bool identity, bool producing = false)
        {
            if (int.TryParse(value, out int count))
                return new Is(c => Compare(colorsOf(c).Count(x => x != 'C'), op == ":" ? "=" : op, count));
            if (!producing && value is "c" or "colorless")
                return new Is(c => colorsOf(c).Count == 0);
            if (!producing && value is "m" or "multicolor" or "multicolour")
                return new Is(c => colorsOf(c).Count >= 2);
            var wanted = ColorNames.TryGetValue(value, out var letters) ? letters : value.ToUpperInvariant();
            if (wanted.Length == 0 || wanted.Any(ch => !(producing ? "WUBRGC" : "WUBRG").Contains(ch)))
                throw new SearchError($"\"{value}\" isn't a color.");
            var set = wanted.ToHashSet();
            // c:uw means at least those colors; id:uw means a commander of those colors could play it.
            string effective = op == ":" ? (identity ? "<=" : ">=") : op;
            return new Is(c =>
            {
                var have = colorsOf(c);
                return effective switch
                {
                    "=" => have.SetEquals(set),
                    "!=" => !have.SetEquals(set),
                    ">=" => have.IsSupersetOf(set),
                    ">" => have.IsProperSupersetOf(set),
                    "<=" => have.IsSubsetOf(set),
                    "<" => have.IsProperSubsetOf(set),
                    _ => false,
                };
            });
        }

        private static HashSet<char> ColorsOf(CardRecord record)
        {
            var colors = record.Colors.Count > 0 ? record.Colors : record.Faces.SelectMany(f => f.Colors).ToList();
            return colors.Select(x => char.ToUpperInvariant(x[0])).ToHashSet();
        }

        // ------------------------------------------------------------ text

        private Node TextTerm(string raw, Func<Context, IEnumerable<string>> texts, bool nameStandsFor = false)
        {
            if (raw.Length >= 2 && raw.StartsWith('/') && raw.EndsWith('/'))
            {
                Regex regex;
                try { regex = new Regex(raw[1..^1].Replace(@"\/", "/"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
                catch (ArgumentException e) { throw new SearchError($"it isn't a valid regular expression ({e.Message})."); }
                return new Is(c => texts(c).Any(t =>
                {
                    try { return regex.IsMatch(t); }
                    catch (RegexMatchTimeoutException) { return false; }
                }));
            }
            string value = Unquote(raw);
            if (!nameStandsFor || !value.Contains('~')) return new Is(c => texts(c).Any(t => Has(t, value)));
            // "~" stands for the card's name, or "this" and its type the way current rules text names a card.
            var pieces = value.Split('~');
            var parts = pieces.Select(Regex.Escape).ToList();
            return new Is(c =>
            {
                if (!texts(c).Any(t => pieces.All(piece => Has(t, piece.Trim())))) return false; // cheap check first
                string self = "(?:" + string.Join("|", Names(c.Entry).Select(Regex.Escape)) + @"|this(?: [\w-]+)?)";
                var regex = new Regex(string.Join(self, parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return texts(c).Any(regex.IsMatch);
            });
        }

        // ------------------------------------------------------------ mana

        private static Node ManaTerm(string op, string value)
        {
            var wanted = ParseCost(value, shorthand: true);
            return new Is(c => CostsOf(c.Entry.Record).Any(cost => CompareCosts(ParseCost(cost, shorthand: false), wanted, op == ":" ? ">=" : op)));
        }

        private static Node DevotionTerm(string op, string value)
        {
            var symbols = ParseCost(value, shorthand: true);
            if (symbols.Generic > 0 || symbols.Symbols.Count == 0) throw new SearchError("devotion needs colored mana symbols.");
            var colors = symbols.Symbols.Keys.SelectMany(s => s.Where(ch => "WUBRG".Contains(ch))).ToHashSet();
            int count = symbols.Symbols.Values.Sum();
            return new Is(c =>
            {
                if (!IsPermanent(c.Entry.Record)) return false;
                int devotion = CostsOf(c.Entry.Record).Take(1).Select(cost => ParseCost(cost, shorthand: false))
                    .Sum(cost => cost.Symbols.Where(kv => kv.Key.Any(colors.Contains)).Sum(kv => kv.Value));
                return Compare(devotion, op == ":" ? ">=" : op, count);
            });
        }

        private sealed record Cost(int Generic, Dictionary<string, int> Symbols);

        private static Cost ParseCost(string text, bool shorthand)
        {
            int generic = 0;
            var symbols = new Dictionary<string, int>();
            void Add(string symbol)
            {
                if (int.TryParse(symbol, out int n)) generic += n;
                else symbols[symbol] = symbols.GetValueOrDefault(symbol) + 1;
            }
            int i = 0;
            while (i < text.Length)
            {
                char ch = text[i];
                if (ch == '{')
                {
                    int end = text.IndexOf('}', i);
                    if (end < 0) throw new SearchError("a \"{\" was never closed.");
                    Add(text[(i + 1)..end].ToUpperInvariant());
                    i = end + 1;
                }
                else if (shorthand && char.IsDigit(ch))
                {
                    int start = i;
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                    generic += int.Parse(text[start..i]);
                }
                else if (shorthand && "WUBRGCXYZS".Contains(char.ToUpperInvariant(ch)))
                {
                    Add(char.ToUpperInvariant(ch).ToString());
                    i++;
                }
                else if (char.IsWhiteSpace(ch) || ch == '/') i++;
                else if (shorthand) throw new SearchError($"\"{ch}\" isn't a mana symbol.");
                else i++;
            }
            return new Cost(generic, symbols);
        }

        private static bool CompareCosts(Cost have, Cost want, string op)
        {
            bool Covers(Cost a, Cost b) => a.Generic >= b.Generic && b.Symbols.All(kv => a.Symbols.GetValueOrDefault(kv.Key) >= kv.Value);
            bool same = Covers(have, want) && Covers(want, have);
            return op switch
            {
                "=" => same,
                "!=" => !same,
                ">=" => Covers(have, want),
                ">" => Covers(have, want) && !same,
                "<=" => Covers(want, have),
                "<" => Covers(want, have) && !same,
                _ => false,
            };
        }

        private static IEnumerable<string> CostsOf(CardRecord record) =>
            record.Faces.Count > 0 && record.ManaCost.Length == 0 ? record.Faces.Select(f => f.ManaCost) : new[] { record.ManaCost };

        // ------------------------------------------------------------ numbers

        private static readonly string[] Fields = { "pow", "power", "tou", "toughness", "loy", "loyalty", "mv", "cmc", "manavalue" };

        private static Node NumberTerm(string op, string value, Func<Context, int?> number)
        {
            string compare = op == ":" ? "=" : op;
            if (Fields.Contains(value.ToLowerInvariant()))
            {
                Func<Context, int?> other = value.ToLowerInvariant() switch
                {
                    "pow" or "power" => c => Stat(c.Entry.Record.Power),
                    "tou" or "toughness" => c => Stat(c.Entry.Record.Toughness),
                    "loy" or "loyalty" => c => Stat(c.Entry.Record.Loyalty),
                    _ => c => c.Entry.ManaValue,
                };
                return new Is(c => number(c) is { } a && other(c) is { } b && Compare(a, compare, b));
            }
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
                throw new SearchError($"\"{value}\" isn't a number.");
            return new Is(c => number(c) is { } n && Compare(n, compare, target));
        }

        /// <summary>A power, toughness or loyalty as a number: "*" counts as 0, "1+*" as 1.</summary>
        private static int? Stat(string? text)
        {
            if (text is null) return null;
            var digits = new string(text.TakeWhile(ch => char.IsDigit(ch) || ch == '-').ToArray());
            return int.TryParse(digits, out int n) ? n : 0;
        }

        // ------------------------------------------------------------ is:

        private static readonly string[] PartyTypes = { "Cleric", "Rogue", "Warrior", "Wizard" };
        private static readonly string[] OutlawTypes = { "Assassin", "Mercenary", "Pirate", "Rogue", "Warlock" };

        private static Node? IsTerm(string value) => value switch
        {
            "split" => Layout("split"),
            "flip" => Layout("flip"),
            "transform" or "tdfc" => Layout("transform"),
            "meld" => Layout("meld"),
            "leveler" => Layout("leveler"),
            "mdfc" => Layout("modal_dfc"),
            "dfc" => Layout("transform", "modal_dfc", "meld", "reversible_card"),
            "adventure" => Layout("adventure"),
            "saga" => new Is(c => Subtypes(c.Entry.Record).Contains("Saga")),
            "spell" => new Is(c => !FrontType(c.Entry.Record).Contains("Land")),
            "permanent" => new Is(c => IsPermanent(c.Entry.Record)),
            "historic" => new Is(c => FrontType(c.Entry.Record) is var t && (t.Contains("Legendary") || t.Contains("Artifact") || Subtypes(c.Entry.Record).Contains("Saga"))),
            "party" => new Is(c => FrontType(c.Entry.Record).Contains("Creature") && Subtypes(c.Entry.Record).Any(PartyTypes.Contains)),
            "outlaw" => new Is(c => FrontType(c.Entry.Record).Contains("Creature") && Subtypes(c.Entry.Record).Any(OutlawTypes.Contains)),
            "modal" => new Is(c => OracleTexts(c.Entry).Any(t => t.Contains('•'))),
            "vanilla" => new Is(c => FrontType(c.Entry.Record).Contains("Creature") && OracleTexts(c.Entry).All(t => t.Trim().Length == 0)),
            "frenchvanilla" => new Is(c => FrontType(c.Entry.Record).Contains("Creature") && IsFrenchVanilla(c.Entry.Record)),
            "bear" => new Is(c => FrontType(c.Entry.Record).Contains("Creature") && c.Entry.ManaValue == 2 && Stat(c.Entry.Record.Power) == 2 && Stat(c.Entry.Record.Toughness) == 2),
            "manland" or "creatureland" => new Is(c => FrontType(c.Entry.Record).Contains("Land") && OracleTexts(c.Entry).Any(t => Regex.IsMatch(t, @"becomes? an? .*creature", RegexOptions.IgnoreCase))),
            "hybrid" => new Is(c => CostsOf(c.Entry.Record).Any(cost => Regex.IsMatch(cost, @"\{[^}P]*/[^}P]*\}"))),
            "phyrexian" => new Is(c => CostsOf(c.Entry.Record).Any(cost => Regex.IsMatch(cost, @"\{[^}]*P\}"))),
            "commander" => new Is(c => (FrontType(c.Entry.Record) is var t && t.Contains("Legendary") && t.Contains("Creature"))
                                       || OracleTexts(c.Entry).Any(o => Has(o, "can be your commander"))),
            "brawler" => new Is(c => FrontType(c.Entry.Record) is var t && t.Contains("Legendary") && (t.Contains("Creature") || t.Contains("Planeswalker"))),
            "companion" => new Is(c => OracleTexts(c.Entry).Any(o => o.Contains("Companion —"))),
            "partner" => new Is(c => c.Entry.Record.Keywords.Any(k => k.StartsWith("Partner", StringComparison.OrdinalIgnoreCase) || k.Equals("Friends forever", StringComparison.OrdinalIgnoreCase))
                                     || OracleTexts(c.Entry).Any(o => Has(o, "Choose a Background"))),
            "foil" => new Is(c => c.Foil ?? c.Printing is { Foil: true } or { FoilOnly: true }, printing: true),
            "nonfoil" => new Is(c => c.Foil is { } f ? !f : c.Printing is { FoilOnly: false }, printing: true),
            "booster" => new Is(c => c.Printing is { Booster: true }, printing: true),
            "promo" => new Is(c => c.Printing is { } p && SetTypeIs(p, "promo"), printing: true),
            "funny" => new Is(c => c.Printing is { } p && SetTypeIs(p, "funny"), printing: true),
            "reprint" => new Is(c => c.Printing is { } p && c.Entry.Record.Printings.Any(o => string.CompareOrdinal(o.Released, p.Released) < 0), printing: true),
            "unique" => new Is(c => c.Entry.Record.Printings.Select(p => p.Set.ToLowerInvariant()).Distinct().Count() <= 1),
            "digital" => Node.False,
            "token" => new Is(c => c.Entry.Record.IsToken),
            "reserved" => new Is(c => c.Entry.Record.Reserved),
            "gamechanger" => new Is(c => c.Entry.Record.GameChanger),
            "alchemy" or "rebalanced" or "oversized" => Node.False, // digital-only and oversized printings aren't kept
            "firstprint" or "firstprinting" => new Is(c => c.Printing is { } p && !c.Entry.Record.Printings.Any(o => string.CompareOrdinal(o.Released, p.Released) < 0), printing: true),
            "etched" => new Is(c => c.Printing is { Etched: true }, printing: true),
            "glossy" => new Is(c => c.Printing is { Glossy: true }, printing: true),
            "full" or "fullart" => new Is(c => c.Printing is { FullArt: true }, printing: true),
            "textless" => new Is(c => c.Printing is { Textless: true }, printing: true),
            "hires" => new Is(c => c.Printing is { HighRes: true }, printing: true),
            "borderless" => new Is(c => c.Printing is { Border: "borderless" }, printing: true),
            "showcase" or "extendedart" or "colorshifted" or "inverted" or "nyxtouched" or "companion_frame"
                => FrameEffect(value),
            "prerelease" or "datestamped" or "buyabox" or "giftbox" or "intropack" or "gameday" or "release" or "league"
                or "planeswalkerdeck" or "playtest" or "boosterfun" or "serialized" or "promopack" or "bundle" or "starterdeck"
                or "brawldeck" or "themepack" or "convention" or "tourney" or "judgegift" or "setpromo" or "stamped"
                or "halofoil" or "galaxyfoil" or "surgefoil" or "textured" or "confettifoil" or "doublerainbow" or "fracturefoil"
                => new Is(c => c.Printing is { } p && p.PromoTypes.Contains(value, StringComparer.OrdinalIgnoreCase), printing: true),
            _ when LandCycles.TryGetValue(value, out var cycle) => new Is(c => FrontType(c.Entry.Record).Contains("Land") && cycle(c.Entry)),
            "supported" => new Is(c => c.Entry.Support == CardSupport.Full),
            "unsupported" => new Is(c => c.Entry.Support != CardSupport.Full),
            _ => throw new SearchError($"the game doesn't know is:{value}."),
        };

        private static Node Layout(params string[] layouts) => new Is(c => layouts.Contains(c.Entry.Record.Layout));

        private static Node FrameEffect(string effect) =>
            new Is(c => c.Printing is { } p && p.FrameEffects.Contains(effect, StringComparer.OrdinalIgnoreCase), printing: true);

        /// <summary>"usd>=2", "eur<0.5": a printing's price when the card data was downloaded (foil or not when known, else the cheaper).</summary>
        private static Node PriceTerm(string op, string value, bool euros)
        {
            if (!double.TryParse(value.TrimStart('$', '€'), NumberStyles.Float, CultureInfo.InvariantCulture, out double wanted))
                throw new SearchError($"\"{value}\" isn't a price.");
            return new Is(c => c.Printing is { } p && (euros ? p.Prices.EurFor(c.Foil) : p.Prices.UsdFor(c.Foil)) is { } have && Compare(have, op, wanted), printing: true);
        }

        // ------------------------------------------------------------ land cycles

        private static readonly string[] BasicTypes = { "Plains", "Island", "Swamp", "Mountain", "Forest" };

        private static int BasicTypeCount(CardEntry e) => Subtypes(e.Record).Count(BasicTypes.Contains);

        private static bool Text(CardEntry e, string pattern) => OracleTexts(e).Any(t => Regex.IsMatch(t, pattern, RegexOptions.IgnoreCase));

        private const string TwoColors = @"\{T\}: Add \{[WUBRG]\} or \{[WUBRG]\}\.";
        private const string Basic = "(?:Plains|Island|Swamp|Mountain|Forest)";

        /// <summary>The nicknames of the land cycles, by what their rules text says.</summary>
        private static readonly Dictionary<string, Func<CardEntry, bool>> LandCycles = new()
        {
            ["dual"] = e => BasicTypeCount(e) == 2 && Regex.IsMatch(e.Record.OracleText.Trim(), @"^\(\{T\}: Add \{[WUBRG]\} or \{[WUBRG]\}\.\)$"),
            ["shockland"] = e => BasicTypeCount(e) == 2 && Text(e, @"you may pay 2 life\. If you don't, it enters tapped"),
            ["fetchland"] = e => Text(e, $@"Pay 1 life, Sacrifice this land: Search your library for an? {Basic} or {Basic} card"),
            ["checkland"] = e => Text(e, $@"^This land enters tapped unless you control an? {Basic} or an? {Basic}\."),
            ["fastland"] = e => Text(e, @"enters tapped unless you control two or fewer other lands"),
            ["slowland"] = e => Text(e, @"enters tapped unless you control two or more other lands"),
            ["painland"] = e => Text(e, TwoColors + @" This land deals 1 damage to you\."),
            ["filterland"] = e => Text(e, @"\{[WUBRG]/[WUBRG]\}, \{T\}: Add \{[WUBRG]\}\{[WUBRG]\}, \{[WUBRG]\}\{[WUBRG]\}, or \{[WUBRG]\}\{[WUBRG]\}"),
            ["gainland"] = e => Text(e, @"When this land enters, you gain 1 life\.") && Text(e, TwoColors),
            ["scryland"] = e => Text(e, @"When this land enters, scry 1\.") && Text(e, TwoColors),
            ["surveilland"] = e => BasicTypeCount(e) == 2 && Text(e, @"When this land enters, surveil 1\."),
            ["bounceland"] = e => Text(e, @"When this land enters, return a land you control to its owner's hand"),
            ["karoo"] = e => Text(e, @"When this land enters, return a land you control to its owner's hand"),
            ["canopyland"] = e => Text(e, @"\{T\}, Pay 1 life: Add \{[WUBRG]\} or \{[WUBRG]\}\.") && Text(e, @"Sacrifice this land: Draw a card"),
            ["canland"] = e => Text(e, @"\{T\}, Pay 1 life: Add \{[WUBRG]\} or \{[WUBRG]\}\.") && Text(e, @"Sacrifice this land: Draw a card"),
            ["bikeland"] = e => BasicTypeCount(e) == 2 && Text(e, @"This land enters tapped\.") && Text(e, @"Cycling \{2\}"),
            ["cycleland"] = e => BasicTypeCount(e) == 2 && Text(e, @"This land enters tapped\.") && Text(e, @"Cycling \{2\}"),
            ["bicycleland"] = e => BasicTypeCount(e) == 2 && Text(e, @"This land enters tapped\.") && Text(e, @"Cycling \{2\}"),
            ["tangoland"] = e => BasicTypeCount(e) == 2 && Text(e, @"enters tapped unless you control two or more basic lands"),
            ["battleland"] = e => BasicTypeCount(e) == 2 && Text(e, @"enters tapped unless you control two or more basic lands"),
            ["shadowland"] = e => Text(e, $@"you may reveal an? {Basic} or {Basic} card from your hand\. If you don't, this land enters tapped"),
            ["snarl"] = e => Text(e, $@"you may reveal an? {Basic} or {Basic} card from your hand\. If you don't, this land enters tapped"),
            ["storageland"] = e => Text(e, @"storage counter"),
            ["triland"] = e => BasicTypeCount(e) == 0 && Regex.IsMatch(e.Record.OracleText.Trim(), @"^This land enters tapped\.\n\{T\}: Add \{[WUBRG]\}, \{[WUBRG]\}, or \{[WUBRG]\}\.$"),
            ["triome"] = e => BasicTypeCount(e) == 3,
            ["trikland"] = e => BasicTypeCount(e) == 3,
            ["pathway"] = e => e.Record.Layout == "modal_dfc" && e.Record.Faces.Count == 2 && e.Record.Faces.All(f => f.TypeLine == "Land" && f.Name.EndsWith(" Pathway", StringComparison.Ordinal)),
            ["bondland"] = e => Text(e, @"enters tapped unless you have two or more opponents"),
            ["crowdland"] = e => Text(e, @"enters tapped unless you have two or more opponents"),
        };

        private static bool IsFrenchVanilla(CardRecord record)
        {
            var lines = OracleTextsOf(record).SelectMany(t => t.Split('\n')).Select(l => Regex.Replace(l, @"\s*\(.*?\)", "").Trim()).Where(l => l.Length > 0).ToList();
            return lines.Count > 0 && lines.All(line => line.Split(", ").All(part =>
                record.Keywords.Any(k => part.StartsWith(k, StringComparison.OrdinalIgnoreCase))));
        }

        // ------------------------------------------------------------ printings

        private static readonly string[] Rarities = { "common", "uncommon", "rare", "mythic" };

        private static string RarityName(string value) => value switch
        {
            "c" => "common", "u" => "uncommon", "r" => "rare", "m" => "mythic", "s" => "special", "b" => "bonus", _ => value,
        };

        private static Node RarityTerm(string op, string value)
        {
            string wanted = RarityName(value);
            if (!Rarities.Contains(wanted) && wanted is not ("special" or "bonus")) throw new SearchError($"\"{value}\" isn't a rarity.");
            if (op is ":" or "=") return new Is(c => c.Printing is { } p && p.Rarity.Equals(wanted, StringComparison.OrdinalIgnoreCase), printing: true);
            if (op == "!=") return new Is(c => c.Printing is { } p && !p.Rarity.Equals(wanted, StringComparison.OrdinalIgnoreCase), printing: true);
            int rank = Array.IndexOf(Rarities, wanted);
            if (rank < 0) throw new SearchError($"{wanted} can't be compared.");
            return new Is(c => c.Printing is { } p && Array.IndexOf(Rarities, p.Rarity.ToLowerInvariant()) is var have and >= 0 && Compare(have, op, rank), printing: true);
        }

        private static Node CollectorNumberTerm(string op, string value)
        {
            if (op is ":" or "=") return new Is(c => c.Printing is { } p && p.CollectorNumber.Equals(value, StringComparison.OrdinalIgnoreCase), printing: true);
            if (!int.TryParse(value, out int target)) throw new SearchError($"\"{value}\" isn't a number.");
            return new Is(c => c.Printing is { } p && int.TryParse(new string(p.CollectorNumber.TakeWhile(char.IsDigit).ToArray()), out int n) && Compare(n, op, target), printing: true);
        }

        private static bool SetTypeIs(Printing p, string type) => p.SetType.Equals(type, StringComparison.OrdinalIgnoreCase);

        private static Node InTerm(string value)
        {
            if (value == "paper" || value is "en" or "english") return Node.True;
            if (value is "mtgo" or "arena") return Node.False;
            string rarity = RarityName(value);
            if (Rarities.Contains(rarity) || rarity is "special" or "bonus")
                return new Is(c => c.Entry.Record.Printings.Any(p => p.Rarity.Equals(rarity, StringComparison.OrdinalIgnoreCase)));
            return new Is(c => c.Entry.Record.Printings.Any(p => p.Set.Equals(value, StringComparison.OrdinalIgnoreCase) || SetTypeIs(p, value)));
        }

        private Node DateTerm(string op, string value, bool yearOnly)
        {
            string compare = op == ":" ? "=" : op;
            string lower = value.ToLowerInvariant();
            string date;
            if (lower is "now" or "today") date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            else if (Regex.IsMatch(value, @"^\d{4}$")) date = value;
            else if (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$")) date = value;
            else if (findSet?.Invoke(value) is { Released.Length: > 0 } set) date = set.Released;
            else throw new SearchError($"\"{value}\" isn't a year, a date or a set code.");
            if (yearOnly || date.Length == 4)
            {
                int year = int.Parse(date[..4]);
                return new Is(c => c.Printing is { Released.Length: >= 4 } p && Compare(int.Parse(p.Released[..4]), compare, year), printing: true);
            }
            return new Is(c => c.Printing is { Released.Length: > 0 } p && Compare(string.CompareOrdinal(p.Released, date), compare, 0), printing: true);
        }

        // ------------------------------------------------------------ sorting

        public static readonly string[] Orders = { "name", "cmc", "mv", "power", "toughness", "color", "rarity", "set", "released", "usd", "eur" };
    }

    private sealed class SearchError(string message) : Exception(message);

    // ---------------------------------------------------------------- shared helpers

    internal static bool Has(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    internal static string Unquote(string text) =>
        text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"') ? text[1..^1] : text.Trim('"');

    internal static IEnumerable<string> Names(CardEntry entry)
    {
        yield return entry.Record.Name;
        if (entry.Name != entry.Record.Name) yield return entry.Name;
        foreach (var face in entry.Record.Faces) yield return face.Name;
    }

    private static IEnumerable<string> TypeLines(CardRecord record) =>
        new[] { record.TypeLine }.Concat(record.Faces.Select(f => f.TypeLine)).Where(t => t.Length > 0);

    private static IEnumerable<string> OracleTexts(CardEntry entry) => OracleTextsOf(entry.Record);

    private static IEnumerable<string> OracleTextsOf(CardRecord record) =>
        record.Faces.Count > 0 ? record.Faces.Select(f => f.OracleText).Append(record.OracleText) : new[] { record.OracleText };

    private static string FrontType(CardRecord record) => record.Faces.Count > 0 ? record.Faces[0].TypeLine : record.TypeLine;

    private static HashSet<string> Subtypes(CardRecord record) =>
        TypeLines(record).SelectMany(t => t.Split('—').Skip(1)).SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsPermanent(CardRecord record) =>
        FrontType(record) is var t && new[] { "Artifact", "Creature", "Enchantment", "Land", "Planeswalker", "Battle" }.Any(t.Contains);

    private static bool Compare(double a, string op, double b) => op switch
    {
        "=" or ":" => a == b,
        "!=" => a != b,
        "<" => a < b,
        "<=" => a <= b,
        ">" => a > b,
        ">=" => a >= b,
        _ => false,
    };

    // ---------------------------------------------------------------- the tree

    /// <summary>What a term looks at: the card, and the printing (when the search is about one copy or one printing).</summary>
    /// <param name="Item">What the screen searches, for its own terms (<see cref="SearchTerms"/>).</param>
    internal sealed record Context(CardEntry Entry, Printing? Printing, bool? Foil, object? Item = null);

    internal abstract record Node
    {
        public static readonly Node True = new Is(_ => true);
        public static readonly Node False = new Is(_ => false);
        public abstract bool Eval(Context c);
        public abstract bool UsesPrinting { get; }
    }

    internal sealed record Is(Func<Context, bool> Test, bool printing = false) : Node
    {
        public override bool Eval(Context c) => Test(c);
        public override bool UsesPrinting => printing;
    }

    internal sealed record Not(Node Inner) : Node
    {
        public override bool Eval(Context c) => !Inner.Eval(c);
        public override bool UsesPrinting => Inner.UsesPrinting;
    }

    internal sealed record All(IReadOnlyList<Node> Terms) : Node
    {
        public override bool Eval(Context c) => Terms.All(t => t.Eval(c));
        public override bool UsesPrinting => Terms.Any(t => t.UsesPrinting);
    }

    internal sealed record Any(IReadOnlyList<Node> Options) : Node
    {
        public override bool Eval(Context c) => Options.Any(t => t.Eval(c));
        public override bool UsesPrinting => Options.Any(t => t.UsesPrinting);
    }
}

/// <summary>
/// Number terms a screen adds to the search language (<c>usd&gt;=2</c>, <c>qty&gt;1</c>): each keyword's value for a card,
/// its printing (null when unknown) and the item the screen searches (null: no value, the term finds the card).
/// </summary>
/// <param name="Help">The terms explained, a line for the search box's tooltip.</param>
public sealed record SearchTerms(IReadOnlyDictionary<string, Func<CardEntry, Printing?, object?, double?>> Numbers, string Help);

/// <summary>A search read by <see cref="CardSearch.Parse"/>: which cards it finds, how to sort them and what was ignored.</summary>
public sealed class CardFilter
{
    private readonly CardSearch.Node _root;

    internal CardFilter(CardSearch.Node root, IReadOnlyList<string> warnings, string? order, bool descending)
    {
        _root = root;
        Warnings = warnings;
        Order = order;
        Descending = descending;
    }

    /// <summary>Terms that were ignored, and why.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The order asked for (order:...), or null.</summary>
    public string? Order { get; }

    public bool Descending { get; }

    /// <summary>Whether the card has a printing the search finds (any printing, when the search doesn't ask about printings).</summary>
    public bool Matches(CardEntry entry)
    {
        if (!_root.UsesPrinting || entry.Record.Printings.Count == 0) return _root.Eval(new CardSearch.Context(entry, null, null));
        return entry.Record.Printings.Any(p => _root.Eval(new CardSearch.Context(entry, p, null)));
    }

    /// <summary>Whether the search finds this printing of the card (null: the card's printing isn't known), foil or not when known.</summary>
    public bool Matches(CardEntry entry, Printing? printing, bool? foil = null) =>
        printing is null && foil is null ? Matches(entry) : _root.Eval(new CardSearch.Context(entry, printing, foil));

    /// <summary>Whether a copy of the card (by set code and collector number, foil or not) is found.</summary>
    public bool Matches(CardEntry entry, string set, string number, bool foil) =>
        Matches(entry, set.Length > 0 ? entry.Record.FindPrinting(set, number.Length > 0 ? number : null) : null, foil);

    /// <summary>
    /// Whether the search finds this printing of the card (foil or not, or either when null) and the screen's item, which
    /// the screen's own terms (<see cref="SearchTerms"/>) look at.
    /// </summary>
    public bool Matches(CardEntry entry, Printing? printing, bool? foil, object item) =>
        _root.Eval(new CardSearch.Context(entry, printing, foil, item));

    /// <summary>The lowest price listed among a card's printings (foil or not), in euros or dollars; null when none is.</summary>
    public static double? CheapestPrice(CardEntry entry, bool euros) =>
        entry.Record.Printings.Select(p => euros ? p.Prices.EurFor(null) : p.Prices.UsdFor(null)).Min();

    /// <summary>The cards in the order asked for (as they come when none was).</summary>
    public IEnumerable<CardEntry> Sort(IEnumerable<CardEntry> cards)
    {
        if (Order is null) return Descending ? cards.Reverse() : cards;
        if (Order is "usd" or "eur")
        {
            // By the cheapest printing; cards with no price listed come last either way.
            bool euros = Order == "eur";
            var priced = cards.Select(e => (Entry: e, Price: CheapestPrice(e, euros))).ToList();
            var withPrice = priced.Where(x => x.Price is not null);
            return (Descending ? withPrice.OrderByDescending(x => x.Price) : withPrice.OrderBy(x => x.Price))
                .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .Concat(priced.Where(x => x.Price is null).OrderBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase))
                .Select(x => x.Entry);
        }
        Func<CardEntry, IComparable> key = Order switch
        {
            "cmc" or "mv" => e => e.ManaValue,
            "power" => e => int.TryParse(e.Record.Power, out int p) ? p : -1,
            "toughness" => e => int.TryParse(e.Record.Toughness, out int t) ? t : -1,
            "color" => e => e.Record.Colors.Count == 0 ? 6 : e.Record.Colors.Count > 1 ? 5 : "WUBRG".IndexOf(char.ToUpperInvariant(e.Record.Colors[0][0])),
            "rarity" => e => Array.IndexOf(new[] { "common", "uncommon", "rare", "mythic" }, (e.Record.DefaultPrinting ?? e.Record.Printings.LastOrDefault())?.Rarity ?? ""),
            "set" => e => (e.Record.DefaultPrinting ?? e.Record.Printings.LastOrDefault())?.Set ?? "",
            "released" => e => e.Record.Printings.FirstOrDefault()?.Released ?? "",
            _ => e => e.Name,
        };
        return (Descending ? cards.OrderByDescending(key) : cards.OrderBy(key)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
    }
}
