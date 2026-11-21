using Xunit;

namespace GitHubSpend.Tests;

public sealed class LcdScreenTests
{
    private static Dictionary<UsageSource, int?> All(int? wc, int? wg, int? gpt, int? pc, int? pg, int? el) => new()
    {
        [UsageSource.WorkClaudeCode] = wc,
        [UsageSource.WorkGitHubCopilot] = wg,
        [UsageSource.ChatGpt] = gpt,
        [UsageSource.PersonalClaudeCode] = pc,
        [UsageSource.PersonalGitHubCopilot] = pg,
        [UsageSource.ElevenLabs] = el,
    };

    // Glyph codes, written as fixed-width escapes: "\x08" followed by a digit would read as one hex number.
    private const string Claude = "\u0008";
    private const string Cat = "\u0009";
    private const string Eleven = "\u000A";
    private const string OpenAi = "\u000B";

    [Fact]
    public void FourMetersMakeTwoPerLineInTheAgreedCellFormat()
    {
        var selection = new Dictionary<UsageSource, int?>
        {
            [UsageSource.WorkClaudeCode] = 83,
            [UsageSource.WorkGitHubCopilot] = 30,
            [UsageSource.PersonalClaudeCode] = 73,
            [UsageSource.PersonalGitHubCopilot] = 12,
        };

        var screen = LcdScreen.RenderUsage(selection);

        Assert.Equal(32, screen.Length);
        // Each cell is "{icon} 000% ": glyph, space, percent padded to 4, space.
        Assert.Equal(Claude + " 83%  " + Cat + " 30%    ", screen[..16]);
        Assert.Equal(Claude + " 73%  " + Cat + " 12%    ", screen[16..]);
    }

    [Fact]
    public void ThreeDigitsAndMissingValuesKeepCellsSevenWide()
    {
        var screen = LcdScreen.RenderUsage(All(100, null, 5, 999, 0, null));

        Assert.Equal(Claude + " 100% " + Cat + " --     ", screen[..16]);
        Assert.Equal(Claude + " 999% " + Cat + " 0%     ", screen[16..]);
    }

    [Fact]
    public void SelectedMetersFloatToTheTopLeftWhateverTheirMenuPosition()
    {
        // Personal Claude, personal GitHub and Chat-GPT only (menu positions 3, 4 and 5).
        var selection = new Dictionary<UsageSource, int?>
        {
            [UsageSource.PersonalClaudeCode] = 70,
            [UsageSource.PersonalGitHubCopilot] = 100,
            [UsageSource.ChatGpt] = 100,
        };

        var screen = LcdScreen.RenderUsage(selection);

        Assert.Equal(Claude + " 70%  " + Cat + " 100%   ", screen[..16]);
        Assert.Equal(OpenAi + " 100%" + new string(' ', 10), screen[16..]);
    }

    [Fact]
    public void TwoMetersSitSideBySideAtTheStartOfLineOne()
    {
        var selection = new Dictionary<UsageSource, int?>
        {
            [UsageSource.WorkGitHubCopilot] = 73,
            [UsageSource.ElevenLabs] = 12,
        };

        var screen = LcdScreen.RenderUsage(selection);

        Assert.Equal(Cat + " 73%  " + Eleven + " 12%    ", screen[..16]);
        Assert.Equal(new string(' ', 16), screen[16..]);
    }

    [Fact]
    public void OneMeterIsJustTheFirstCell()
    {
        var screen = LcdScreen.RenderUsage(new Dictionary<UsageSource, int?> { [UsageSource.ChatGpt] = 55 });

        Assert.Equal(OpenAi + " 55%" + new string(' ', 11), screen[..16]);
        Assert.Equal(new string(' ', 16), screen[16..]);
    }

    [Fact]
    public void NeverShowsMoreThanTheFirstFourEvenIfGivenSix()
    {
        var screen = LcdScreen.RenderUsage(All(83, 73, 30, 91, 55, 12));

        // Menu order: work Claude, work GitHub | personal Claude, personal GitHub. Chat-GPT and ElevenLabs are dropped.
        Assert.Equal(Claude + " 83%  " + Cat + " 73%    ", screen[..16]);
        Assert.Equal(Claude + " 91%  " + Cat + " 55%    ", screen[16..]);
    }

    [Fact]
    public void NothingSelectedIsABlankScreen() =>
        Assert.Equal(new string(' ', 32), LcdScreen.RenderUsage(new Dictionary<UsageSource, int?>()));

    [Fact]
    public void BytesAreOnePerCharacterWithNoZeroByte()
    {
        var bytes = LcdScreen.ToBytes(LcdScreen.RenderUsage(All(0, 0, 0, 0, 0, 0)));

        Assert.Equal(32, bytes.Length);
        Assert.DoesNotContain((byte)0, bytes);
    }

    [Fact]
    public void IndicatorCellIsAlwaysBlank()
    {
        Assert.Equal(' ', LcdScreen.RenderUsage(All(100, 100, 100, 999, 999, 999))[LcdScreen.IndicatorIndex]);
        Assert.Equal(' ', LcdScreen.RenderUsage(All(null, null, null, null, null, null))[LcdScreen.IndicatorIndex]);
    }

    [Fact]
    public void RejectsWrongLength() =>
        Assert.Throws<ArgumentException>(() => LcdScreen.ToBytes("too short"));
}
