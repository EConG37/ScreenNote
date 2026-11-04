using System;
using ScreenNote;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        var elevated = ElevationService.IsCurrentProcessElevated();
        Require(elevated == ElevationService.IsCurrentProcessElevated(), "Current-process elevation is stable across calls.");

        Require(!ElevationService.IsProcessElevated((uint)Environment.ProcessId, out var selfName) && selfName.Length > 0,
            "Own process always opens with full query access, so it never reports elevated: " + selfName);
        Require(!ElevationService.IsProcessElevated(0, out var invalidName) && invalidName.Length == 0,
            "An invalid process id reports not elevated without a name.");

        try { ElevationService.IsForegroundProcessElevated(out _); }
        catch (Exception ex) { throw new InvalidOperationException("Foreground probe must not throw.", ex); }
        _checks++; Console.WriteLine("PASS: Foreground elevation probe completes without throwing.");

        using (var watcher = new ForegroundElevationWatcher(_ => { }))
        {
            watcher.CheckNow();
            _checks++; Console.WriteLine("PASS: Watcher installs and performs an immediate foreground check.");
        }
        var second = new ForegroundElevationWatcher(_ => { });
        second.Dispose();
        second.Dispose();
        _checks++; Console.WriteLine("PASS: Watcher disposal is idempotent.");
        var third = new ForegroundElevationWatcher(_ => { });
        third.Dispose();
        third.CheckNow();
        _checks++; Console.WriteLine("PASS: Disposed watcher ignores later checks without throwing.");

        // RestartElevated is intentionally not exercised: it shows a UAC prompt on success.
        Console.WriteLine($"PASS: {_checks} elevation checks, using real process handles and WinEvent hooks.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS: " + message);
    }
}
