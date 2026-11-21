using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace GitHubSpend.Tests;

public sealed class UsagePickerTests
{
    private static TestConsole Interactive()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        return console;
    }

    [Fact]
    public void ShowsAllSixLabelsInTheRequestedOrder()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);

        UsagePicker.Choose(console, previous: null, interactive: true);

        var output = console.Output;
        var labels = new[] { "Work Claude Code", "Work Github Copilot", "Personal Claude Code", "Personal Github Copilot", "Chat-GPT", "ElevenLabs" };
        var positions = labels.Select(l => output.IndexOf(l, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions);
    }

    private static readonly UsageSource[] FirstFour =
    {
        UsageSource.WorkClaudeCode, UsageSource.WorkGitHubCopilot,
        UsageSource.PersonalClaudeCode, UsageSource.PersonalGitHubCopilot,
    };

    [Fact]
    public void FirstRunTicksTheFirstFourAndEnterAcceptsThem()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);

        var chosen = UsagePicker.Choose(console, previous: null, interactive: true);

        Assert.Equal(FirstFour, chosen);
    }

    [Fact]
    public void TickingAFifthMeterIsRefusedAndAsksAgain()
    {
        var console = Interactive();
        // Prompt 1: four ticked; move down to Chat-GPT (index 4) and tick it: five ticked. Enter.
        for (var i = 0; i < 4; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Enter);
        // Prompt 2 starts again at the top with those five ticked: untick Work Claude Code. Enter.
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Enter);

        var chosen = UsagePicker.Choose(console, previous: FirstFour, interactive: true);

        Assert.Equal(new[]
        {
            UsageSource.WorkGitHubCopilot, UsageSource.PersonalClaudeCode,
            UsageSource.PersonalGitHubCopilot, UsageSource.ChatGpt,
        }, chosen);
        Assert.Contains("at most 4", console.Output);
    }

    [Fact]
    public void AnOverlongSavedSelectionIsTrimmedToFour()
    {
        var chosen = UsagePicker.Choose(new TestConsole(), UsageSources.All.ToList(), interactive: false);

        Assert.Equal(FirstFour, chosen);
    }

    [Fact]
    public void LastSelectionIsPreTickedAndSpaceTogglesTheHighlightedMeter()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Spacebar);   // first row (Work Claude Code) is ticked, so this untoggles it
        console.Input.PushKey(ConsoleKey.Enter);

        var chosen = UsagePicker.Choose(console,
            previous: new[] { UsageSource.WorkClaudeCode, UsageSource.ChatGpt }, interactive: true);

        Assert.Equal(new[] { UsageSource.ChatGpt }, chosen);
    }

    [Fact]
    public void CanTickAMeterThatWasOffAndMoveDownTheList()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.DownArrow);  // Work Github Copilot
        console.Input.PushKey(ConsoleKey.Spacebar);   // was off, now on
        console.Input.PushKey(ConsoleKey.Enter);

        var chosen = UsagePicker.Choose(console,
            previous: new[] { UsageSource.ElevenLabs }, interactive: true);

        Assert.Equal(new[] { UsageSource.WorkGitHubCopilot, UsageSource.ElevenLabs }, chosen);
    }

    [Fact]
    public void CanClearEverything()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Enter);

        var chosen = UsagePicker.Choose(console, new[] { UsageSource.WorkClaudeCode }, interactive: true);

        Assert.Empty(chosen);
    }

    [Fact]
    public void NonInteractiveUsesTheSavedSelectionWithoutPrompting()
    {
        var console = new TestConsole(); // no keys pushed: a prompt would throw

        var chosen = UsagePicker.Choose(console, new[] { UsageSource.PersonalClaudeCode }, interactive: false);

        Assert.Equal(new[] { UsageSource.PersonalClaudeCode }, chosen);
        Assert.Equal(string.Empty, console.Output);
    }

    [Fact]
    public void NonInteractiveWithNothingSavedUsesTheFirstFour() =>
        Assert.Equal(FirstFour, UsagePicker.Choose(new TestConsole(), previous: null, interactive: false));

    private static IAnsiConsole WithIdleTimeout(TestConsole console, TimeSpan timeout) =>
        new ConsoleWithInput(console, new EnterWhenIdleInput(console.Input, timeout));

    [Fact]
    public void UntouchedPickerAcceptsTheTickedMetersWhenItTimesOut()
    {
        var console = Interactive(); // no keys: nobody is there

        var chosen = UsagePicker.Choose(WithIdleTimeout(console, TimeSpan.FromMilliseconds(50)),
            previous: new[] { UsageSource.PersonalClaudeCode, UsageSource.ChatGpt }, interactive: true);

        Assert.Equal(new[] { UsageSource.PersonalClaudeCode, UsageSource.ChatGpt }, chosen);
    }

    [Fact]
    public void KeysPressedInTimeWorkAsUsual()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Spacebar); // untick Work Claude Code
        console.Input.PushKey(ConsoleKey.Enter);

        var chosen = UsagePicker.Choose(WithIdleTimeout(console, TimeSpan.FromMinutes(1)),
            previous: new[] { UsageSource.WorkClaudeCode, UsageSource.ChatGpt }, interactive: true);

        Assert.Equal(new[] { UsageSource.ChatGpt }, chosen);
    }

    [Theory]
    [InlineData(ConsoleKey.Spacebar)]
    [InlineData(ConsoleKey.DownArrow)]
    [InlineData(ConsoleKey.UpArrow)]
    public void APickerKeyTurnsTheTimeoutOff(ConsoleKey key)
    {
        var keys = new TestConsoleInput();
        keys.PushKey(key);
        var input = new EnterWhenIdleInput(keys, TimeSpan.Zero); // already past its deadline

        Assert.Equal(key, input.ReadKey(intercept: true)?.Key);
        Assert.False(input.IsKeyAvailable()); // no Enter is invented once someone is choosing
    }

    [Fact]
    public void AStrayKeystrokeLeavesTheTimeoutRunning()
    {
        var keys = new TestConsoleInput();
        keys.PushCharacter('h'); // typing meant for another window, caught as the console took focus
        var input = new EnterWhenIdleInput(keys, TimeSpan.Zero);

        Assert.Equal('h', input.ReadKey(intercept: true)?.KeyChar);
        Assert.True(input.IsKeyAvailable());
        Assert.Equal(ConsoleKey.Enter, input.ReadKey(intercept: true)?.Key);
    }

    [Fact]
    public void PickerStillAcceptsByItselfAfterStrayTyping()
    {
        var typedOnly = Interactive();
        typedOnly.Input.PushText("hello"); // no Space or Enter: only the timeout can finish it

        var chosen = UsagePicker.Choose(WithIdleTimeout(typedOnly, TimeSpan.FromMilliseconds(50)),
            previous: new[] { UsageSource.PersonalClaudeCode, UsageSource.ChatGpt }, interactive: true);

        Assert.Equal(new[] { UsageSource.PersonalClaudeCode, UsageSource.ChatGpt }, chosen);
    }

    [Fact]
    public void WithNoKeyItPressesEnterAtTheDeadline()
    {
        var input = new EnterWhenIdleInput(new TestConsoleInput(), TimeSpan.Zero);

        Assert.True(input.IsKeyAvailable());
        Assert.Equal(ConsoleKey.Enter, input.ReadKey(intercept: true)?.Key);
    }

    private static readonly UsageSource[] ClaudeAndChatGpt = { UsageSource.PersonalClaudeCode, UsageSource.ChatGpt };

    // The highlight starts on the first row; after the meters come the 5-hour box, then test mode.
    private static void TickBoxAndAccept(TestConsole console, int rowsAfterMeters)
    {
        for (var i = 0; i < UsageSources.All.Count + rowsAfterMeters; i++)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
        }

        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Enter);
    }

    private static void TickFiveHourAndAccept(TestConsole console) => TickBoxAndAccept(console, rowsAfterMeters: 0);

    private static void TickTestModeAndAccept(TestConsole console) => TickBoxAndAccept(console, rowsAfterMeters: 1);

    [Fact]
    public void FiveHourBoxIsOfferedAfterTheMetersAndWeeklyIsTheDefault()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);

        var choice = UsagePicker.Pick(console, ClaudeAndChatGpt, interactive: true);

        Assert.Equal(UsageWindow.Weekly, choice.Window);
        var output = console.Output;
        Assert.True(output.IndexOf("ElevenLabs", StringComparison.Ordinal) < output.IndexOf(UsagePicker.FiveHourLabel, StringComparison.Ordinal));
        Assert.True(output.IndexOf(UsagePicker.FiveHourLabel, StringComparison.Ordinal) < output.IndexOf(UsagePicker.TestModeLabel, StringComparison.Ordinal));
    }

    [Fact]
    public void TickingTheFiveHourBoxPicksFiveHourLimitsAndKeepsTheMeters()
    {
        var console = Interactive();
        TickFiveHourAndAccept(console);

        var choice = UsagePicker.Pick(console, ClaudeAndChatGpt, interactive: true);

        Assert.Equal(UsageWindow.FiveHour, choice.Window);
        Assert.Equal(ClaudeAndChatGpt, choice.Meters);
        Assert.False(choice.TestMode);
    }

    [Fact]
    public void LastTimesFiveHourChoiceIsPreTickedAndCanBeUnticked()
    {
        var kept = Interactive();
        kept.Input.PushKey(ConsoleKey.Enter);
        Assert.Equal(UsageWindow.FiveHour,
            UsagePicker.Pick(kept, ClaudeAndChatGpt, interactive: true, UsageWindow.FiveHour).Window);

        var unticked = Interactive();
        TickFiveHourAndAccept(unticked);              // Space on a ticked box unticks it
        Assert.Equal(UsageWindow.Weekly,
            UsagePicker.Pick(unticked, ClaudeAndChatGpt, interactive: true, UsageWindow.FiveHour).Window);
    }

    [Fact]
    public void WithoutAConsoleTheSavedWindowIsKept() =>
        Assert.Equal(UsageWindow.FiveHour,
            UsagePicker.Pick(new TestConsole(), ClaudeAndChatGpt, interactive: false, UsageWindow.FiveHour).Window);

    [Fact]
    public void TestModeIsOfferedLastAndStartsUnticked()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);

        var choice = UsagePicker.Pick(console, ClaudeAndChatGpt, interactive: true);

        Assert.False(choice.TestMode);
        Assert.Empty(choice.TestValues);
        Assert.Equal(ClaudeAndChatGpt, choice.Meters);
        Assert.True(console.Output.IndexOf(UsagePicker.TestModeLabel, StringComparison.Ordinal) >
                    console.Output.IndexOf("ElevenLabs", StringComparison.Ordinal));
    }

    [Fact]
    public void TickingTestModeAsksForAValuePerPickedMeterInLcdOrder()
    {
        var console = Interactive();
        TickTestModeAndAccept(console);
        console.Input.PushTextWithEnter("42");
        console.Input.PushTextWithEnter("--");

        var choice = UsagePicker.Pick(console, ClaudeAndChatGpt, interactive: true);

        Assert.True(choice.TestMode);
        Assert.Equal(ClaudeAndChatGpt, choice.Meters);
        Assert.Equal(42, choice.TestValues[UsageSource.PersonalClaudeCode]);
        Assert.Null(choice.TestValues[UsageSource.ChatGpt]);
        Assert.True(console.Output.IndexOf("Personal Claude Code:", StringComparison.Ordinal) <
                    console.Output.IndexOf("Chat-GPT:", StringComparison.Ordinal));
    }

    [Fact]
    public void ATestValueItCantReadIsAskedAgain()
    {
        var console = Interactive();
        TickTestModeAndAccept(console);
        console.Input.PushTextWithEnter("lots");
        console.Input.PushTextWithEnter("1000");
        console.Input.PushTextWithEnter("7");

        var choice = UsagePicker.Pick(console, new[] { UsageSource.ChatGpt }, interactive: true);

        Assert.Equal(7, choice.TestValues[UsageSource.ChatGpt]);
        Assert.Contains("Type 0-999 or --", console.Output);
    }

    [Fact]
    public void TestModeWithNoMeterTickedAsksAgain()
    {
        var console = Interactive();
        TickTestModeAndAccept(console);                // test mode on, but no meter ticked
        console.Input.PushKey(ConsoleKey.Spacebar);    // asked again from the top: tick Work Claude Code
        console.Input.PushKey(ConsoleKey.Enter);       // test mode is still ticked
        console.Input.PushTextWithEnter("5");

        var choice = UsagePicker.Pick(console, Array.Empty<UsageSource>(), interactive: true);

        Assert.Contains("at least one meter", console.Output);
        Assert.Equal(new[] { UsageSource.WorkClaudeCode }, choice.Meters);
        Assert.Equal(5, choice.TestValues[UsageSource.WorkClaudeCode]);
    }

    [Fact]
    public void BlankRefreshAnswerKeepsLastTimesMinutes()
    {
        var console = Interactive();
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(12, UsagePicker.AskRefreshMinutes(console, current: 12));
        Assert.Contains("(12)", console.Output); // the default is shown
    }

    [Fact]
    public void TypedRefreshMinutesAreUsed()
    {
        var console = Interactive();
        console.Input.PushTextWithEnter("2");

        Assert.Equal(2, UsagePicker.AskRefreshMinutes(console, current: 5));
    }

    [Fact]
    public void RefreshMinutesOutOfRangeOrNotANumberAreAskedAgain()
    {
        var console = Interactive();
        console.Input.PushTextWithEnter("0");
        console.Input.PushTextWithEnter("2000");
        console.Input.PushTextWithEnter("soon");
        console.Input.PushTextWithEnter("30");

        Assert.Equal(30, UsagePicker.AskRefreshMinutes(console, current: 5));
        Assert.Contains("Type whole minutes, 1-1440", console.Output);
    }

    [Fact]
    public void AnUnattendedStartKeepsTheMinutesWithoutWaiting()
    {
        var console = Interactive(); // nobody there: no keys at all
        var unattended = WithIdleTimeout(console, TimeSpan.FromMilliseconds(50));

        var choice = UsagePicker.Pick(unattended, ClaudeAndChatGpt, interactive: true);
        var minutes = UsagePicker.AskRefreshMinutes(unattended, current: 9);

        Assert.Equal(ClaudeAndChatGpt, choice.Meters);
        Assert.Equal(9, minutes);
    }
}
