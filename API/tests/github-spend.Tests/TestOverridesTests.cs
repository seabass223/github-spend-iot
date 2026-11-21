using Spectre.Console.Testing;
using Xunit;

namespace GitHubSpend.Tests;

public sealed class TestOverridesTests
{
    private static TestOverrides Make() => new([UsageSource.PersonalClaudeCode, UsageSource.ChatGpt]);

    private static int? Value(TestOverrides test, UsageSource source) =>
        test.TryGet(source, out var value) ? value : throw new Xunit.Sdk.XunitException($"{source} isn't overridden.");

    private static TestConsole Answers(params string[] lines)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        foreach (var line in lines)
        {
            console.Input.PushTextWithEnter(line); // "" is a bare Enter
        }

        return console;
    }

    [Fact]
    public void AskingAgainShowsEachCurrentValueAndEnterKeepsIt()
    {
        var test = Make();
        test.Set(UsageSource.PersonalClaudeCode, 42);
        test.Set(UsageSource.ChatGpt, null);
        var console = Answers("", "");

        test.AskAgain(console);

        Assert.Equal(42, Value(test, UsageSource.PersonalClaudeCode));
        Assert.Null(Value(test, UsageSource.ChatGpt));
        Assert.Contains("Personal Claude Code (42):", console.Output);
        Assert.Contains("Chat-GPT (--):", console.Output);
    }

    [Fact]
    public void AskingAgainChangesValuesAndCanGoBackToLive()
    {
        var test = Make();
        test.Set(UsageSource.PersonalClaudeCode, 42);
        var console = Answers("live", "15");     // Chat-GPT was live: its question shows (live)

        test.AskAgain(console);

        Assert.False(test.TryGet(UsageSource.PersonalClaudeCode, out _));
        Assert.Equal(15, Value(test, UsageSource.ChatGpt));
        Assert.Contains("Chat-GPT (live):", console.Output);
    }

    [Fact]
    public void AskingAgainRepeatsAQuestionItCantRead()
    {
        var test = Make();
        var console = Answers("lots", "--", "7");

        test.AskAgain(console);

        Assert.Null(Value(test, UsageSource.PersonalClaudeCode));
        Assert.Equal(7, Value(test, UsageSource.ChatGpt));
        Assert.Contains("Type 0-999, -- or live", console.Output);
    }

    [Fact]
    public void LeavingEverythingLiveKeepsTestModeOff()
    {
        var test = Make();

        test.AskAgain(Answers("", ""));

        Assert.False(test.Active);
    }

    [Fact]
    public void StartsLive()
    {
        var test = Make();
        Assert.False(test.Active);
        Assert.False(test.TryGet(UsageSource.PersonalClaudeCode, out _));
    }

    [Theory]
    [InlineData("1 42", 42)]
    [InlineData("  1   7%  ", 7)]
    [InlineData("1 0", 0)]
    [InlineData("1 999", 999)]
    public void SetsAMeterByItsLcdNumber(string line, int expected)
    {
        var test = Make();
        test.Apply(line);
        Assert.True(test.Active);
        Assert.Equal(expected, Value(test, UsageSource.PersonalClaudeCode));
        Assert.False(test.TryGet(UsageSource.ChatGpt, out _));
    }

    [Fact]
    public void DashesShowAsUnavailable()
    {
        var test = Make();
        test.Apply("2 --");
        Assert.True(test.TryGet(UsageSource.ChatGpt, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void LiveOnOneMeterKeepsTheOthers()
    {
        var test = Make();
        test.Apply("1 42");
        test.Apply("2 5");
        test.Apply("1 live");
        Assert.False(test.TryGet(UsageSource.PersonalClaudeCode, out _));
        Assert.Equal(5, Value(test, UsageSource.ChatGpt));
    }

    [Fact]
    public void AllSetsEveryMeterAndLiveClearsThem()
    {
        var test = Make();
        test.Apply("all 50");
        Assert.Equal(50, Value(test, UsageSource.PersonalClaudeCode));
        Assert.Equal(50, Value(test, UsageSource.ChatGpt));

        test.Apply("LIVE");
        Assert.False(test.Active);
    }

    [Theory]
    [InlineData("1 1000")]
    [InlineData("1 -5")]
    [InlineData("1 abc")]
    [InlineData("0 50")]
    [InlineData("3 50")]
    [InlineData("claude 50")]
    [InlineData("1")]
    public void IgnoresWhatItCantRead(string line)
    {
        var test = Make();
        Assert.NotEmpty(test.Apply(line));
        Assert.False(test.Active);
    }

    [Fact]
    public void BlankLinePrintsNothing() => Assert.Equal("", Make().Apply("   "));

    [Fact]
    public void ValuesFromThePickerCanBeChangedByTyping()
    {
        var test = Make();
        test.Set(UsageSource.PersonalClaudeCode, 42); // as the startup picker does
        test.Set(UsageSource.ChatGpt, null);

        test.Apply("1 10");
        test.Apply("2 live");

        Assert.Equal(10, Value(test, UsageSource.PersonalClaudeCode));
        Assert.False(test.TryGet(UsageSource.ChatGpt, out _));
    }

    [Fact]
    public void HelpNumbersTheMetersInLcdOrder()
    {
        var test = Make();
        test.Apply("2 --");
        var help = test.Apply("help");
        Assert.Contains("1  Personal Claude Code", help);
        Assert.Contains("live", help);
        Assert.Contains("2  Chat-GPT", help);
        Assert.Contains("--  (test)", help);
    }

    [Fact]
    public void OverriddenValuesRenderOnTheLcd()
    {
        var test = Make();
        test.Apply("1 42");
        test.Apply("2 --");
        var values = new Dictionary<UsageSource, int?>();
        foreach (var source in new[] { UsageSource.PersonalClaudeCode, UsageSource.ChatGpt })
        {
            test.TryGet(source, out var value);
            values[source] = value;
        }

        Assert.Equal($"{LcdScreen.Claude} 42%  {LcdScreen.OpenAi} --     ", LcdScreen.RenderUsage(values)[..16]);
    }
}
