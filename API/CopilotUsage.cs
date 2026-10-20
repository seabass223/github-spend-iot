using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class CopilotUsage
{
    internal static async Task<decimal> GetBudgetPercentUsedAsync(HttpClient http, JsonSerializerOptions json, string org, string? user, int year, int month, decimal monthlyBudgetUsd)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(monthlyBudgetUsd);

        var spend = await GetSpendAsync(http, json, org, user, year, month, day: null);
        return spend / monthlyBudgetUsd * 100m;
    }

    internal static int GetTrafficLight(decimal percentUsed) =>
        percentUsed <= 33m
            ? 0
            : percentUsed < 67m
                ? 1
                : percentUsed < 100m
                    ? 2
                    : 3;

    internal static async Task<decimal> GetSpendAsync(HttpClient http, JsonSerializerOptions json, string org, string? user, int year, int month, int? day)
    {
        decimal total = 0;
        foreach (var meter in new[] { "premium_request/", "ai_credit/" })
        {
            total += await GetMeterGrossAmountAsync(http, json, org, meter, user, year, month, day);
        }

        return total;
    }

    private static async Task<decimal> GetMeterGrossAmountAsync(HttpClient http, JsonSerializerOptions json, string org, string meter, string? user, int year, int month, int? day)
    {
        var query = $"?year={year}&month={month}";
        if (day.HasValue)
        {
            query += $"&day={day.Value}";
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            query += $"&user={Uri.EscapeDataString(user)}";
        }

        var url = $"organizations/{Uri.EscapeDataString(org)}/settings/billing/{meter}usage{query}";
        using var response = await http.GetAsync(url);

        // A meter with no usage in the period can come back as 404.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"GET {url} failed with {(int)response.StatusCode}: {body}");
        }

        var report = await response.Content.ReadFromJsonAsync<UsageReport>(json);
        return report?.UsageItems?.Sum(item => item.GrossAmount) ?? 0;
    }

    private sealed record UsageReport([property: JsonPropertyName("usageItems")] List<UsageItem>? UsageItems);

    private sealed record UsageItem([property: JsonPropertyName("grossAmount")] decimal GrossAmount);
}
