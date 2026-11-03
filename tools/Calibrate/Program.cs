// Scores every sample in a JSONL corpus with Jev and shows how each threshold would perform.
//
//   dotnet run --project tools/Calibrate -- corpus/samples.jsonl
//
// API key: the TYPESAFE_API_KEY environment variable, or the key saved in the app's Settings.
// Writes calibration-results.md next to the corpus file.

using System.Text;
using System.Text.Json;
using PasteGuard;

var corpusPath = args.Length > 0 ? args[0] : Path.Combine("corpus", "samples.jsonl");
if (!File.Exists(corpusPath))
{
    Console.Error.WriteLine($"Corpus not found: {corpusPath}");
    return 1;
}

var settings = Settings.Load();
var apiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey)) apiKey = settings.ApiKey;
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("No API key. Set TYPESAFE_API_KEY or save a key in PasteGuard's Settings first.");
    return 1;
}

using var jev = new JevClient(settings.ApiBaseUrl);
var rows = new List<Row>();

foreach (var (line, index) in File.ReadLines(corpusPath).Select((l, i) => (l, i + 1)))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var sample = JsonSerializer.Deserialize<Sample>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                 ?? throw new InvalidDataException($"Line {index} is not valid JSON.");
    var malicious = sample.Label.Equals("malicious", StringComparison.OrdinalIgnoreCase);
    var gated = PreFilter.Check(sample.Text).IsCommandLike;

    int? score = null;
    string? error = null;
    if (gated)
    {
        try { score = await jev.ScoreAsync(apiKey, settings.Model, sample.Text); }
        catch (JevException ex) { error = ex.Message; }
        await Task.Delay(150);
    }

    var row = new Row(index, malicious, gated, score, error, sample.Note ?? "", sample.Text);
    rows.Add(row);
    Console.WriteLine($"{(malicious ? "MAL" : "ben"),-4} {(gated ? (score?.ToString() ?? "ERR") : "-"),4}  {row.Note}");
}

var scored = rows.Where(r => r.Error is null).ToList();
var malTotal = scored.Count(r => r.Malicious);
var benTotal = scored.Count(r => !r.Malicious);

var md = new StringBuilder();
md.AppendLine($"# Calibration results ({DateTime.Now:yyyy-MM-dd}, model `{settings.Model}`)");
md.AppendLine();
md.AppendLine($"{malTotal} malicious and {benTotal} benign samples. Text the pre-filter doesn't pass counts as score 0 (never sent to Jev).");
md.AppendLine();
md.AppendLine("| Threshold | Attacks blocked | Benign blocked (false alarms) |");
md.AppendLine("|---:|---:|---:|");

int? recommended = null;
for (var t = 30; t <= 95; t += 5)
{
    var tp = scored.Count(r => r.Malicious && r.Effective >= t);
    var fp = scored.Count(r => !r.Malicious && r.Effective >= t);
    md.AppendLine($"| {t} | {tp}/{malTotal} | {fp}/{benTotal} |");
    if (recommended is null && fp == 0) recommended = t;
}

md.AppendLine();
md.AppendLine(recommended is null
    ? "No threshold from 30 to 95 avoids every false alarm. Look at the benign samples with high scores below."
    : $"Lowest threshold with no false alarms: **{recommended}**.");
md.AppendLine();
md.AppendLine("## Per-sample scores");
md.AppendLine();
md.AppendLine("| # | Label | Pre-filter | Score | Note |");
md.AppendLine("|---:|---|---|---:|---|");
foreach (var r in rows.OrderByDescending(r => r.Effective))
{
    md.AppendLine($"| {r.Line} | {(r.Malicious ? "malicious" : "benign")} | {(r.Gated ? "sent" : "skipped")} | {(r.Error is null ? r.Effective.ToString() : "error")} | {r.Note} |");
}

var errors = rows.Where(r => r.Error is not null).ToList();
if (errors.Count > 0)
{
    md.AppendLine();
    md.AppendLine($"{errors.Count} samples failed: {errors[0].Error}");
}

var outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(corpusPath))!, "calibration-results.md");
File.WriteAllText(outPath, md.ToString());
Console.WriteLine();
Console.WriteLine(recommended is null ? "No zero-false-alarm threshold found." : $"Lowest threshold with no false alarms: {recommended}");
Console.WriteLine($"Full results: {outPath}");
return 0;

internal sealed record Sample(string Label, string Text, string? Note);

internal sealed record Row(int Line, bool Malicious, bool Gated, int? Score, string? Error, string Note, string Text)
{
    public int Effective => Gated ? Score ?? 0 : 0;
}
