/// <summary>The meters that can appear on the LCD, in the order they are offered at startup.</summary>
internal enum UsageSource
{
    WorkClaudeCode,
    WorkGitHubCopilot,
    PersonalClaudeCode,
    PersonalGitHubCopilot,
    ChatGpt,
    ElevenLabs,
}

/// <summary>
/// Which usage limit the Claude Code and Chat-GPT meters show: the weekly one (the default) or the
/// rolling 5-hour one. The other meters have only their billing period and ignore it.
/// </summary>
internal enum UsageWindow
{
    Weekly,
    FiveHour,
}

internal static class UsageSources
{
    internal static IReadOnlyList<UsageSource> All { get; } = Enum.GetValues<UsageSource>();

    /// <summary>At most this many meters can be shown at once: two per LCD line, two lines.</summary>
    internal const int MaxSelected = 4;

    /// <summary>True for the meters that have both a weekly and a 5-hour limit (see <see cref="UsageWindow"/>).</summary>
    internal static bool HasWindows(UsageSource source) =>
        source is UsageSource.WorkClaudeCode or UsageSource.PersonalClaudeCode or UsageSource.ChatGpt;

    internal static string Label(UsageSource source) => source switch
    {
        UsageSource.WorkClaudeCode => "Work Claude Code",
        UsageSource.WorkGitHubCopilot => "Work Github Copilot",
        UsageSource.PersonalClaudeCode => "Personal Claude Code",
        UsageSource.PersonalGitHubCopilot => "Personal Github Copilot",
        UsageSource.ChatGpt => "Chat-GPT",
        UsageSource.ElevenLabs => "ElevenLabs",
        _ => source.ToString(),
    };
}
