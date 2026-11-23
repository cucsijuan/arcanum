// SPDX-License-Identifier: AGPL-3.0-or-later
// Makes a module's card data in the game's compact form, as a device would after downloading the source's files, so
// a module release brings it already made (OracleJsonl.PreparedFileName at the root of the module's zip).
//
//   dotnet run --project tools/PrepareCardData -- <module folder> <output folder>
//
// Prints the path of the file it wrote (OracleJsonl.PreparedFileName).
using Arcanum.Data.CardData;
using Arcanum.Data.Modules;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: PrepareCardData <module folder> <output folder>");
    return 2;
}
var module = ContentModule.Load(args[0]);
var sources = module.Sources;
Directory.CreateDirectory(args[1]);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd(sources.UserAgent);
http.DefaultRequestHeaders.Accept.ParseAdd("application/json;q=0.9,*/*;q=0.8");

async Task<string> Download(string index, string field)
{
    using var doc = JsonDocument.Parse(await http.GetStringAsync(index));
    var url = doc.RootElement.GetProperty(field).GetString() ?? throw new InvalidDataException($"{index} has no {field}");
    var file = Path.GetTempFileName();
    Console.Error.WriteLine($"Downloading {url}");
    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    await using (var output = File.Create(file)) await response.Content.CopyToAsync(output);
    return file;
}

var cardsFile = await Download(sources.Cards.Index, sources.Cards.DownloadField);
var printingsFile = sources.Printings is { } printings ? await Download(printings.Index, printings.DownloadField) : null;
var path = Path.Combine(args[1], OracleJsonl.PreparedFileName);
try
{
    using var cards = File.OpenRead(cardsFile);
    using var printingsInput = printingsFile is null ? null : File.OpenRead(printingsFile);
    // Through the compact form, as a device makes it, so the records are exactly the ones it would read.
    var compact = new MemoryStream();
    OracleJsonl.Prepare(cards, printingsInput, sources.Cards.ToFilter(), compact);
    compact.Position = 0;
    var records = OracleJsonl.ReadCompact(compact);
    using var output = File.Create(path);
    CardCache.WriteRecords(records, output);
}
finally
{
    File.Delete(cardsFile);
    if (printingsFile is not null) File.Delete(printingsFile);
}
Console.WriteLine(path);
return 0;
