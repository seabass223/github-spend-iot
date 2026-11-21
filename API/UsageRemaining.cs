using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// "Percent left" for each AI service shown on the LCD: 100 at the start of a billing period
/// or limit window, dropping as you use it, clamped to 0..100.
///
/// Every source uses an official, documented interface:
/// - ElevenLabs: REST GET /v1/user/subscription (credits used vs. plan limit, own reset date).
/// - GitHub Copilot (personal): REST AI credit usage report for a user, vs. the plan's monthly credits.
/// - GitHub Copilot (work): your own gross usage in the work org vs. the monthly budget.
/// - Claude Code (Pro/Max): Claude Code's own `claude -p /usage` command, live (no model call).
///   Claude Code signs in itself; this code never sees the Claude token. A work account without its
///   own profile setting still reads the rate_limits snapshot tools/claude_usage_statusline.py saves.
/// - Codex (ChatGPT plan): Codex's own app-server protocol, method account/rateLimits/read.
///   Codex authenticates itself; this code never sees the ChatGPT token.
/// For the two subscriptions the weekly window is used, since it's the closest thing to a
/// billing cycle.
/// </summary>
internal static class UsageRemaining
{
    /// <summary>Percent left from a "used" percentage, treating a window past its reset time as unused.</summary>
    internal static int FromUsedPercent(double usedPercent, long resetsAtUnix, DateTimeOffset now) =>
        now.ToUnixTimeSeconds() >= resetsAtUnix ? 100 : Clamp(100 - usedPercent);

    internal static int FromQuantity(decimal used, decimal limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return Clamp((double)((limit - used) / limit * 100m));
    }

    private static int Clamp(double percent) => (int)Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero);

    // ---- ElevenLabs -----------------------------------------------------------------------

    internal static async Task<int> GetElevenLabsAsync(HttpClient http, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/user/subscription");
        request.Headers.Add("xi-api-key", apiKey);
        var root = await SendJsonAsync(http, request);

        var used = root["character_count"]!.GetValue<decimal>();
        var limit = root["character_limit"]!.GetValue<decimal>();
        return FromQuantity(used, limit);
    }

    // ---- GitHub Copilot, personal account --------------------------------------------------

    internal static async Task<int> GetGitHubPersonalAsync(HttpClient http, string token, string user, decimal monthlyCredits, DateTime utcNow)
    {
        var url = $"https://api.github.com/users/{Uri.EscapeDataString(user)}/settings/billing/ai_credit/usage" +
                  $"?year={utcNow.Year}&month={utcNow.Month}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.UserAgent.ParseAdd("github-spend-usage-display/1.0");

        using var response = await http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return 100; // no usage recorded yet this month
        }

        var root = await ReadJsonAsync(response, url);
        var used = root["usageItems"]?.AsArray().Sum(item => item?["grossQuantity"]?.GetValue<decimal>() ?? 0) ?? 0;
        return FromQuantity(used, monthlyCredits);
    }

    // ---- Claude Code, Pro/Max subscription ---------------------------------------------------

    /// <summary>
    /// The weekly all-models line of `/usage`, e.g. "Current week (all models): 40% used · resets ...".
    /// Per-model lines such as "Current week (Fable): ..." don't match; a plan without per-model
    /// limits may print plain "Current week:".
    /// </summary>
    private static readonly Regex ClaudeWeeklyLine = new(
        @"^Current week(?: \(all models\))?:\s*(\d+(?:\.\d+)?)%\s*used",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The 5-hour line of `/usage`, e.g. "Current session: 12% used · resets 2pm".</summary>
    private static readonly Regex ClaudeSessionLine = new(
        @"^Current session:\s*(\d+(?:\.\d+)?)%\s*used",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs `claude -p /usage`, Claude Code's own usage command. It is answered from the account's plan
    /// limits without a model call (nothing is billed), so it counts usage from every client: the
    /// desktop app, the CLI and claude.ai. The status line snapshot only refreshed when the terminal
    /// CLI drew its status line, so it went stale while the desktop app was used.
    /// <paramref name="configDir"/> selects another Claude Code profile (CLAUDE_CONFIG_DIR); null is the default one.
    /// </summary>
    internal static async Task<int> GetClaudeAsync(string claudeCommand, string? configDir, UsageWindow window, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(claudeCommand)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(), // no project settings or CLAUDE.md
        };
        foreach (var arg in new[] { "-p", "/usage", "--output-format", "json", "--no-session-persistence", "--strict-mcp-config" })
        {
            start.ArgumentList.Add(arg);
        }

        // A server started from inside a Claude Code session inherits that session's CLAUDE_* variables,
        // which would tie this child to a host that may be gone. Start clean and pick the profile explicitly.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        if (configDir is not null)
        {
            start.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{claudeCommand}'.");
        process.StandardInput.Close(); // the prompt is an argument; don't wait for piped input
        process.ErrorDataReceived += (_, _) => { }; // drain stderr so it can't block
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return ParseClaudeUsage(output, window);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// Percent left of the weekly all-models limit ("Current week") or the 5-hour one ("Current
    /// session"), from `claude -p /usage --output-format json`.
    /// </summary>
    internal static int ParseClaudeUsage(string printJson, UsageWindow window = UsageWindow.Weekly)
    {
        var root = JsonNode.Parse(printJson) ?? throw new InvalidOperationException("claude /usage returned no JSON.");
        var text = root["result"]?.GetValue<string>() ?? "";
        var firstLine = text.Split('\n', 2)[0].Trim();
        if (root["is_error"]?.GetValue<bool>() == true)
        {
            throw new InvalidOperationException($"claude /usage failed: {firstLine}");
        }

        var match = (window == UsageWindow.FiveHour ? ClaudeSessionLine : ClaudeWeeklyLine).Match(text);
        if (!match.Success)
        {
            // e.g. signed in with an API key, which has no plan limits
            var name = window == UsageWindow.FiveHour ? "5-hour" : "weekly";
            throw new InvalidOperationException($"claude /usage shows no {name} limit: {firstLine}");
        }

        return Clamp(100 - double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Reads the rate_limits snapshot the status line script saved (only refreshed by the terminal CLI).</summary>
    internal static int? GetClaudeFromFile(string path, DateTimeOffset now, UsageWindow window = UsageWindow.Weekly)
    {
        if (!File.Exists(path))
        {
            return null; // Claude Code hasn't run with the status line yet
        }

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var limit = root["rate_limits"]?[window == UsageWindow.FiveHour ? "five_hour" : "seven_day"];
        if (limit is null)
        {
            return null;
        }

        return FromUsedPercent(limit["used_percentage"]!.GetValue<double>(), limit["resets_at"]!.GetValue<long>(), now);
    }

    // ---- Codex, ChatGPT plan ----------------------------------------------------------------

    /// <summary>Starts `codex app-server`, does the handshake and asks for account rate limits.</summary>
    internal static async Task<int?> GetCodexAsync(string codexCommand, DateTimeOffset now, TimeSpan timeout,
        UsageWindow window = UsageWindow.Weekly)
    {
        using var process = Process.Start(new ProcessStartInfo(codexCommand, "app-server")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Could not start '{codexCommand} app-server'.");

        process.ErrorDataReceived += (_, _) => { }; // drain stderr so it can't block
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            async Task SendAsync(object message)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
                await process.StandardInput.FlushAsync();
            }

            async Task<JsonNode> WaitForIdAsync(int id)
            {
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cts.Token)
                               ?? throw new InvalidOperationException("codex app-server exited early.");
                    var message = JsonNode.Parse(line);
                    if (message?["id"]?.GetValue<int>() == id)
                    {
                        return message;
                    }
                }
            }

            await SendAsync(new { method = "initialize", id = 0, @params = new { clientInfo = new { name = "usage_display", title = "Usage display", version = "1.0.0" } } });
            await WaitForIdAsync(0);
            await SendAsync(new { method = "initialized", @params = new { } });
            await SendAsync(new { method = "account/rateLimits/read", id = 1 });

            var reply = await WaitForIdAsync(1);
            if (reply["error"] is { } error)
            {
                throw new InvalidOperationException($"account/rateLimits/read failed: {error.ToJsonString()}");
            }

            return FromCodexRateLimits(reply["result"]?["rateLimits"], window, now);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// Percent left from Codex's rateLimits: the window lasting 300 minutes for 5-hour or 10080 for
    /// weekly, falling back to "primary" (5-hour) or "secondary" (weekly) if none says how long it lasts.
    /// </summary>
    internal static int? FromCodexRateLimits(JsonNode? rateLimits, UsageWindow window, DateTimeOffset now)
    {
        if (rateLimits is null)
        {
            return null;
        }

        var minutes = window == UsageWindow.FiveHour ? 300 : 10080;
        var limit = new[] { rateLimits["primary"], rateLimits["secondary"] }
                        .FirstOrDefault(w => w?["windowDurationMins"]?.GetValue<long>() == minutes)
                    ?? rateLimits[window == UsageWindow.FiveHour ? "primary" : "secondary"];
        if (limit is null)
        {
            return null;
        }

        return FromUsedPercent(limit["usedPercent"]!.GetValue<double>(), limit["resetsAt"]!.GetValue<long>(), now);
    }

    // ---- Shared --------------------------------------------------------------------------

    /// <summary>Plain text for humans and scripts: "WorkClaudeCode=83 ChatGpt=-1", selected meters only, -1 where unavailable.</summary>
    internal static string FormatForDisplay(IReadOnlyDictionary<UsageSource, int?> values) =>
        string.Join(' ', UsageSources.All.Where(values.ContainsKey)
            .Select(s => $"{s}={(values[s] ?? -1).ToString(CultureInfo.InvariantCulture)}"));

    private static async Task<JsonNode> SendJsonAsync(HttpClient http, HttpRequestMessage request)
    {
        using var response = await http.SendAsync(request);
        return await ReadJsonAsync(response, request.RequestUri!.ToString());
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, string url)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // The body is printed, never the request headers that hold the key.
            throw new InvalidOperationException($"GET {url.Split('?')[0]} failed with {(int)response.StatusCode}: {body}");
        }

        return JsonNode.Parse(body) ?? throw new InvalidOperationException($"GET {url} returned no JSON.");
    }
}
