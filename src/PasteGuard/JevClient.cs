using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PasteGuard;

public sealed class JevException : Exception
{
    public int? StatusCode { get; }

    public JevException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;
}

/// <summary>
/// Calls the Jev System One API (https://docs.typesafe.ai) with one yes/no ("noul") question
/// and turns the answer probability into a 0–100 risk score.
/// </summary>
public sealed class JevClient : IDisposable
{
    public const string QuestionName = "clickfix";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    internal const string Instructions =
        "The state is text a user just copied to their Windows clipboard. Decide whether it is a ClickFix-style attack: " +
        "a malicious command that a web page, fake CAPTCHA, fake browser or Windows error, or fake 'verify you are human' step " +
        "told the user to paste into the Run dialog (Win+R), a terminal, or PowerShell. Typical signs: downloading and running " +
        "remote code (iex/irm/iwr, DownloadString, curl or wget piped into a shell, mshta or msiexec with a URL), hidden windows, " +
        "encoded or obfuscated commands, abuse of rundll32, regsvr32, certutil, bitsadmin, finger or conhost, payloads from raw IP " +
        "addresses or odd domains, and a trailing comment pretending to be a CAPTCHA or verification code. Ordinary admin, developer, " +
        "or software-install commands from well-known official sources are not attacks.";

    internal const string CriteriaTrue = "A malicious ClickFix-style command meant to compromise the computer.";
    internal const string CriteriaFalse = "Ordinary text or a legitimate command.";

    private static readonly int[] RetryDelaysMs = { 0, 500, 1500 };
    private readonly HttpClient _http;

    public JevClient(string baseUrl = "https://api.typesafe.ai", HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeout below
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PasteGuard/1.0 (+https://github.com/Ryankapa/PasteGuard)");
    }

    /// <summary>Returns a 0–100 score: how likely Jev thinks the text is a ClickFix attack.</summary>
    public async Task<int> ScoreAsync(string apiKey, string model, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new JevException("No Jev API key is set.");

        var body = BuildRequestJson(model, TextPrep.ForAnalysis(text));

        for (var attempt = 0; ; attempt++)
        {
            var lastAttempt = attempt == RetryDelaysMs.Length - 1;
            if (RetryDelaysMs[attempt] > 0) await Task.Delay(RetryDelaysMs[attempt], ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
            {
                Content = new StringContent(body, new UTF8Encoding(false), "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new JevException("Jev did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                if (!lastAttempt) continue;
                throw new JevException("Could not reach the Jev API (check your internet connection).", null, ex);
            }

            using (response)
            {
                var json = await response.Content.ReadAsStringAsync(timeout.Token);
                var code = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) return ParseScore(json);
                if ((code == 429 || code == 529 || code >= 500) && !lastAttempt) continue;
                throw new JevException(DescribeError(code), code);
            }
        }
    }

    internal static string BuildRequestJson(string model, string text)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["state"] = text,
            ["questions"] = new Dictionary<string, object>
            {
                [QuestionName] = new Dictionary<string, object>
                {
                    ["type"] = "noul",
                    ["instructions"] = Instructions,
                    ["criteria"] = new Dictionary<string, string>
                    {
                        ["true"] = CriteriaTrue,
                        ["false"] = CriteriaFalse,
                    },
                },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    internal static int ParseScore(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("answers", out var answers) &&
                answers.TryGetProperty(QuestionName, out var answer) &&
                answer.TryGetProperty("noul", out var noul) &&
                noul.ValueKind == JsonValueKind.Number)
            {
                return ToScore(noul.GetDouble());
            }
        }
        catch (JsonException) { }
        throw new JevException("Jev sent back a response this version doesn't understand.");
    }

    /// <summary>Maps Jev's yes-probability (0–1) to a 0–100 score.</summary>
    public static int ToScore(double probability) =>
        (int)Math.Round(Math.Clamp(probability, 0, 1) * 100, MidpointRounding.AwayFromZero);

    private static string DescribeError(int code) => code switch
    {
        401 => "Jev rejected the API key (401). Check the key in Settings.",
        403 => "This API key isn't allowed to use Jev (403).",
        429 => "Jev rate limit reached (429). Try again in a moment.",
        529 => "Jev is temporarily overloaded (529).",
        400 or 422 => $"Jev rejected the request ({code}). The model name in config.json may be wrong.",
        _ => $"Jev returned an error ({code}).",
    };

    public void Dispose() => _http.Dispose();
}

internal static class TextPrep
{
    private static readonly Regex LongWhitespace = new(@"[ \t  -​　]{4,}", RegexOptions.Compiled);

    /// <summary>Squashes padding attackers use to push a fake comment out of view; trims ends.</summary>
    public static string ForAnalysis(string text) => LongWhitespace.Replace(text, "    ").Trim();

    /// <summary>Short single-line preview for logs and notifications.</summary>
    public static string Preview(string text, int max)
    {
        var oneLine = Regex.Replace(text, @"\s+", " ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
