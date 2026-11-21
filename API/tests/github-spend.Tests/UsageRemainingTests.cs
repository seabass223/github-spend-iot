using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GitHubSpend.Tests;

public sealed class UsageRemainingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Theory]
    [InlineData(0, 100)]
    [InlineData(41.2, 59)]
    [InlineData(100, 0)]
    [InlineData(130, 0)]
    public void FromUsedPercentInvertsAndClamps(double used, int expected) =>
        Assert.Equal(expected, UsageRemaining.FromUsedPercent(used, Now.ToUnixTimeSeconds() + 60, Now));

    [Fact]
    public void WindowPastItsResetCountsAsUnused() =>
        Assert.Equal(100, UsageRemaining.FromUsedPercent(90, Now.ToUnixTimeSeconds() - 1, Now));

    [Theory]
    [InlineData(0, 300, 100)]
    [InlineData(81, 300, 73)]
    [InlineData(300, 300, 0)]
    [InlineData(450, 300, 0)]
    public void FromQuantityGivesPercentLeft(int used, int limit, int expected) =>
        Assert.Equal(expected, UsageRemaining.FromQuantity(used, limit));

    [Fact]
    public async Task ElevenLabsUsesCharacterCountAgainstLimit()
    {
        using var http = new HttpClient(new JsonHandler("""{"character_count": 88000, "character_limit": 100000}"""));
        Assert.Equal(12, await UsageRemaining.GetElevenLabsAsync(http, "key"));
    }

    [Fact]
    public async Task GitHubPersonalSumsAiCreditQuantities()
    {
        var handler = new JsonHandler("""{"usageItems": [{"grossQuantity": 50000}, {"grossQuantity": 31000}]}""");
        using var http = new HttpClient(handler);

        var left = await UsageRemaining.GetGitHubPersonalAsync(http, "token", "octo cat", 300000m, new DateTime(2026, 11, 12));

        Assert.Equal(73, left);
        Assert.Equal("/users/octo%20cat/settings/billing/ai_credit/usage", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("?year=2026&month=11", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task GitHubPersonalWithNoUsageIsFull()
    {
        using var http = new HttpClient(new JsonHandler("{}", HttpStatusCode.NotFound));
        Assert.Equal(100, await UsageRemaining.GetGitHubPersonalAsync(http, "t", "u", 300m, new DateTime(2026, 11, 12)));
    }

    [Fact]
    public void ClaudeReadsSevenDayWindowFromStatusLineFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            var t = Now.ToUnixTimeSeconds();
            File.WriteAllText(path,
                "{\"rate_limits\": {" +
                $"\"five_hour\": {{\"used_percentage\": 90, \"resets_at\": {t + 60}}}, " +
                $"\"seven_day\": {{\"used_percentage\": 17, \"resets_at\": {t + 3600}}}" +
                "}}");
            Assert.Equal(83, UsageRemaining.GetClaudeFromFile(path, Now));
            Assert.Equal(10, UsageRemaining.GetClaudeFromFile(path, Now, UsageWindow.FiveHour));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Real `claude -p /usage` text, with the per-model line moved first to prove it isn't picked.
    private const string ClaudeUsageText =
        "You are currently using your subscription to power your Claude Code usage\n\n" +
        "Current session: 1% used · resets Nov 13, 2:10pm (America/Indiana/Indianapolis)\n" +
        "Current week (Fable): 36% used · resets Nov 15, 1pm (America/Indiana/Indianapolis)\n" +
        "Current week (all models): 40% used · resets Nov 15, 1pm (America/Indiana/Indianapolis)\n";

    private static string ClaudePrintJson(string result, bool isError = false) =>
        JsonSerializer.Serialize(new { type = "result", is_error = isError, result });

    [Fact]
    public void ClaudeUsageReadsWeeklyAllModelsLine() =>
        Assert.Equal(60, UsageRemaining.ParseClaudeUsage(ClaudePrintJson(ClaudeUsageText)));

    [Fact]
    public void ClaudeUsageReadsTheSessionLineForFiveHourLimits() =>
        Assert.Equal(99, UsageRemaining.ParseClaudeUsage(ClaudePrintJson(ClaudeUsageText), UsageWindow.FiveHour));

    [Fact]
    public void ClaudeUsageWithoutASessionLineThrowsForFiveHour() =>
        Assert.Throws<InvalidOperationException>(() => UsageRemaining.ParseClaudeUsage(
            ClaudePrintJson("Current week (all models): 40% used · resets Nov 15\n"), UsageWindow.FiveHour));

    // The shape `codex app-server` returns for account/rateLimits/read (values from a real reply).
    private static System.Text.Json.Nodes.JsonNode CodexLimits(long t, bool withDurations = true) =>
        System.Text.Json.Nodes.JsonNode.Parse(
            "{\"primary\": {\"usedPercent\": 6" + (withDurations ? ", \"windowDurationMins\": 300" : "") + $", \"resetsAt\": {t + 600}}}, " +
            "\"secondary\": {\"usedPercent\": 43" + (withDurations ? ", \"windowDurationMins\": 10080" : "") + $", \"resetsAt\": {t + 86400}}}}}")!;

    [Fact]
    public void CodexPicksTheWindowByItsLength()
    {
        var limits = CodexLimits(Now.ToUnixTimeSeconds());
        Assert.Equal(57, UsageRemaining.FromCodexRateLimits(limits, UsageWindow.Weekly, Now));
        Assert.Equal(94, UsageRemaining.FromCodexRateLimits(limits, UsageWindow.FiveHour, Now));
    }

    [Fact]
    public void CodexFallsBackToPrimaryAndSecondaryWhenLengthsAreMissing()
    {
        var limits = CodexLimits(Now.ToUnixTimeSeconds(), withDurations: false);
        Assert.Equal(57, UsageRemaining.FromCodexRateLimits(limits, UsageWindow.Weekly, Now));
        Assert.Equal(94, UsageRemaining.FromCodexRateLimits(limits, UsageWindow.FiveHour, Now));
    }

    [Fact]
    public void CodexFiveHourWindowPastItsResetCountsAsUnused()
    {
        var limits = CodexLimits(Now.ToUnixTimeSeconds() - 3600); // primary reset 50 minutes ago
        Assert.Equal(100, UsageRemaining.FromCodexRateLimits(limits, UsageWindow.FiveHour, Now));
    }

    [Fact]
    public void ClaudeUsageAcceptsPlainWeeklyLabel() =>
        Assert.Equal(87, UsageRemaining.ParseClaudeUsage(ClaudePrintJson("Current session: 3% used\nCurrent week: 12.6% used · resets Nov 15\n")));

    [Fact]
    public void ClaudeUsageWithoutWeeklyLimitThrows() =>
        Assert.Throws<InvalidOperationException>(() =>
            UsageRemaining.ParseClaudeUsage(ClaudePrintJson("You are using an API key, so plan limits don't apply.\n")));

    [Fact]
    public void ClaudeUsageErrorThrows() =>
        Assert.Throws<InvalidOperationException>(() =>
            UsageRemaining.ParseClaudeUsage(ClaudePrintJson("Not logged in · Please run /login", isError: true)));

    [Fact]
    public void ClaudeWithoutFileIsUnavailable() =>
        Assert.Null(UsageRemaining.GetClaudeFromFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), Now));

    [Fact]
    public void DisplayFormatListsSelectedMetersOnlyWithMinusOneForMissing() =>
        Assert.Equal("WorkClaudeCode=83 ChatGpt=-1 ElevenLabs=12", UsageRemaining.FormatForDisplay(
            new Dictionary<UsageSource, int?>
            {
                [UsageSource.ElevenLabs] = 12,
                [UsageSource.WorkClaudeCode] = 83,
                [UsageSource.ChatGpt] = null,
            }));

    private sealed class JsonHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
