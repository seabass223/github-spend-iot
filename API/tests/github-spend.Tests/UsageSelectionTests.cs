using Xunit;

namespace GitHubSpend.Tests;

public sealed class UsageSelectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "github-spend-tests-" + Guid.NewGuid());
    private string PathIn(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void MissingFileMeansNothingSavedYet() =>
        Assert.Null(UsageSelection.Load(PathIn("nope.json")));

    [Fact]
    public void SavedSelectionComesBackInMenuOrder()
    {
        var path = PathIn("sub/selection.json"); // Save creates the folder
        UsageSelection.Save(path, new[] { UsageSource.ElevenLabs, UsageSource.WorkClaudeCode });

        Assert.Equal(new[] { UsageSource.WorkClaudeCode, UsageSource.ElevenLabs }, UsageSelection.Load(path));
    }

    [Fact]
    public void AnEmptySelectionIsRememberedAsEmptyNotAsMissing()
    {
        var path = PathIn("selection.json");
        UsageSelection.Save(path, Array.Empty<UsageSource>());

        Assert.Empty(UsageSelection.Load(path)!);
    }

    [Fact]
    public void UnknownNamesAreIgnoredAndGarbageCountsAsNothingSaved()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn("a.json"), """{"selected": ["ChatGpt", "SomethingNew"]}""");
        File.WriteAllText(PathIn("b.json"), "not json at all");

        Assert.Equal(new[] { UsageSource.ChatGpt }, UsageSelection.Load(PathIn("a.json")));
        Assert.Null(UsageSelection.Load(PathIn("b.json")));
    }

    [Fact]
    public void RefreshMinutesAreRememberedAndMissingMeansNoneSaved()
    {
        var path = PathIn("selection.json");
        Assert.Null(UsageSelection.LoadRefreshMinutes(path));

        UsageSelection.SaveRefreshMinutes(path, 12);

        Assert.Equal(12, UsageSelection.LoadRefreshMinutes(path));
    }

    [Fact]
    public void SavingMetersKeepsTheMinutesAndTheOtherWayRound()
    {
        var path = PathIn("selection.json");
        UsageSelection.Save(path, new[] { UsageSource.ChatGpt });
        UsageSelection.SaveRefreshMinutes(path, 3);
        UsageSelection.Save(path, new[] { UsageSource.ElevenLabs });

        Assert.Equal(3, UsageSelection.LoadRefreshMinutes(path));
        Assert.Equal(new[] { UsageSource.ElevenLabs }, UsageSelection.Load(path));
    }

    [Fact]
    public void AFileFromBeforeMinutesExistedStillLoads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn("old.json"), """{"selected": ["PersonalClaudeCode", "ChatGpt"]}""");

        Assert.Null(UsageSelection.LoadRefreshMinutes(PathIn("old.json")));
        UsageSelection.SaveRefreshMinutes(PathIn("old.json"), 7);
        Assert.Equal(new[] { UsageSource.PersonalClaudeCode, UsageSource.ChatGpt }, UsageSelection.Load(PathIn("old.json")));
    }

    [Fact]
    public void TheWindowChoiceIsRememberedAlongsideTheOthers()
    {
        var path = PathIn("selection.json");
        Assert.Null(UsageSelection.LoadWindow(path));

        UsageSelection.Save(path, new[] { UsageSource.ChatGpt });
        UsageSelection.SaveWindow(path, UsageWindow.FiveHour);
        UsageSelection.SaveRefreshMinutes(path, 4);

        Assert.Equal(UsageWindow.FiveHour, UsageSelection.LoadWindow(path));
        Assert.Equal(new[] { UsageSource.ChatGpt }, UsageSelection.Load(path));
        Assert.Equal(4, UsageSelection.LoadRefreshMinutes(path));
    }

    [Fact]
    public void AnUnknownWindowCountsAsNoneSaved()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn("w.json"), """{"window": "Fortnightly"}""");

        Assert.Null(UsageSelection.LoadWindow(PathIn("w.json")));
    }

    [Theory]
    [InlineData("""{"refreshMinutes": 0}""")]
    [InlineData("""{"refreshMinutes": 5000}""")]
    [InlineData("""{"refreshMinutes": "ten"}""")]
    public void OutOfRangeOrOddMinutesCountAsNoneSaved(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn("m.json"), json);

        Assert.Null(UsageSelection.LoadRefreshMinutes(PathIn("m.json")));
    }
}
