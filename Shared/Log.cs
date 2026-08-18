using System;
using System.Threading.Tasks;

namespace BeastsV3.Shared;

// Static logger bridge to the host console and an optional LogFile.
// The file gets everything; the console gets errors, plus Debug and Info when verbose.
public static class Log
{
    private static Action<string> _debug = _ => { };
    private static Action<string, Exception> _error = (_, _) => { };
    private static Func<bool> _verbose = () => false;
    private static LogFile _file;

    // Wires up the sinks; `file` may be null.
    public static void Attach(Action<string> debug, Action<string, Exception> error, Func<bool> isVerbose,
        LogFile file = null)
    {
        _debug = debug ?? (_ => { });
        _error = error ?? ((_, _) => { });
        _verbose = isVerbose ?? (() => false);
        _file = file;
    }

    public static void Detach()
    {
        _debug = _ => { };
        _error = (_, _) => { };
        _verbose = () => false;
        _file = null;
    }

    // Step-by-step detail. File always, console only when verbose.
    public static void Debug(string message)
    {
        _file?.Write("DEBUG", message);
        if (_verbose()) _debug(message);
    }

    // Notable one-off events. File always, console only when verbose.
    public static void Info(string message)
    {
        _file?.Write("INFO", message);
        if (_verbose()) _debug(message);
    }

    // A recoverable problem. File always, console only when verbose.
    public static void Warn(string message)
    {
        _file?.Write("WARN", message);
        if (_verbose()) _debug($"WARN: {message}");
    }

    // A failure. Always reaches both sinks.
    public static void Error(string message, Exception ex = null)
    {
        _file?.Write("ERROR", ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

        // Stack traces go to the file only.
        if (ex?.StackTrace != null) _file?.Write("ERROR", ex.StackTrace);

        _error(message, ex);
    }

    // Writes a block of related lines to the file under one heading.
    public static void Section(string title, params string[] lines)
    {
        _file?.Write("INFO", $"--- {title} ---");
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line)) _file?.Write("INFO", $"  {line}");
        }
    }

    // Runs a detached task and logs anything that escapes it.
    public static void FireAndForget(Func<Task> work, string label)
    {
        if (work == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Debug($"{label} cancelled.");
            }
            catch (Exception ex)
            {
                Error($"{label} failed", ex);
            }
        });
    }
}
