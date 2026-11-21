using Spectre.Console;

/// <summary>
/// What the startup questions settled on: the meters to show, the typed values if test mode was
/// ticked, and how often the Photon should fetch the screen.
/// </summary>
internal sealed record StartupChoice(IReadOnlyList<UsageSource> Meters, IReadOnlyDictionary<UsageSource, int?> TestValues)
{
    /// <summary>True when every selected meter shows a typed value (null = "--") instead of its live one.</summary>
    internal bool TestMode => TestValues.Count > 0;

    internal int RefreshMinutes { get; init; } = UsageSelection.DefaultRefreshMinutes;

    /// <summary>Whether Claude Code and Chat-GPT show their weekly or their 5-hour limit.</summary>
    internal UsageWindow Window { get; init; } = UsageWindow.Weekly;
}

/// <summary>
/// The checkbox list shown when the server starts. Ticks what was selected last time; Space toggles,
/// Enter accepts. At most <see cref="UsageSources.MaxSelected"/> meters can be ticked: tick more and it
/// says so and asks again. Below the meters are two more boxes: "5-hour limits", which makes Claude
/// Code and Chat-GPT show their 5-hour limit instead of the weekly one (remembered like the meters),
/// and "Test mode", never pre-ticked: tick it and Enter then asks for the value each selected meter
/// should show, for this run only.
/// Left untouched for <see cref="IdleTimeout"/>, it accepts what's ticked.
/// It also shows in the ./build.sh --launch window, whose output is piped to the log (see PickerConsole.cs).
/// Without a visible console (a hidden launch, input redirected) it skips the prompt and uses the
/// saved selection, or the first four meters if nothing was saved yet.
/// </summary>
internal static class UsagePicker
{
    /// <summary>With no key pressed for this long, the picker goes ahead with the ticked meters.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    internal const string FiveHourLabel = "5-hour limits for Claude Code and Chat-GPT (instead of weekly)";
    internal const string TestModeLabel = "Test mode (type the values the LCD shows)";

    /// <summary>What a row of the list is. Meter comes first so that default(Row) is a meter row.</summary>
    private enum RowKind
    {
        Meter,
        FiveHour,
        TestMode,
    }

    /// <summary>
    /// One row of the list: a meter, the 5-hour box or the test mode box. Spectre starts the highlight
    /// on the row equal to default(Row), so the default must be the first meter, not one of the boxes.
    /// </summary>
    private readonly record struct Row(UsageSource Meter, RowKind Kind)
    {
        internal static Row For(UsageSource meter) => new(meter, RowKind.Meter);
        internal static Row FiveHour => new(default, RowKind.FiveHour);
        internal static Row TestMode => new(default, RowKind.TestMode);
    }

    private static string RowLabel(Row row) => row.Kind switch
    {
        RowKind.FiveHour => FiveHourLabel,
        RowKind.TestMode => TestModeLabel,
        _ => UsageSources.Label(row.Meter),
    };

    /// <summary>The meters picked, ignoring the other boxes.</summary>
    internal static IReadOnlyList<UsageSource> Choose(IAnsiConsole console, IReadOnlyCollection<UsageSource>? previous, bool interactive) =>
        Pick(console, previous, interactive).Meters;

    internal static StartupChoice Pick(IAnsiConsole console, IReadOnlyCollection<UsageSource>? previous, bool interactive,
        UsageWindow previousWindow = UsageWindow.Weekly)
    {
        var preselected = Ordered(previous ?? UsageSources.All).Take(UsageSources.MaxSelected).ToList();
        if (!interactive)
        {
            return new StartupChoice(preselected, NoTestValues) { Window = previousWindow };
        }

        var fiveHour = previousWindow == UsageWindow.FiveHour;
        var testMode = false;
        while (true)
        {
            var prompt = new MultiSelectionPrompt<Row>()
                .Title($"Which usage meters should the LCD show? [grey](up to {UsageSources.MaxSelected})[/]")
                .NotRequired()
                .PageSize(UsageSources.All.Count + 2)
                .InstructionsText("[grey](Press [blue]<space>[/] to toggle a row, [green]<enter>[/] to accept. " +
                                  $"Untouched, it accepts in {IdleTimeout.TotalSeconds:0} s.)[/]")
                .UseConverter(RowLabel)
                .AddChoices(UsageSources.All.Select(Row.For).Append(Row.FiveHour).Append(Row.TestMode));

            foreach (var meter in preselected)
            {
                prompt.Select(Row.For(meter));
            }

            if (fiveHour)
            {
                prompt.Select(Row.FiveHour);
            }

            if (testMode)
            {
                prompt.Select(Row.TestMode);
            }

            var rows = console.Prompt(prompt);
            var picked = Ordered(rows.Where(r => r.Kind == RowKind.Meter).Select(r => r.Meter));
            fiveHour = rows.Contains(Row.FiveHour);
            testMode = rows.Contains(Row.TestMode);
            preselected = picked.ToList();

            if (picked.Count > UsageSources.MaxSelected)
            {
                console.MarkupLine($"[red]Pick at most {UsageSources.MaxSelected} meters (you ticked {picked.Count}).[/]");
                continue;
            }

            if (testMode && picked.Count == 0)
            {
                console.MarkupLine("[red]Tick at least one meter to give it a test value.[/]");
                continue;
            }

            return new StartupChoice(picked, testMode ? AskTestValues(console, picked) : NoTestValues)
            {
                Window = fiveHour ? UsageWindow.FiveHour : UsageWindow.Weekly,
            };
        }
    }

    /// <summary>Asks, in LCD order, what each selected meter should show: 0-999, or -- for "unavailable".</summary>
    private static Dictionary<UsageSource, int?> AskTestValues(IAnsiConsole console, IReadOnlyList<UsageSource> meters)
    {
        console.MarkupLine($"[yellow]Test mode:[/] type what each meter shows, 0-{TestOverrides.MaxPercent} " +
                           "or [blue]--[/] for unavailable. Only this run uses them; nothing is saved.");
        var values = new Dictionary<UsageSource, int?>();
        foreach (var meter in meters)
        {
            var text = console.Prompt(new TextPrompt<string>($"  {Markup.Escape(UsageSources.Label(meter))}:")
                .Validate(typed => TestOverrides.TryParseValue(typed.Trim(), out _)
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]Type 0-{TestOverrides.MaxPercent} or --[/]")));
            TestOverrides.TryParseValue(text.Trim(), out var value);
            values[meter] = value;
        }

        return values;
    }

    /// <summary>
    /// Asks how many minutes the Photon waits between fetches. Enter on a blank line keeps
    /// <paramref name="current"/> (what was typed last time). When the picker timed out unattended,
    /// its keyboard keeps answering Enter, so this keeps the current value without waiting.
    /// </summary>
    internal static int AskRefreshMinutes(IAnsiConsole console, int current)
    {
        var error = $"[red]Type whole minutes, {UsageSelection.MinRefreshMinutes}-{UsageSelection.MaxRefreshMinutes}[/]";
        return console.Prompt(new TextPrompt<int>("How often should the LCD refresh, in minutes?")
            .DefaultValue(current)
            .ValidationErrorMessage(error)
            .Validate(minutes => minutes is >= UsageSelection.MinRefreshMinutes and <= UsageSelection.MaxRefreshMinutes
                ? ValidationResult.Success()
                : ValidationResult.Error(error)));
    }

    /// <summary>
    /// Startup entry point: loads what was saved, asks (when interactive), saves the answers and
    /// returns them. The meter selection and the 5-hour box are saved only after a real, non-test
    /// choice, so neither a hidden launch nor a test run changes what you normally see; the refresh
    /// interval is saved whenever it was asked.
    /// </summary>
    internal static StartupChoice RunAtStartup(string selectionPath)
    {
        var previous = UsageSelection.Load(selectionPath);
        var savedWindow = UsageSelection.LoadWindow(selectionPath) ?? UsageWindow.Weekly;
        var savedMinutes = UsageSelection.LoadRefreshMinutes(selectionPath) ?? UsageSelection.DefaultRefreshMinutes;
        StartupChoice choice;
        using (var window = PickerConsole.Open(IdleTimeout))
        {
            if (window is null)
            {
                choice = Pick(AnsiConsole.Console, previous, interactive: false, savedWindow) with { RefreshMinutes = savedMinutes };
            }
            else
            {
                choice = Pick(window.Console, previous, interactive: true, savedWindow);
                choice = choice with { RefreshMinutes = AskRefreshMinutes(window.Console, savedMinutes) };
                if (!choice.TestMode)
                {
                    UsageSelection.Save(selectionPath, choice.Meters);
                    UsageSelection.SaveWindow(selectionPath, choice.Window);
                }

                UsageSelection.SaveRefreshMinutes(selectionPath, choice.RefreshMinutes);
            }
        }

        Console.WriteLine(choice.Meters.Count == 0
            ? "No meters selected: the LCD will stay blank."
            : "Showing: " + string.Join(", ", choice.Meters.Select(UsageSources.Label)));
        if (choice.Meters.Any(UsageSources.HasWindows))
        {
            Console.WriteLine(choice.Window == UsageWindow.FiveHour
                ? "Claude Code and Chat-GPT show their 5-hour limits."
                : "Claude Code and Chat-GPT show their weekly limits.");
        }

        Console.WriteLine($"The LCD refreshes every {choice.RefreshMinutes} minute{(choice.RefreshMinutes == 1 ? "" : "s")}.");
        if (choice.TestMode)
        {
            Console.WriteLine("Test mode for this run: " + string.Join(", ", choice.Meters.Select(m =>
                $"{UsageSources.Label(m)} {(choice.TestValues[m] is { } p ? $"{p}%" : "--")}")));
        }

        return choice;
    }

    private static readonly IReadOnlyDictionary<UsageSource, int?> NoTestValues = new Dictionary<UsageSource, int?>();

    private static IReadOnlyList<UsageSource> Ordered(IEnumerable<UsageSource> sources) =>
        UsageSources.All.Where(sources.Contains).ToList();
}
