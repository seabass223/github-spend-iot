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

var token = settings["GITHUB_TOKEN_WORK"];
var org = settings["GITHUB_ORG_WORK"];
var user = settings["GITHUB_USER_WORK"];

if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("GITHUB_TOKEN_WORK is required (needs Plan: read-only / org billing read access).");
    return 1;
}

if (string.IsNullOrWhiteSpace(org))
{
    Console.Error.WriteLine("GITHUB_ORG_WORK is required.");
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
        Console.Error.WriteLine($"Could not resolve the authenticated user ({(int)me.StatusCode}). Set GITHUB_USER_WORK.");
        return 1;
    }

    user = (await me.Content.ReadFromJsonAsync<Account>(json))?.Login;
    if (string.IsNullOrWhiteSpace(user))
    {
        Console.Error.WriteLine("Could not resolve the authenticated user login. Set GITHUB_USER_WORK.");
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

// Which meters to show: a checkbox list at startup that remembers the last choice (see
// UsagePicker.cs). Only the selected ones are queried and sent to the LCD. Ticking its
// "Test mode" box asks for a value per meter instead.
var startup = UsagePicker.RunAtStartup(UsageSelection.DefaultPath);
var selected = startup.Meters;

// Test values for this run only: from the picker, and changed later by typing into this
// console window (see TestMode.cs).
var testMode = new TestOverrides(selected);
foreach (var (meter, value) in startup.TestValues)
{
    testMode.Set(meter, value);
}

app.Lifetime.ApplicationStarted.Register(testMode.ListenOnConsole);
string LogTag() => testMode.Active ? " (test mode)" : "";

// Percent left for each selected meter; null where a source isn't configured or failed.
// See UsageRemaining.cs.
using var usageHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

string ClaudeFile(string settingName, string defaultName) =>
    settings[settingName]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", defaultName);

var claudeCommand = settings["CLAUDE_COMMAND"] ?? "claude";

async Task<IReadOnlyDictionary<UsageSource, int?>> GetUsageAsync()
{
    var now = DateTimeOffset.UtcNow;

    async Task<int?> Read(UsageSource source)
    {
        if (testMode.TryGet(source, out var typed))
        {
            return typed; // test mode: never queried, never saved
        }

        try
        {
            return source switch
            {
                // Claude Code's own `claude -p /usage`, live, so desktop app and claude.ai use counts too.
                // Work runs it against its own profile when CLAUDE_CONFIG_DIR_WORK is set, otherwise it
                // falls back to the status line snapshot (tools/claude_usage_statusline.py work).
                // Claude and Chat-GPT show the weekly or the 5-hour limit, as picked at startup.
                UsageSource.WorkClaudeCode => settings["CLAUDE_CONFIG_DIR_WORK"] is { } workProfile
                    ? await UsageRemaining.GetClaudeAsync(claudeCommand, workProfile, startup.Window, TimeSpan.FromSeconds(30))
                    : UsageRemaining.GetClaudeFromFile(ClaudeFile("CLAUDE_USAGE_FILE_WORK", "usage-limits-work.json"), now, startup.Window),
                UsageSource.PersonalClaudeCode =>
                    await UsageRemaining.GetClaudeAsync(claudeCommand, configDir: null, startup.Window, TimeSpan.FromSeconds(30)),

                // Your own Copilot usage at work (same gross dollars as /me-this-month) against the
                // monthly budget: $1,000 = 100,000 AI credits, the "x / 100,000" GitHub's page shows.
                UsageSource.WorkGitHubCopilot => UsageRemaining.FromQuantity(
                    await CopilotUsage.GetSpendAsync(http, json, org, user, now.Year, now.Month, day: null), monthlyBudgetUsd),

                UsageSource.PersonalGitHubCopilot => await ReadPersonalGitHubAsync(now),

                UsageSource.ChatGpt => await UsageRemaining.GetCodexAsync(
                    settings["CODEX_COMMAND"] ?? (OperatingSystem.IsWindows() ? "codex.cmd" : "codex"), now, TimeSpan.FromSeconds(30),
                    startup.Window),

                UsageSource.ElevenLabs => settings["ELEVENLABS_API_KEY"] is { } key
                    ? await UsageRemaining.GetElevenLabsAsync(usageHttp, key)
                    : null,

                _ => null,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{UsageSources.Label(source)} usage unavailable: {ex.Message}");
            return null;
        }
    }

    // Start every selected meter at once; unselected ones are never touched.
    var reads = selected.ToDictionary(s => s, Read);
    await Task.WhenAll(reads.Values);
    return reads.ToDictionary(r => r.Key, r => r.Value.Result);
}

async Task<int?> ReadPersonalGitHubAsync(DateTimeOffset now)
{
    var personalToken = settings["GITHUB_TOKEN_PERSONAL"];
    var personalUser = settings["GITHUB_USER_PERSONAL"];
    var credits = settings["GITHUB_AI_CREDITS_PERSONAL"];
    if (personalToken is null || personalUser is null || credits is null)
    {
        return null; // not configured: shows "--"
    }

    return await UsageRemaining.GetGitHubPersonalAsync(usageHttp, personalToken, personalUser,
        decimal.Parse(credits, CultureInfo.InvariantCulture), now.UtcDateTime);
}

// Selected meters only, e.g. "WorkClaudeCode=83 ChatGpt=100", -1 where unavailable.
app.MapGet("/usage-remaining", async () =>
{
    var text = UsageRemaining.FormatForDisplay(await GetUsageAsync());
    Console.WriteLine($"Usage remaining{LogTag()}: {text}");
    return Results.Text(text);
});

// The exact 32 bytes for the Photon's 16x2 LCD (see LcdScreen.cs). Line 1 is bytes 0-15,
// line 2 is bytes 16-31; custom glyphs are codes 0x08-0x0F. The X-Refresh-Seconds header tells
// the Photon how long to wait before its next fetch (the minutes asked at startup).
var refreshSeconds = (startup.RefreshMinutes * 60).ToString(CultureInfo.InvariantCulture);
app.MapGet("/lcd", async (HttpContext context) =>
{
    var screen = LcdScreen.RenderUsage(await GetUsageAsync());
    Console.WriteLine($"LCD{LogTag()}: [{screen[..16]}] [{screen[16..]}]");
    context.Response.Headers[LcdScreen.RefreshHeader] = refreshSeconds;
    return Results.Bytes(LcdScreen.ToBytes(screen), "application/octet-stream");
});

await app.RunAsync();
return 0;

sealed record Account([property: JsonPropertyName("login")] string? Login);