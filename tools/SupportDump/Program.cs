// SPDX-License-Identifier: AGPL-3.0-or-later
// Writes which cards the engine runs with a content module and card data:
// "<name>\t<support>" per card (support.tsv), why each unsupported card isn't supported (why.tsv), the keywords the card importer understands without a script (keywords.txt)
// and the scripts that failed to load (script-errors.txt).
using Arcanum.Data.CardData;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: SupportDump <module dir> <cards.jsonl.gz> <output dir>");
    return 1;
}
var module = Arcanum.Data.Modules.ContentModule.Load(args[0]);
using var file = File.OpenRead(args[1]);
var scriptErrors = new List<string>();
var records = OracleJsonl.Import(file, module.Sources.Cards.ToFilter()).ToList();
var scripts = module.LoadScripts(scriptErrors);
var db = new CardDatabase(records, scripts);
Directory.CreateDirectory(args[2]);
File.WriteAllLines(Path.Combine(args[2], "support.tsv"), db.All.Select(d => $"{d.Name}\t{db.SupportOf(d.Name)}"));
File.WriteAllLines(Path.Combine(args[2], "keywords.txt"), CardFactory.SupportedKeywords.OrderBy(k => k));
File.WriteAllLines(Path.Combine(args[2], "script-errors.txt"), scriptErrors.Order());
// Why each unsupported card isn't supported: "<name>\t<reason>" per failed requirement (why.tsv).
File.WriteAllLines(Path.Combine(args[2], "why.tsv"), records.Where(r => db.SupportOf(r.Name) != CardSupport.Full)
    .SelectMany(r => CardFactory.Explain(r, scripts.GetValueOrDefault(r.OracleId)).Select(reason => $"{r.Name}\t{reason}")));
return 0;
