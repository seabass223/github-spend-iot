using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Spectre.Console;
using Spectre.Console.Rendering;

/// <summary>
/// Where the startup picker can ask, or nothing when nobody could answer it.
///
/// In a normal terminal that's the console itself. Under ./build.sh --launch the window is real and
/// its keyboard reaches this exe, but stdout is piped through PowerShell to copy it to the log, so
/// Spectre sees a redirected output and won't prompt. There the picker draws straight onto the
/// console window (CONOUT$) instead; only the result goes to stdout, and so to the log.
/// A hidden launch has a console too, but an invisible one: asking there would wait forever.
///
/// With an idle timeout (the startup picker), the keyboard is wrapped so that if nobody uses the picker
/// within it, Enter is pressed for you and the ticked meters are kept: an unattended start still serves
/// the LCD. Test values asked for while the server runs have no timeout.
/// </summary>
internal sealed class PickerConsole : IDisposable
{
    private readonly Action? _close;

    private PickerConsole(IAnsiConsole console, Action? close, TimeSpan? idleTimeout)
    {
        Console = idleTimeout is { } timeout
            ? new ConsoleWithInput(console, new EnterWhenIdleInput(console.Input, timeout))
            : console;
        _close = close;
    }

    internal IAnsiConsole Console { get; }

    public void Dispose() => _close?.Invoke();

    /// <summary>The console to ask on, or null when nobody could answer.</summary>
    internal static PickerConsole? Open(TimeSpan? idleTimeout)
    {
        if (!Environment.UserInteractive || System.Console.IsInputRedirected)
        {
            return null; // no keyboard to answer with
        }

        if (!System.Console.IsOutputRedirected)
        {
            return new PickerConsole(AnsiConsole.Console, close: null, idleTimeout); // an ordinary terminal
        }

        return OperatingSystem.IsWindows() ? OpenConsoleWindow(idleTimeout) : null;
    }

    [SupportedOSPlatform("windows")]
    private static PickerConsole? OpenConsoleWindow(TimeSpan? idleTimeout)
    {
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window))
        {
            return null; // a hidden launch: nobody can see the question
        }

        var handle = CreateFileW("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        // Spectre redraws the list with ANSI cursor moves, so the window must interpret them.
        if (!GetConsoleMode(handle, out var originalMode) ||
            !SetConsoleMode(handle, originalMode | EnableProcessedOutput | EnableVirtualTerminalProcessing))
        {
            handle.Dispose();
            return null;
        }

        try
        {
            var writer = new StreamWriter(new FileStream(handle, FileAccess.Write, bufferSize: 0), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Yes,
                ColorSystem = ColorSystemSupport.Standard,
                Interactive = InteractionSupport.Yes,
                Out = new AnsiConsoleOutput(writer),
            });
            console.Profile.Width = 80;                  // the window's width can't be read through the pipe
            console.Profile.Capabilities.Unicode = false; // plain ASCII whatever the window's code page

            return new PickerConsole(console, () =>
            {
                SetConsoleMode(handle, originalMode);
                writer.Dispose(); // closes the handle too
            }, idleTimeout);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            SetConsoleMode(handle, originalMode);
            handle.Dispose();
            return null;
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint EnableProcessedOutput = 0x1;
    private const uint EnableVirtualTerminalProcessing = 0x4;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(SafeFileHandle handle, uint mode);
}

/// <summary>A console that reads its keys from <paramref name="input"/> and does everything else through <paramref name="inner"/>.</summary>
internal sealed class ConsoleWithInput(IAnsiConsole inner, IAnsiConsoleInput input) : IAnsiConsole
{
    public Profile Profile => inner.Profile;
    public IAnsiConsoleCursor Cursor => inner.Cursor;
    public IAnsiConsoleInput Input => input;
    public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;
    public RenderPipeline Pipeline => inner.Pipeline;
    public void Clear(bool home) => inner.Clear(home);
    public void Write(IRenderable renderable) => inner.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action) => inner.WriteAnsi(action);
}

/// <summary>
/// Keyboard input that presses Enter by itself if nobody uses the picker before the timeout. The first
/// picker key (a move, Space or Enter) turns the timer off for good: someone is choosing. Other keys
/// are passed on (the picker ignores them) but leave the timer running, because the launch window
/// takes focus as it opens and can catch keystrokes meant for another window.
/// </summary>
internal sealed class EnterWhenIdleInput(IAnsiConsoleInput inner, TimeSpan timeout) : IAnsiConsoleInput
{
    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    private readonly DateTime _deadline = DateTime.UtcNow + timeout;
    private bool _touched;

    /// <summary>The keys the checkbox list acts on.</summary>
    private static readonly HashSet<ConsoleKey> PickerKeys =
    [
        ConsoleKey.UpArrow, ConsoleKey.DownArrow, ConsoleKey.Home, ConsoleKey.End,
        ConsoleKey.PageUp, ConsoleKey.PageDown, ConsoleKey.Spacebar, ConsoleKey.Enter,
    ];

    private bool TimedOut => !_touched && DateTime.UtcNow >= _deadline;

    public bool IsKeyAvailable() => inner.IsKeyAvailable() || TimedOut;

    public ConsoleKeyInfo? ReadKey(bool intercept)
    {
        while (!_touched && !inner.IsKeyAvailable())
        {
            if (TimedOut)
            {
                return Enter;
            }

            Thread.Sleep(Poll);
        }

        return Seen(inner.ReadKey(intercept));
    }

    public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
    {
        while (!_touched && !inner.IsKeyAvailable())
        {
            if (TimedOut)
            {
                return Enter;
            }

            await Task.Delay(Poll, cancellationToken);
        }

        return Seen(await inner.ReadKeyAsync(intercept, cancellationToken));
    }

    private ConsoleKeyInfo? Seen(ConsoleKeyInfo? key)
    {
        if (key is { } k && PickerKeys.Contains(k.Key))
        {
            _touched = true;
        }

        return key;
    }
}
