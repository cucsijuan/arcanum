// SPDX-License-Identifier: AGPL-3.0-or-later
// Writes which cards the engine runs with a content module and card data:
// "<name>\t<support>" per card (support.tsv), and the keywords the card importer understands without a script.
using Arcanum.Data.CardData;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: SupportDump <module dir> <cards.jsonl.gz> <output dir>");
    return 1;
}
var module = Arcanum.Data.Modules.ContentModule.Load(args[0]);
using var file = File.OpenRead(args[1]);
var db = new CardDatabase(OracleJsonl.Import(file, module.Sources.Cards.ToFilter()), module.LoadScripts());
Directory.CreateDirectory(args[2]);
File.WriteAllLines(Path.Combine(args[2], "support.tsv"), db.All.Select(d => $"{d.Name}\t{db.SupportOf(d.Name)}"));
File.WriteAllLines(Path.Combine(args[2], "keywords.txt"), CardFactory.SupportedKeywords.OrderBy(k => k));
return 0;
