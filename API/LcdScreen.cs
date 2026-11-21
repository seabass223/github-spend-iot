using System.Text;

/// <summary>
/// Builds the exact 32 characters for the 16x2 LCD (line 1 = chars 0-15, line 2 = 16-31).
///
/// The text uses the LCD's own character set, not Unicode: plain ASCII, plus the Photon's
/// custom glyphs at codes 0x08-0x0F (the LCD mirrors its 8 custom characters 0-7 there,
/// which avoids sending a 0 byte). Send it as Latin-1 so each char becomes exactly one byte.
/// </summary>
internal static class LcdScreen
{
    internal const int Width = 16;
    internal const int Length = Width * 2;

    /// <summary>
    /// Response header with the seconds the Photon waits before its next fetch. The firmware matches
    /// the name without regard to case and keeps its previous interval if the header is missing.
    /// </summary>
    internal const string RefreshHeader = "X-Refresh-Seconds";

    // Must match the glyph slots loaded by the Photon sketch (slot n is shown at code 8 + n).
    internal const char Claude = '\x08';
    internal const char GitHubCat = '\x09';
    internal const char ElevenLabs = '\x0A';
    internal const char OpenAi = '\x0B';

    /// <summary>
    /// The selected meters (at most four) pack in menu order from the top-left, two per line:
    ///   {CC} 83%  {OAI} 30%
    ///   {GH} 73%  {11} 12%
    /// Each cell is "{icon} 000% ": the glyph, a space, the percent left in four characters
    /// (left-aligned, up to "999%") and a trailing space, seven characters in all.
    /// A selected meter with no data shows "--". The values dictionary holds only selected meters.
    /// Work and personal versions of the same service share a glyph, so they are told apart by order.
    /// </summary>
    internal static string RenderUsage(IReadOnlyDictionary<UsageSource, int?> values)
    {
        var cells = UsageSources.All
            .Where(values.ContainsKey)
            .Take(PerLine * 2)
            .Select(s => Cell(s, values[s]))
            .ToList();

        var lines = new[] { cells.Take(PerLine), cells.Skip(PerLine).Take(PerLine) };
        return ReserveIndicatorCell(string.Concat(lines.Select(line => Line(string.Concat(line)))));
    }

    internal const int PerLine = 2;
    internal const int CellWidth = 7;

    private static char GlyphFor(UsageSource source) => source switch
    {
        UsageSource.WorkClaudeCode or UsageSource.PersonalClaudeCode => Claude,
        UsageSource.WorkGitHubCopilot or UsageSource.PersonalGitHubCopilot => GitHubCat,
        UsageSource.ChatGpt => OpenAi,
        UsageSource.ElevenLabs => ElevenLabs,
        _ => ' ',
    };

    private static string Cell(UsageSource source, int? percent)
    {
        var value = percent is { } p ? $"{Math.Clamp(p, 0, 999)}%" : "--";
        return $"{GlyphFor(source)} {value,-4} ";
    }

    /// <summary>
    /// The last cell of line 2 belongs to the Photon's refresh indicator (spinner, "!", thumbs up),
    /// so every screen leaves it blank.
    /// </summary>
    internal const int IndicatorIndex = Length - 1;

    internal static string ReserveIndicatorCell(string screen) => screen[..IndicatorIndex] + " ";

    /// <summary>Pads or cuts to exactly one 16-char line.</summary>
    internal static string Line(string text) =>
        text.Length >= Width ? text[..Width] : text.PadRight(Width);

    internal static byte[] ToBytes(string screen)
    {
        if (screen.Length != Length)
        {
            throw new ArgumentException($"LCD screen must be exactly {Length} characters, got {screen.Length}.", nameof(screen));
        }

        return Encoding.Latin1.GetBytes(screen);
    }
}
