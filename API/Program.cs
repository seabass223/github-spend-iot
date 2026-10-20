using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

const decimal monthlyBudgetUsd = 1000m;
const bool useOrganizationBudget = true;

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

var budgetUser = useOrganizationBudget ? null : user;
var app = builder.Build();

app.MapGet("/me-this-month", async () =>
{
    var today = DateTime.UtcNow;
    var spend = await CopilotUsage.GetSpendAsync(http, json, org, user, today.Year, today.Month, day: null);
    return Results.Ok(decimal.ToInt32(decimal.Floor(spend)));
});

app.MapGet("/ai-credits-used", async () =>
{
    var today = DateTime.UtcNow;
    var percentUsed = await CopilotUsage.GetBudgetPercentUsedAsync(http, json, org, budgetUser, today.Year, today.Month, monthlyBudgetUsd);

    Console.WriteLine($"Monthly budget used: {percentUsed:F1}%");
    return Results.Ok(percentUsed);
});

app.MapGet("/ai-credits-used-traffic-light", async () =>
{
    var today = DateTime.UtcNow;
    var percentUsed = await CopilotUsage.GetBudgetPercentUsedAsync(http, json, org, budgetUser, today.Year, today.Month, monthlyBudgetUsd);
    var trafficLight = CopilotUsage.GetTrafficLight(percentUsed);

    return Results.Ok(trafficLight);
});

await app.RunAsync();
return 0;

sealed record Account([property: JsonPropertyName("login")] string? Login);