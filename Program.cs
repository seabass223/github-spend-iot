using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:7007");

var settings = LocalSettings.Load();

var token = settings["GITHUB_TOKEN"];
var org = settings["GITHUB_ORG"];
var user = settings["GITHUB_USER"];

if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("GITHUB_TOKEN is required (needs Plan: read-only / org billing read access).");
    return 1;
}

if (string.IsNullOrWhiteSpace(org))
{
    Console.Error.WriteLine("GITHUB_ORG is required.");
    return 1;
}

using var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
http.DefaultRequestHeaders.UserAgent.ParseAdd("github-spend-copilot-billing/1.0");

var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

if (string.IsNullOrWhiteSpace(user))
{
    using var me = await http.GetAsync("user");
    if (!me.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Could not resolve the authenticated user ({(int)me.StatusCode}). Set GITHUB_USER.");
        return 1;
    }

    user = (await me.Content.ReadFromJsonAsync<Account>(json))?.Login;
    if (string.IsNullOrWhiteSpace(user))
    {
        Console.Error.WriteLine("Could not resolve the authenticated user login. Set GITHUB_USER.");
        return 1;
    }
}

var app = builder.Build();

app.MapGet("/me-this-month", async () =>
{
    var today = DateTime.UtcNow;
    var spend = await GetSpendAsync(http, json, org, user, today.Year, today.Month, day: null);
    return Results.Ok(decimal.ToInt32(decimal.Floor(spend)));
});

app.MapGet("/ai-credits-used", async () =>
{
    var today = DateTime.UtcNow;
    var percentUsed = await GetAiCreditsPercentUsedAsync(http, json, org, today.Year, today.Month);

    Console.WriteLine($"AI credits used: {percentUsed:F1}%");
    return Results.Ok(percentUsed);
});

app.MapGet("/ai-credits-used-traffic-light", async () =>
{
    var today = DateTime.UtcNow;
    var percentUsed = await GetAiCreditsPercentUsedAsync(http, json, org, today.Year, today.Month);
    var trafficLight = percentUsed <= 33m
        ? 0
        : percentUsed < 67m
            ? 1
            : percentUsed < 100m
                ? 2
                : 3;

    return Results.Ok(trafficLight);
});

await app.RunAsync();
return 0;

// Additional (metered) Copilot spend = premium requests over the included allowance + AI credits.
static async Task<decimal> GetSpendAsync(HttpClient http, JsonSerializerOptions json, string org, string? user, int year, int month, int? day)
{
    decimal total = 0;
    foreach (var meter in new[] { "premium_request/", "ai_credit/" })
    {
        total += await GetMeterNetAmountAsync(http, json, org, meter, user, year, month, day);
    }

    return total;
}

static async Task<decimal> GetAiCreditsUsedAsync(HttpClient http, JsonSerializerOptions json, string org, int year, int month)
{
    var url = $"organizations/{Uri.EscapeDataString(org)}/settings/billing/ai_credit/usage?year={year}&month={month}";
    var report = await GetJsonAsync<UsageReport>(http, json, url);
    return report.UsageItems?.Sum(item => item.GrossQuantity) ?? 0;
}

static async Task<decimal> GetAiCreditsPercentUsedAsync(HttpClient http, JsonSerializerOptions json, string org, int year, int month)
{
    var usedCredits = await GetAiCreditsUsedAsync(http, json, org, year, month);
    var copilotBilling = await GetCopilotBillingAsync(http, json, org);
    var totalSeats = copilotBilling.SeatBreakdown?.Total ?? 0;
    var planType = copilotBilling.PlanType;
    var creditsPerSeat = planType?.ToLowerInvariant() switch
    {
        "business" => 1900m,
        "enterprise" => 3900m,
        _ => throw new InvalidOperationException($"Unsupported Copilot plan type: {planType}")
    };
    var includedCredits = totalSeats * creditsPerSeat;

    // Assumes this organization's seats represent its effective AI credit pool.
    return includedCredits == 0
        ? 0
        : usedCredits / includedCredits * 100m;
}

static async Task<CopilotBilling> GetCopilotBillingAsync(HttpClient http, JsonSerializerOptions json, string org)
{
    var url = $"orgs/{Uri.EscapeDataString(org)}/copilot/billing";
    return await GetJsonAsync<CopilotBilling>(http, json, url);
}

static async Task<T> GetJsonAsync<T>(HttpClient http, JsonSerializerOptions json, string url)
{
    using var response = await http.GetAsync(url);
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"GET {url} failed with {(int)response.StatusCode}: {body}");
    }

    return await response.Content.ReadFromJsonAsync<T>(json)
        ?? throw new InvalidOperationException($"GET {url} returned an empty response body.");
}

static async Task<decimal> GetMeterNetAmountAsync(HttpClient http, JsonSerializerOptions json, string org, string meter, string? user, int year, int month, int? day)
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
    // return report?.UsageItems?.Sum(i => i.NetAmount) ?? 0;
    return report?.UsageItems?.Sum(i => i.GrossAmount) ?? 0;
}

sealed record Account([property: JsonPropertyName("login")] string? Login);

sealed record UsageReport([property: JsonPropertyName("usageItems")] List<UsageItem>? UsageItems);

sealed record CopilotBilling(
    [property: JsonPropertyName("seat_breakdown")] CopilotSeatBreakdown? SeatBreakdown,
    [property: JsonPropertyName("plan_type")] string? PlanType);

sealed record CopilotSeatBreakdown([property: JsonPropertyName("total")] decimal Total);

sealed record UsageItem(
    [property: JsonPropertyName("product")] string? Product,
    [property: JsonPropertyName("sku")] string? Sku,
    [property: JsonPropertyName("grossQuantity")] decimal GrossQuantity,
    [property: JsonPropertyName("grossAmount")] decimal GrossAmount,
    [property: JsonPropertyName("discountAmount")] decimal DiscountAmount,
    [property: JsonPropertyName("netAmount")] decimal NetAmount);