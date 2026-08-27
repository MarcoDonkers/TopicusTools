namespace ATFRerunTool.Orchestration;

/// <summary>
/// Keeps a single-line progress overview (passed/failed counts + a bar) pinned to the
/// bottom of the console while regular job/test output keeps scrolling above it.
///
/// All console output produced while a rerun session is active should go through
/// <see cref="WriteLine(string)"/> / <see cref="WriteLine(string, ConsoleColor)"/> instead of
/// calling <see cref="Console.WriteLine(string)"/> directly — that's what erases the footer
/// before printing the new line and redraws it afterwards, so parallel workers never tear
/// or duplicate it.
/// </summary>
public static class ConsoleProgress
{
    private static readonly object _lock = new();
    private static int _total;
    private static int _passed;
    private static int _failed;
    private static bool _active;
    private static bool _footerDrawn;

    /// <summary>Begins a session with the given total job count. Both Passed and Failed
    /// start at 0 — nothing has been attempted yet.</summary>
    public static void Start(int total)
    {
        lock (_lock)
        {
            _total = total;
            _passed = 0;
            _failed = 0;
            _active = total > 0;
            _footerDrawn = false;
            if (_active) Redraw();
        }
    }

    /// <summary>Call at the start of each retry round: jobs still in the queue are being
    /// re-attempted, so their previous "failed this round" count no longer applies. Passed
    /// is left untouched since jobs that went green stay green.</summary>
    public static void ResetRoundFailures()
    {
        lock (_lock)
        {
            _failed = 0;
            if (_active) Redraw();
        }
    }

    /// <summary>Marks one more job as having gone green (cumulative for the whole session).</summary>
    public static void MarkPassed()
    {
        lock (_lock)
        {
            _passed++;
            if (_active) Redraw();
        }
    }

    /// <summary>Marks one more job as failed for the current round.</summary>
    public static void MarkFailed()
    {
        lock (_lock)
        {
            _failed++;
            if (_active) Redraw();
        }
    }

    /// <summary>Ends the session and erases the footer line.</summary>
    public static void Stop()
    {
        lock (_lock)
        {
            if (_footerDrawn) ClearFooterLine();
            _active = false;
            _footerDrawn = false;
        }
    }

    public static void WriteLine(string message) => WriteLineCore(message, null);

    public static void WriteLine(string message, ConsoleColor color) => WriteLineCore(message, color);

    private static void WriteLineCore(string message, ConsoleColor? color)
    {
        lock (_lock)
        {
            if (_footerDrawn) ClearFooterLine();

            if (color is { } c) Console.ForegroundColor = c;
            Console.WriteLine(message);
            if (color is not null) Console.ResetColor();

            if (_active) Redraw();
        }
    }

    private static int SafeWidth()
    {
        try { return Math.Max(20, Console.WindowWidth - 1); }
        catch { return 100; }
    }

    private static void ClearFooterLine()
    {
        int width = SafeWidth();
        Console.Write("\r" + new string(' ', width) + "\r");
        _footerDrawn = false;
    }

    private static void Redraw()
    {
        int total = Math.Max(_total, 1);
        int done = Math.Clamp(_passed + _failed, 0, total);
        double pct = (double)done / total;

        const int barWidth = 30;
        int filled = Math.Clamp((int)Math.Round(barWidth * pct), 0, barWidth);
        var bar = "[" + new string('█', filled) + new string('░', barWidth - filled) + "]";

        var text = $"{bar} Passed: {_passed}/{_total}   Failed: {_failed}/{_total}";

        int width = SafeWidth();
        if (text.Length > width) text = text[..width];

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(text.PadRight(width));
        Console.ResetColor();
        _footerDrawn = true;
    }
}
