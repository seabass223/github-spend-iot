using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Spectre.Console;

/// <summary>
/// Test mode: values you choose for meters, served by /lcd (and /usage-remaining) instead of live ones.
/// Set at startup by ticking "Test mode" in the picker (see UsagePicker.cs). While the server runs,
/// pressing Enter in its console window asks for every meter's value again (Enter keeps the one
/// shown, "live" returns a meter to its live value), and typed shortcuts change one at a time.
/// Overrides live in memory only, so they vanish when the exe exits; nothing is saved. Meters are
/// numbered in LCD order (the selected meters). Overridden meters are never queried.
///   (Enter)   asks for every value again
///   1 42      meter 1 shows 42% (0-999, a trailing % is fine)
///   2 --      meter 2 shows "--", as if its source were unavailable
///   1 live    meter 1 goes back to its live value
///   all 50    every meter shows 50%
///   live      every meter goes back to live (test mode off)
///   help      the meters, their numbers and what each shows
/// The shortcuts are read as typed lines; the questions after Enter are drawn straight onto the
/// window like the startup picker, since the --launch window pipes the exe's output to the log.
/// </summary>
internal sealed class TestOverrides(IReadOnlyList<UsageSource> meters)
{
    internal const int MaxPercent = 999;

    private readonly ConcurrentDictionary<UsageSource, int?> _values = new();

    /// <summary>True while any meter is overridden.</summary>
    internal bool Active => !_values.IsEmpty;

    /// <summary>The value typed for this meter (null means "--"), if test mode overrides it.</summary>
    internal bool TryGet(UsageSource source, out int? value) => _values.TryGetValue(source, out value);

    /// <summary>Makes a meter show <paramref name="value"/> (null = "--") until the exe exits or it's set back to live.</summary>
    internal void Set(UsageSource source, int? value) => _values[source] = value;

    /// <summary>Applies one typed line and returns what to print (empty for a blank line).</summary>
    internal string Apply(string line)
    {
        var words = line.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (words)
        {
            case []:
                return "";

            case ["help" or "?" or "test"]:
                return Help();

            case ["live" or "clear" or "off"]:
                _values.Clear();
                return "Test mode off: every meter is live again.\n" + Status();

            case [var target, var value] when TryParseTargets(target, out var targets):
                if (value == "live")
                {
                    foreach (var source in targets)
                    {
                        _values.TryRemove(source, out _);
                    }
                }
                else if (TryParseValue(value, out var percent))
                {
                    foreach (var source in targets)
                    {
                        _values[source] = percent;
                    }
                }
                else
                {
                    return $"\"{value}\" isn't a value: use 0-{MaxPercent}, -- or live.";
                }

                return Status() + "\nPress the Photon's refresh button to fetch now.";

            default:
                return $"Didn't understand \"{line.Trim()}\".\n" + Help();
        }
    }

    internal string Help()
    {
        if (meters.Count == 0)
        {
            return "Test mode: no meters are selected, so there is nothing to set.";
        }

        return "Test mode: set what the LCD shows until this window closes (nothing is saved).\n" +
               Status() + "\n" +
               "Press Enter to be asked for every value again, or type a shortcut:\n" +
               $"  <n> <0-{MaxPercent}>   <n> --   <n> live   all <value>   live (all back to live)   help\n" +
               "Then press the Photon's refresh button to fetch now (otherwise it waits for its next refresh).";
    }

    /// <summary>
    /// Asks for every meter's value again, in LCD order. Each question shows what the meter shows now;
    /// Enter keeps it, a number or "--" sets it, and "live" returns the meter to its live value.
    /// </summary>
    internal void AskAgain(IAnsiConsole console)
    {
        console.MarkupLine($"[yellow]Change test values:[/] 0-{MaxPercent}, [blue]--[/] for unavailable or [blue]live[/]. " +
                           "Enter keeps the value in brackets.");
        foreach (var meter in meters)
        {
            var now = TryGet(meter, out var value) ? (value is { } p ? p.ToString(CultureInfo.InvariantCulture) : "--") : "live";
            var typed = console.Prompt(new TextPrompt<string>($"  {Markup.Escape(UsageSources.Label(meter))}")
                .DefaultValue(now)
                .Validate(t => IsLive(t) || TryParseValue(t.Trim(), out _)
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]Type 0-{MaxPercent}, -- or live[/]")));

            if (IsLive(typed))
            {
                _values.TryRemove(meter, out _);
            }
            else if (TryParseValue(typed.Trim(), out var percent))
            {
                _values[meter] = percent;
            }
        }
    }

    private static bool IsLive(string typed) => typed.Trim().Equals("live", StringComparison.OrdinalIgnoreCase);

    /// <summary>One line per selected meter: its number, name and what it shows.</summary>
    internal string Status()
    {
        var text = new StringBuilder();
        for (var i = 0; i < meters.Count; i++)
        {
            var shows = TryGet(meters[i], out var value)
                ? (value is { } p ? $"{p}%" : "--") + "  (test)"
                : "live";
            text.Append(CultureInfo.InvariantCulture, $"  {i + 1}  {UsageSources.Label(meters[i]),-24} {shows}");
            if (i < meters.Count - 1)
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads typed lines on a background thread until input ends; each reply is printed. Starts once
    /// the server is listening, so the hint appears after the startup log.
    /// </summary>
    internal void ListenOnConsole()
    {
        if (!Console.IsInputRedirected && meters.Count > 0)
        {
            Console.WriteLine("Test mode: press Enter here to set the LCD values by hand for this run (type 'help' for shortcuts).");
        }

        var reader = new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
            {
                if (line.Trim().Length == 0)
                {
                    AskAgainOnConsole();
                    continue;
                }

                var reply = Apply(line);
                if (reply.Length > 0)
                {
                    Console.WriteLine(reply);
                }
            }
        })
        {
            IsBackground = true, // never keeps the exe alive
            Name = "test mode console",
        };
        reader.Start();
    }

    /// <summary>Enter on an empty line: the questions, drawn on the window; then the result, to the window and the log.</summary>
    private void AskAgainOnConsole()
    {
        if (meters.Count == 0)
        {
            return;
        }

        using (var window = PickerConsole.Open(idleTimeout: null))
        {
            if (window is null)
            {
                return; // nobody can answer (input redirected or a hidden window)
            }

            try
            {
                AskAgain(window.Console);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Test mode: couldn't ask for values: {ex.Message}");
                return;
            }
        }

        Console.WriteLine(Status() + "\nPress the Photon's refresh button to fetch now.");
    }

    private bool TryParseTargets(string word, out IReadOnlyList<UsageSource> targets)
    {
        if (word is "all" or "*")
        {
            targets = meters;
            return meters.Count > 0;
        }

        if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number >= 1 && number <= meters.Count)
        {
            targets = [meters[number - 1]];
            return true;
        }

        targets = [];
        return false;
    }

    /// <summary>"42", "42%" (0-999) or "--"/"-" (null: shows "--"). Shared with the startup picker.</summary>
    internal static bool TryParseValue(string word, out int? percent)
    {
        percent = null;
        if (word is "--" or "-")
        {
            return true; // shows "--"
        }

        if (int.TryParse(word.TrimEnd('%'), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p <= MaxPercent)
        {
            percent = p;
            return true;
        }

        return false;
    }
}
