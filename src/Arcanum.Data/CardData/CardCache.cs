// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.CardData;

/// <summary>
/// A <see cref="CardDatabase"/> kept on disk ready to use, so a start doesn't read the card data's text again and make
/// every card: the records, the database's lookups exactly as built, each card's name and support, the sets and the
/// card name lists a game asks for. Definitions aren't stored: an entry read back makes its own when first asked.
/// </summary>
/// <remarks>
/// A cache holds for one <see cref="Key"/> (the code that makes cards, the card data file and the module's scripts);
/// with any of them changed it reads as missing and is made again. Text is stored once and as UTF-16, so reading it
/// back is a copy rather than a decode.
/// </remarks>
public static class CardCache
{
    private const uint Signature = 0x42444341; // "ACDB"
    private const int Format = 1;

    private const byte PlainEntry = 0, MeldResultEntry = 1, MelderEntry = 2;

    /// <summary>
    /// What a cache must have been made from to be used: this format, the assemblies that make cards (their build), the
    /// card data file and every script file (name, size, and last change or, where the file system gives every file
    /// the same time, contents).
    /// </summary>
    public static string Key(string cardFile, string scriptsDirectory)
    {
        var key = new StringBuilder().Append(Format);
        foreach (var assembly in new[] { typeof(CardCache).Assembly, typeof(CardDefinition).Assembly })
            key.Append('|').Append(assembly.ManifestModule.ModuleVersionId);
        var card = new FileInfo(cardFile);
        key.Append('|').Append(card.Name).Append(':').Append(card.Length).Append(':').Append(card.LastWriteTimeUtc.Ticks);
        var files = Directory.Exists(scriptsDirectory)
            ? new DirectoryInfo(scriptsDirectory).EnumerateFiles("*.json").OrderBy(f => f.Name, StringComparer.Ordinal).ToList()
            : new List<FileInfo>();
        // A file system that stamps every file with one time (a read-only image stamps them with when it was mounted)
        // says nothing about which changed: there the contents are what tells.
        bool byContents = files.Count > 1 && files.All(f => f.LastWriteTimeUtc == files[0].LastWriteTimeUtc);
        var hash = new Fnv();
        foreach (var file in files)
        {
            hash.Add(file.Name);
            hash.Add(file.Length);
            if (byContents) hash.Add(File.ReadAllBytes(file.FullName));
            else hash.Add(file.LastWriteTimeUtc.Ticks);
        }
        return key.Append('|').Append(files.Count).Append(byContents ? ":contents:" : ":").Append(hash.Value.ToString("x16")).ToString();
    }

    /// <summary>64-bit FNV-1a, eight bytes at a time.</summary>
    private struct Fnv
    {
        private const ulong Prime = 1099511628211UL;

        public Fnv() { }

        public ulong Value { get; private set; } = 14695981039346656037UL;

        public void Add(long value) => Value = (Value ^ (ulong)value) * Prime;

        public void Add(string text) => Add(MemoryMarshal.AsBytes(text.AsSpan()));

        public void Add(ReadOnlySpan<byte> bytes)
        {
            ulong value = Value;
            var words = MemoryMarshal.Cast<byte, ulong>(bytes);
            foreach (ulong word in words) value = (value ^ word) * Prime;
            foreach (byte b in bytes[(words.Length * 8)..]) value = (value ^ b) * Prime;
            Value = (value ^ (ulong)bytes.Length) * Prime;
        }
    }

    // ---------------------------------------------------------------- writing

    /// <summary>Writes <paramref name="cards"/> to <paramref name="path"/> (through a temporary file, so a cut-off write leaves none).</summary>
    public static void Write(CardDatabase cards, string path, string key)
    {
        if (!BitConverter.IsLittleEndian) return; // text is stored in this machine's order; a big-endian one just doesn't cache
        var writer = new Writer();
        writer.Database(cards);
        var temp = path + ".tmp";
        using (var file = File.Create(temp)) writer.CopyTo(file, key);
        SafeFile.Replace(temp, path);
    }

    private sealed class Writer
    {
        private readonly MemoryStream _body = new();
        private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
        private readonly List<string> _stringList = new();
        private readonly Dictionary<string, int> _lists = new(StringComparer.Ordinal);
        private readonly List<int[]> _listList = new();
        private readonly Dictionary<string, int> _maps = new(StringComparer.Ordinal);
        private readonly List<int[]> _mapList = new();

        public void Database(CardDatabase cards)
        {
            var entryIndex = new Dictionary<CardEntry, int>(ReferenceEqualityComparer.Instance);
            var entries = new List<CardEntry>();
            foreach (var entry in cards.ByName.Values)
                if (entryIndex.TryAdd(entry, entries.Count)) entries.Add(entry);
            var recordIndex = new Dictionary<CardRecord, int>(ReferenceEqualityComparer.Instance);
            var records = new List<CardRecord>();
            void AddRecord(CardRecord record)
            {
                if (recordIndex.TryAdd(record, records.Count)) records.Add(record);
            }
            foreach (var entry in entries) AddRecord(entry.Record);
            foreach (var tokens in cards.TokensByName.Values)
                foreach (var token in tokens) AddRecord(token);

            Int(records.Count);
            foreach (var record in records) Record(record);

            Int(entries.Count);
            foreach (var entry in entries)
            {
                Int(recordIndex[entry.Record]);
                String(entry.Name);
                Int((int)entry.Support);
                var definition = entry.Definition;
                if (definition.IsMeldResult) Byte(MeldResultEntry);
                else if (definition.MeldResult is { } result)
                {
                    // The entry of the combined face it melds into (the same definition, or an equal one made from it).
                    int target = entries.FindIndex(e => e.Definition.IsMeldResult && (ReferenceEquals(e.Definition, result) || e.Definition.Equals(result)));
                    if (target < 0) throw new InvalidOperationException($"{entry.Name} melds into a face that isn't in the database.");
                    Byte(MelderEntry);
                    Int(target);
                }
                else Byte(PlainEntry);
            }

            Int(cards.ByName.Count);
            foreach (var (name, entry) in cards.ByName)
            {
                String(name);
                Int(entryIndex[entry]);
            }

            Int(cards.Sorted.Count);
            foreach (var entry in cards.Sorted) Int(entryIndex[entry]);

            Int(cards.TokensByName.Count);
            foreach (var (name, tokens) in cards.TokensByName)
            {
                String(name);
                Int(tokens.Count);
                foreach (var token in tokens) Int(recordIndex[token]);
            }

            Int(cards.Sets.Count);
            foreach (var set in cards.Sets)
            {
                String(set.Code);
                String(set.Name);
                String(set.Released);
                String(set.Type);
                Int(set.CardCount);
            }

            List(cards.NonbasicLandNames);
            List(cards.CreatureCardNames);
        }

        private void Record(CardRecord r)
        {
            String(r.OracleId);
            String(r.Name);
            String(r.Layout);
            String(r.ManaCost);
            String(r.TypeLine);
            String(r.OracleText);
            String(r.Power);
            String(r.Toughness);
            String(r.Loyalty);
            List(r.Colors);
            List(r.ColorIdentity);
            List(r.Keywords);
            List(r.ProducedMana);
            Map(r.Legalities);
            Int(r.Faces.Count);
            foreach (var face in r.Faces)
            {
                String(face.Name);
                String(face.ManaCost);
                String(face.TypeLine);
                String(face.OracleText);
                String(face.Power);
                String(face.Toughness);
                String(face.Loyalty);
                List(face.Colors);
            }
            Int((r.IsToken ? 1 : 0) | (r.Reserved ? 2 : 0) | (r.GameChanger ? 4 : 0));
            Tokens(r.RelatedTokens);
            String(r.DefaultPrintingId);
            Int(r.Printings.Count);
            foreach (var p in r.Printings) Printing(p);
        }

        private void Printing(Printing p)
        {
            String(p.Set);
            String(p.SetName);
            String(p.CollectorNumber);
            String(p.Rarity);
            String(p.Id);
            String(p.Released);
            String(p.SetType);
            Tokens(p.Tokens);
            Int((p.Booster ? 1 : 0) | (p.Foil ? 2 : 0) | (p.FoilOnly ? 4 : 0) | (p.Fracture ? 8 : 0) | (p.Etched ? 16 : 0)
                | (p.Glossy ? 32 : 0) | (p.FullArt ? 64 : 0) | (p.Textless ? 128 : 0) | (p.HighRes ? 256 : 0));
            String(p.Illustration);
            var prices = p.Prices;
            Byte((byte)((prices.Eur.HasValue ? 1 : 0) | (prices.EurFoil.HasValue ? 2 : 0) | (prices.Usd.HasValue ? 4 : 0) | (prices.UsdFoil.HasValue ? 8 : 0)));
            foreach (var price in new[] { prices.Eur, prices.EurFoil, prices.Usd, prices.UsdFoil })
                if (price is { } value) Double(value);
            String(p.Border);
            String(p.Frame);
            List(p.FrameEffects);
            String(p.PrintedName);
            String(p.FlavorName);
            List(p.PromoTypes);
        }

        private void Tokens(IReadOnlyList<RelatedToken> tokens)
        {
            Int(tokens.Count);
            foreach (var token in tokens)
            {
                String(token.Name);
                String(token.Id);
                String(token.TypeLine);
            }
        }

        private int Intern(string? text)
        {
            if (text is null) return 0;
            if (!_strings.TryGetValue(text, out int index))
            {
                _stringList.Add(text);
                _strings[text] = index = _stringList.Count; // 0 is null
            }
            return index;
        }

        private void String(string? text) => Int(Intern(text));

        private void List(IReadOnlyList<string> items)
        {
            var indexes = items.Select(Intern).ToArray();
            var key = string.Join(",", indexes) + ";" + indexes.Length;
            if (!_lists.TryGetValue(key, out int index))
            {
                _lists[key] = index = _listList.Count;
                _listList.Add(indexes);
            }
            Int(index);
        }

        private void Map(IReadOnlyDictionary<string, string> map)
        {
            var indexes = map.SelectMany(pair => new[] { Intern(pair.Key), Intern(pair.Value) }).ToArray();
            var key = string.Join(",", indexes);
            if (!_maps.TryGetValue(key, out int index))
            {
                _maps[key] = index = _mapList.Count;
                _mapList.Add(indexes);
            }
            Int(index);
        }

        private void Byte(byte value) => _body.WriteByte(value);

        private void Int(int value) => Varint(_body, (uint)value);

        private void Double(double value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
            _body.Write(bytes);
        }

        private static void Varint(Stream stream, uint value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            stream.WriteByte((byte)value);
        }

        public void CopyTo(Stream output, string key)
        {
            var head = new MemoryStream();
            Span<byte> signature = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(signature, Signature);
            head.Write(signature);
            Varint(head, Format);
            var keyBytes = Encoding.UTF8.GetBytes(key);
            Varint(head, (uint)keyBytes.Length);
            head.Write(keyBytes);
            Varint(head, (uint)_stringList.Count);
            foreach (var text in _stringList)
            {
                Varint(head, (uint)text.Length);
                head.Write(MemoryMarshal.AsBytes(text.AsSpan()));
            }
            foreach (var table in new[] { _listList, _mapList })
            {
                Varint(head, (uint)table.Count);
                foreach (var indexes in table)
                {
                    Varint(head, (uint)indexes.Length);
                    foreach (int index in indexes) Varint(head, (uint)index);
                }
            }
            head.Position = 0;
            head.CopyTo(output);
            _body.Position = 0;
            _body.CopyTo(output);
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>The key the cache at <paramref name="path"/> was made for (null: no cache, or not one this format reads).</summary>
    public static string? StoredKey(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            var head = new byte[Math.Min(file.Length, 4096)];
            file.ReadExactly(head);
            var reader = new Reader(head);
            return reader.Key();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// The database cached at <paramref name="path"/>, or null when there is none, it was made for another key or it
    /// can't be read. <paramref name="scriptFor"/> gives a card's script by oracle id when its definition is made.
    /// </summary>
    public static CardDatabase? Read(string path, string key, Func<string, Scripts.CardScript?> scriptFor)
    {
        if (!BitConverter.IsLittleEndian || !File.Exists(path)) return null;
        try
        {
            var reader = new Reader(File.ReadAllBytes(path));
            return reader.Header(key) ? reader.Database(scriptFor) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _data;
        private int _at;
        private string[] _strings = Array.Empty<string>();
        private IReadOnlyList<string>[] _lists = Array.Empty<IReadOnlyList<string>>();
        private IReadOnlyDictionary<string, string>[] _maps = Array.Empty<IReadOnlyDictionary<string, string>>();

        public Reader(byte[] data) => _data = data;

        public string? Key()
        {
            if (_data.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(_data) != Signature) return null;
            _at = 4;
            if (Int() != Format) return null;
            int keyLength = Int();
            var key = Encoding.UTF8.GetString(_data, _at, keyLength);
            _at += keyLength;
            return key;
        }

        public bool Header(string key)
        {
            if (Key() != key) return false;

            _strings = new string[Int() + 1];
            for (int i = 1; i < _strings.Length; i++)
            {
                int length = Int();
                _strings[i] = new string(MemoryMarshal.Cast<byte, char>(_data.AsSpan(_at, length * 2)));
                _at += length * 2;
            }
            _lists = new IReadOnlyList<string>[Int()];
            for (int i = 0; i < _lists.Length; i++)
            {
                var items = new string[Int()];
                for (int j = 0; j < items.Length; j++) items[j] = _strings[Int()];
                _lists[i] = items.Length == 0 ? Array.Empty<string>() : items;
            }
            _maps = new IReadOnlyDictionary<string, string>[Int()];
            for (int i = 0; i < _maps.Length; i++)
            {
                int pairs = Int() / 2;
                var map = new Dictionary<string, string>(pairs);
                for (int j = 0; j < pairs; j++) map[_strings[Int()]] = _strings[Int()];
                _maps[i] = map;
            }
            return true;
        }

        public CardDatabase Database(Func<string, Scripts.CardScript?> scriptFor)
        {
            var records = new CardRecord[Int()];
            for (int i = 0; i < records.Length; i++) records[i] = Record();

            var entries = new CardEntry[Int()];
            for (int i = 0; i < entries.Length; i++)
            {
                var record = records[Int()];
                string name = String()!;
                var support = (CardSupport)Int();
                CardDefinition Make() => CardFactory.Create(record, scriptFor(record.OracleId)).Definition;
                Func<CardDefinition> make;
                switch (Byte())
                {
                    case PlainEntry:
                        make = Make;
                        break;
                    case MeldResultEntry:
                        make = () => Make() with { IsMeldResult = true };
                        break;
                    case MelderEntry:
                        int target = Int();
                        make = () => Make() with { MeldResult = entries[target].Definition };
                        break;
                    default:
                        throw new FormatException("Unknown card entry kind.");
                }
                entries[i] = new CardEntry(name, make, support, record);
            }

            int names = Int();
            var byName = new Dictionary<string, CardEntry>(names, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names; i++)
            {
                string name = String()!;
                byName[name] = entries[Int()];
            }

            var sorted = new List<CardEntry>(Int());
            for (int i = sorted.Capacity; i > 0; i--) sorted.Add(entries[Int()]);

            int tokenNames = Int();
            var tokensByName = new Dictionary<string, List<CardRecord>>(tokenNames, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tokenNames; i++)
            {
                string name = String()!;
                var tokens = new List<CardRecord>(Int());
                for (int j = tokens.Capacity; j > 0; j--) tokens.Add(records[Int()]);
                tokensByName[name] = tokens;
            }

            var sets = new SetInfo[Int()];
            for (int i = 0; i < sets.Length; i++) sets[i] = new SetInfo(String()!, String()!, String()!, String()!, Int());

            var nonbasicLandNames = List();
            var creatureCardNames = List();
            if (_at != _data.Length) throw new FormatException("Trailing data in the card cache.");
            return new CardDatabase(byName, sorted, tokensByName, sets, nonbasicLandNames, creatureCardNames);
        }

        private CardRecord Record()
        {
            string oracleId = String()!, name = String()!, layout = String()!, manaCost = String()!, typeLine = String()!, oracleText = String()!;
            string? power = String(), toughness = String(), loyalty = String();
            IReadOnlyList<string> colors = List(), colorIdentity = List(), keywords = List(), producedMana = List();
            var legalities = _maps[Int()];
            var faces = new CardFaceRecord[Int()];
            for (int i = 0; i < faces.Length; i++)
            {
                faces[i] = new CardFaceRecord(String()!, String()!, String()!, String()!, String(), String())
                {
                    Loyalty = String(),
                    Colors = List(),
                };
            }
            int flags = Int();
            var relatedTokens = Tokens();
            string? defaultPrintingId = String();
            var printings = new Printing[Int()];
            for (int i = 0; i < printings.Length; i++) printings[i] = Printing();
            return new CardRecord
            {
                OracleId = oracleId,
                Name = name,
                Layout = layout,
                ManaCost = manaCost,
                TypeLine = typeLine,
                OracleText = oracleText,
                Power = power,
                Toughness = toughness,
                Loyalty = loyalty,
                Colors = colors,
                ColorIdentity = colorIdentity,
                Keywords = keywords,
                ProducedMana = producedMana,
                Legalities = legalities,
                Faces = faces.Length == 0 ? Array.Empty<CardFaceRecord>() : faces,
                IsToken = (flags & 1) != 0,
                Reserved = (flags & 2) != 0,
                GameChanger = (flags & 4) != 0,
                RelatedTokens = relatedTokens,
                DefaultPrintingId = defaultPrintingId,
                Printings = printings.Length == 0 ? Array.Empty<Printing>() : printings,
            };
        }

        private Printing Printing()
        {
            string set = String()!, setName = String()!, number = String()!, rarity = String()!, id = String()!, released = String()!, setType = String()!;
            var tokens = Tokens();
            int flags = Int();
            string? illustration = String();
            int present = Byte();
            double? Price(int bit) => (present & bit) != 0 ? Double() : null;
            double? eur = Price(1), eurFoil = Price(2), usd = Price(4), usdFoil = Price(8);
            return new Printing(set, setName, number, rarity, id, released, setType, (flags & 1) != 0, tokens, (flags & 2) != 0)
            {
                FoilOnly = (flags & 4) != 0,
                Fracture = (flags & 8) != 0,
                Etched = (flags & 16) != 0,
                Glossy = (flags & 32) != 0,
                FullArt = (flags & 64) != 0,
                Textless = (flags & 128) != 0,
                HighRes = (flags & 256) != 0,
                Illustration = illustration,
                Prices = present == 0 ? PrintingPrices.None : new PrintingPrices(eur, eurFoil, usd, usdFoil),
                Border = String()!,
                Frame = String()!,
                FrameEffects = List(),
                PrintedName = String(),
                FlavorName = String(),
                PromoTypes = List(),
            };
        }

        private IReadOnlyList<RelatedToken> Tokens()
        {
            int count = Int();
            if (count == 0) return Array.Empty<RelatedToken>();
            var tokens = new RelatedToken[count];
            for (int i = 0; i < count; i++) tokens[i] = new RelatedToken(String()!, String()!, String()!);
            return tokens;
        }

        private string? String() => _strings[Int()];

        private IReadOnlyList<string> List() => _lists[Int()];

        private byte Byte() => _data[_at++];

        private double Double()
        {
            double value = BinaryPrimitives.ReadDoubleLittleEndian(_data.AsSpan(_at, 8));
            _at += 8;
            return value;
        }

        private int Int()
        {
            uint value = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = _data[_at++];
                value |= (uint)(b & 0x7F) << shift;
                if (b < 0x80) return (int)value;
                if (shift > 28) throw new FormatException("Bad number in the card cache.");
            }
        }
    }
}
