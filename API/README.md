# GitHub Copilot budget monitor

The budget percentage measures the current UTC month's gross Copilot usage in
dollars against a $1,000 monthly budget:

```text
budget burn (%) = (premium request grossAmount + AI credit grossAmount) / 1000 * 100
```

This includes usage before included allowances and discounts are deducted; it is
not the net amount billed. Percentages are not capped: $250 is 25%, $1,000 is 100%,
and $1,250 is 125%.

## Budget and scope

Two hardcoded settings at the top of [Program.cs](./Program.cs) control the budget:

```csharp
const decimal monthlyBudgetUsd = 1000m;
const bool useOrganizationBudget = true;
```

- `true` (default): total usage across `GITHUB_ORG_WORK`, without a user filter.
- `false`: only usage attributed to `GITHUB_USER_WORK`, or the authenticated user when
  `GITHUB_USER_WORK` is not set.

The monthly budget must be greater than zero. Rebuild/restart after changing
either setting. These settings do not affect the existing personal dollar endpoint.

`GITHUB_TOKEN_WORK`, `GITHUB_ORG_WORK`, and the optional `GITHUB_USER_WORK` are read from
`local.settings.json` under `Values`, with environment-variable fallback.

## Endpoints

The service listens on port 7007. Existing route names and numeric JSON response
shapes are retained for compatibility:

| Route | Response |
| --- | --- |
| `/me-this-month` | Personal gross Copilot usage in dollars, rounded down to a whole dollar (unchanged). |
| `/ai-credits-used` | Budget burn percentage, rather than percentage of included AI credits. |
| `/ai-credits-used-traffic-light` | Budget traffic light: `0` at or below 33%, `1` above 33% and below 67%, `2` from 67% to below 100%, `3` at or above 100%. |

Both budget routes use the same budget and scope settings and sum both Copilot
meters. They no longer depend on the Copilot plan or seat count.

## AI usage remaining (LCD)

### Choosing which meters to show

When the server starts it shows a checkbox list ([Spectre.Console](https://spectreconsole.net)):
**Space** toggles a meter, **Enter** accepts. **At most four** can be ticked: tick a fifth and it
says so and asks again. The last selection is remembered in
`%LOCALAPPDATA%\github-spend\selection.json` and pre-ticked next time; on the very first run the
first four are ticked. **Only the selected meters are queried and sent to the LCD.**

The list also shows in the `./build.sh --launch` / Start Menu window, even though that window copies
the server's output to the log through a pipe: it is drawn straight onto the console window
([PickerConsole.cs](./PickerConsole.cs)). **Left untouched for 30 seconds, it accepts what's ticked**,
so an unattended start still serves the LCD. Moving, Space or Enter turns that timer off; other keys
don't, since the window takes focus as it opens and can catch typing meant elsewhere. Without a
visible console (a hidden launch, redirected input) the prompt is skipped and the saved selection is
used (or the first four if nothing was saved), and a hidden launch never overwrites it.

### Weekly or 5-hour limits

Claude Code and Chat-GPT each have two limits: a weekly one and a rolling 5-hour one. They show the
weekly one unless you tick **5-hour limits for Claude Code and Chat-GPT**, the row after the meters.
The box applies to both services (Work and Personal Claude Code, and Chat-GPT); the other meters
have only their billing period and ignore it. It's remembered like the meter selection, and a test
run doesn't change it. The LCD looks the same either way; the server's log says which one is shown.

### How often the LCD refreshes

After the meter list (and any test values) it asks:

```text
How often should the LCD refresh, in minutes? (5):
```

Type whole minutes (1-1440), or press Enter on a blank line to keep the number in brackets, which is
what you typed last time (5 the first time). It's remembered in the same `selection.json`, even after
a test run. An unattended start keeps it without asking. Each `/lcd` response carries it to the
Photon as an `X-Refresh-Seconds` header, and the Photon waits that long before its next fetch, so a
new interval takes effect after the Photon's next fetch. The refresh button still fetches at once.

### Routes

Both routes use the same data, percent **left** for each selected meter (100 when its period resets):

- `/lcd`: the exact 32 bytes the Photon's 16x2 LCD shows, line 1 then line 2, no newline.
  The bytes are the LCD's character codes (send as Latin-1), and custom glyph slot *n*
  appears at code `0x08 + n`: Claude `0x08`, GitHub cat `0x09`, ElevenLabs `0x0A`,
  OpenAI `0x0B`. The `X-Refresh-Seconds` header sets the Photon's wait until its next fetch.
  The Photon rejects any body that isn't exactly 32 bytes and keeps its last screen; after three
  refresh intervals without a good fetch it draws `!` in the last column of line 1.
- `/usage-remaining`: the numbers as plain text, selected meters only, e.g.
  `WorkClaudeCode=83 ChatGpt=100`, with `-1` where a source isn't configured or failed.

### LCD layout

The selected meters (at most four) pack from the top-left in menu order, **two per line**. Each
cell is `{icon} 000% `: the glyph, a space, the percent left in four characters (left-aligned, up to
`999%`) and a trailing space, seven characters in all. Unselected meters take no space, so the
display never has gaps. Work and personal versions of a service share a glyph, so they are told
apart by their order. The layout lives in [LcdScreen.cs](./LcdScreen.cs), so changing it needs no
reflash.

```text
{Claude} 83%  {GitHub} 30%        four selected
{Claude} 73%  {GitHub} 12%

{Claude} 70%  {GitHub} 100%       three selected: Personal Claude, Personal Copilot, Chat-GPT
{OpenAI} 100%
```

When a percentage changes, the Photon counts it up or down to the new value over about 1.5
seconds instead of jumping. A number rolls only if the previous screen had a number in the same
place after the same glyph; a meter that was `--` or has just appeared shows at once. Keep values
left-aligned in their four-character field, since a roll draws in those four cells.

### Test mode

Make `/lcd` and `/usage-remaining` show values you choose instead of live ones. The values are held
in memory only: closing the window or restarting the server brings back the live values, and
nothing is saved. An overridden meter isn't queried.

**At startup:** in the meter list, tick the meters you want (up to four) and also tick
**Test mode**, the last row (never pre-ticked). Press Enter, then type the value for each ticked
meter in LCD order: 0-999, or `--` to show it as unavailable. A test run doesn't change the
selection remembered for next time.

```text
Which usage meters should the LCD show? (up to 4)
  [X] Personal Claude Code
  [X] Chat-GPT
> [X] Test mode (type the values the LCD shows)
...
Test mode: type what each meter shows, 0-999 or -- for unavailable.
  Personal Claude Code: 42
  Chat-GPT: --
```

**While it runs:** press **Enter** in the server's console window (the one `./build.sh --launch`
opens) to be asked for every value again, without restarting. Each question shows what that meter
shows now: Enter keeps it, a number or `--` changes it, and `live` puts the meter back on its live
value. This works whether or not the server started in test mode.

```text
Change test values: 0-999, -- for unavailable or live. Enter keeps the value in brackets.
Personal Claude Code (42): 15
Chat-GPT (--):
```

Or type a shortcut to change one meter at a time. Meters are numbered in LCD order.

| Type | Effect |
| --- | --- |
| `help` | List the meters with their numbers and what each shows |
| `1 42` | Meter 1 shows 42% (0-999; `42%` works too) |
| `2 --` | Meter 2 shows `--`, as if its source were unavailable |
| `1 live` | Meter 1 goes back to its live value |
| `all 50` | Every meter shows 50% |
| `live` | Every meter goes back to live (test mode off) |

The Photon only fetches at its refresh interval, so press its refresh button to see a change at once. Log
lines are tagged `(test mode)` while any value is overridden.

### Sources (all official interfaces)

| Meter | Source | Period |
| --- | --- | --- |
| Work / Personal Claude Code | `claude -p /usage`, the "Current week (all models)" line, or "Current session" for 5-hour limits. It runs locally with no model call and isn't billed, and it counts desktop app, CLI and claude.ai use. Claude Code signs in itself; no login token is read. Work without `CLAUDE_CONFIG_DIR_WORK` reads the status line snapshot instead (`seven_day` or `five_hour`, see below). | Weekly window, or 5 hours |
| Work Github Copilot | Your own gross usage in `GITHUB_ORG_WORK` (the `/me-this-month` dollars) vs. `monthlyBudgetUsd` ($1,000 = 100,000 AI credits, as on github.com/settings/copilot). Uses the `_WORK` settings above. | Calendar month (UTC) |
| Personal Github Copilot | `GET /users/{user}/settings/billing/ai_credit/usage` vs. `GITHUB_AI_CREDITS_PERSONAL`. Shows `--` until those settings exist. | Calendar month (UTC) |
| Chat-GPT | `codex app-server` method `account/rateLimits/read`: the 10080-minute window (`secondary`), or the 300-minute one (`primary`) for 5-hour limits. Codex signs in itself. | Weekly window, or 5 hours |
| ElevenLabs | `GET /v1/user/subscription`, `character_count` vs. `character_limit` | Your plan's reset date |

Settings (under `Values` in `local.settings.json`, all optional; a selected meter without its
settings shows `--`):

| Key | Value |
| --- | --- |
| `ELEVENLABS_API_KEY` | ElevenLabs API key, **User: Read** permission only |
| `GITHUB_TOKEN_PERSONAL`, `GITHUB_USER_PERSONAL` | Fine-grained PAT from your personal account (**Plan: Read** only) and its username |
| `GITHUB_AI_CREDITS_PERSONAL` | Monthly AI credits in your personal Copilot plan (e.g. `1000`) |
| `CLAUDE_COMMAND` | Override for the Claude Code CLI command (default `claude`) |
| `CLAUDE_CONFIG_DIR_WORK` | The work account's Claude Code profile folder (its `CLAUDE_CONFIG_DIR`); the work meter then reads `/usage` live |
| `CLAUDE_USAGE_FILE_WORK` | Work Claude Code status-line file when `CLAUDE_CONFIG_DIR_WORK` isn't set (default `~/.claude/usage-limits-work.json`) |
| `CODEX_COMMAND` | Override for the Codex CLI command (default `codex.cmd` on Windows) |

Two Claude accounts: Personal uses the default Claude Code profile (`~/.claude`). Point
`CLAUDE_CONFIG_DIR_WORK` at the work profile to read it live too. Without that setting the work meter
falls back to the status line snapshot: run `... claude_usage_statusline.py work` in the work profile's
`statusLine` command. That snapshot only refreshes when the terminal CLI draws its status line, so it
goes stale while you use the desktop app or claude.ai.

The Claude and Codex values come from the machine where Claude Code and Codex are signed in.

## Validation

The focused [regression tests](./tests/github-spend.Tests/CopilotUsageTests.cs)
mock billing responses; no GitHub credentials or network calls are needed.

```powershell
dotnet test .\tests\github-spend.Tests\github-spend.Tests.csproj
```
