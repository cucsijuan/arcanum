// SPDX-License-Identifier: AGPL-3.0-or-later
// Checks a career season file before it is published: it reads, everything it names exists in the content module, and
// (given the published file) it changes no day that has already been played.
using Arcanum.Data.Career;
using Arcanum.Data.Modules;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: SeasonCheck <module dir> <season.json> [published season.json]");
    return 2;
}
var module = ContentModule.Load(args[0]);
var content = module.LoadCareer() ?? throw new InvalidOperationException("The module has no career.");
CareerSeason season;
try { season = CareerSeason.Parse(File.ReadAllText(args[1])); }
catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"{args[1]}: {e.Message}");
    return 1;
}
var problems = season.Problems(content, module.LoadSets().ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase),
    module.DeckNames().ToHashSet(StringComparer.OrdinalIgnoreCase),
    module.LoadFormats().Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToList();
if (args.Length > 2 && File.Exists(args[2]) && new FileInfo(args[2]).Length > 0)
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    if (CareerSeason.Parse(File.ReadAllText(args[2])).ChangedPast(season, today) is { } past) problems.Add(past);
}
foreach (var problem in problems) Console.Error.WriteLine(problem);
if (problems.Count > 0) return 1;
Console.WriteLine($"{args[1]}: {season.Changes.Count} changes, fine.");
return 0;
