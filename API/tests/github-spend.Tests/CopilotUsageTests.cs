using System.Net;
using System.Text.Json;
using Xunit;

namespace GitHubSpend.Tests;

public sealed class CopilotUsageTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(250, 25)]
    [InlineData(1000, 100)]
    [InlineData(1250, 125)]
    public async Task BudgetPercentageUsesThousandDollarBudget(int grossAmount, int expectedPercent)
    {
        var handler = new UsageHandler(grossAmount / 2m, grossAmount / 2m);
        using var http = CreateClient(handler);

        var percent = await CopilotUsage.GetBudgetPercentUsedAsync(http, new(), "test org", null, 2026, 10, 1000m);

        Assert.Equal((decimal)expectedPercent, percent);
    }

    [Fact]
    public async Task BudgetPercentageUsesGrossAmountsAndRetainsFractionalPrecision()
    {
        var handler = new UsageHandler(125.25m, 50.10m);
        using var http = CreateClient(handler);

        var percent = await CopilotUsage.GetBudgetPercentUsedAsync(http, new(), "test org", null, 2026, 10, 1000m);

        Assert.Equal(17.535m, percent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("octo cat")]
    public async Task BudgetPercentageQueriesBothMetersUsingTheSelectedScope(string? user)
    {
        var handler = new UsageHandler(50m, 150m);
        using var http = CreateClient(handler);

        var percent = await CopilotUsage.GetBudgetPercentUsedAsync(http, new(), "test org", user, 2026, 10, 1000m);

        Assert.Equal(20m, percent);
        var scopeQuery = user is null ? "" : $"&user={Uri.EscapeDataString(user)}";
        Assert.Collection(handler.RequestUrls,
            url => Assert.Equal($"https://api.github.com/organizations/test%20org/settings/billing/premium_request/usage?year=2026&month=10{scopeQuery}", url),
            url => Assert.Equal($"https://api.github.com/organizations/test%20org/settings/billing/ai_credit/usage?year=2026&month=10{scopeQuery}", url));
    }

    [Fact]
    public async Task MissingMeterDoesNotAddToBudgetBurn()
    {
        var handler = new UsageHandler(50m, 500m, HttpStatusCode.NotFound);
        using var http = CreateClient(handler);

        var percent = await CopilotUsage.GetBudgetPercentUsedAsync(http, new(), "test org", null, 2026, 10, 1000m);

        Assert.Equal(5m, percent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task BillingFailuresAreSurfaced(HttpStatusCode status)
    {
        var handler = new UsageHandler(50m, 150m, status);
        using var http = CreateClient(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CopilotUsage.GetBudgetPercentUsedAsync(http, new(), "test org", null, 2026, 10, 1000m));

        Assert.Contains("ai_credit/usage", error.Message);
        Assert.Contains($"failed with {(int)status}", error.Message);
        Assert.Contains("billing error", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public async Task NonpositiveBudgetIsRejectedBeforeQueryingBilling(int budget)
    {
        var handler = new UsageHandler(50m, 150m);
        using var http = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CopilotUsage.GetBudgetPercentUsedAsync(http, new(), "test org", null, 2026, 10, budget));

        Assert.Empty(handler.RequestUrls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3300, 0)]
    [InlineData(3301, 1)]
    [InlineData(6699, 1)]
    [InlineData(6700, 2)]
    [InlineData(9999, 2)]
    [InlineData(10000, 3)]
    [InlineData(12500, 3)]
    public void TrafficLightPreservesExistingThresholds(int hundredthsOfPercent, int expected)
    {
        Assert.Equal(expected, CopilotUsage.GetTrafficLight(hundredthsOfPercent / 100m));
    }

    private static HttpClient CreateClient(UsageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.github.com/") };

    private sealed class UsageHandler(decimal premiumAmount, decimal aiCreditAmount, HttpStatusCode aiCreditStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal List<string> RequestUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
            RequestUrls.Add(uri.AbsoluteUri);

            var isPremium = uri.AbsolutePath.EndsWith("/premium_request/usage", StringComparison.Ordinal);
            if (!isPremium && !uri.AbsolutePath.EndsWith("/ai_credit/usage", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected billing meter: {uri.AbsolutePath}");
            }

            var status = isPremium ? HttpStatusCode.OK : aiCreditStatus;
            var grossAmount = isPremium ? premiumAmount : aiCreditAmount;
            var body = status == HttpStatusCode.OK
                ? JsonSerializer.Serialize(new { usageItems = new[] { new { grossAmount, netAmount = 0m, grossQuantity = 1900m } } })
                : "billing error";

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
