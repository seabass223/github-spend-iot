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

- `true` (default): total usage across `GITHUB_ORG`, without a user filter.
- `false`: only usage attributed to `GITHUB_USER`, or the authenticated user when
  `GITHUB_USER` is not set.

The monthly budget must be greater than zero. Rebuild/restart after changing
either setting. These settings do not affect the existing personal dollar endpoint.

`GITHUB_TOKEN`, `GITHUB_ORG`, and the optional `GITHUB_USER` are read from
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

## Validation

The focused [regression tests](./tests/github-spend.Tests/CopilotUsageTests.cs)
mock billing responses; no GitHub credentials or network calls are needed.

```powershell
dotnet test .\tests\github-spend.Tests\github-spend.Tests.csproj
```
