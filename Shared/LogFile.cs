using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeastsV3.Shared;

// Append-only session log. Write() enqueues; a background task batches to disk.
// Writes config/BeastsV3Logs/BeastsV3.log, rotating the last session to BeastsV3.prev.log.
public sealed class LogFile : IDisposable
{
    public const string LogFileName = "BeastsV3.log";
    public const string PreviousLogFileName = "BeastsV3.prev.log";

    public const long DefaultMaxBytes = 8L * 1024L * 1024L;
    private const long MinMaxBytes = 256L * 1024L;

    // Queue cap; lines past it are dropped and counted.
    private const int MaxQueuedLines = 20_000;

    // Max delay between disk writes while lines keep arriving.
    private const int FlushIntervalMs = 250;

    private readonly BlockingCollection<string> _queue =
        new(new ConcurrentQueue<string>(), MaxQueuedLines);

    private readonly Task _writerTask;
    private readonly string _path;
    private readonly string _previousPath;
    private readonly long _maxBytes;

    // _pendingDrops resets each time a notice is written; _totalDrops is the session total.
    private long _pendingDrops;
    private long _totalDrops;

    private long _approxBytesWritten;
    private bool _rollOverDisabled;
    private int _disposed;

    public LogFile(string directory = null, long maxBytes = DefaultMaxBytes)
    {
        var dir = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Directory.GetCurrentDirectory(), "config", "BeastsV3Logs")
            : directory;

        _path = Path.Combine(dir, LogFileName);
        _previousPath = Path.Combine(dir, PreviousLogFileName);
        _maxBytes = Math.Max(MinMaxBytes, maxBytes);

        try { Directory.CreateDirectory(dir); } catch { }
        RotateForNewSession();

        _writerTask = Task.Factory.StartNew(
            DrainLoop,
            CancellationToken.None,
            // Runs on a dedicated thread, not the thread pool.
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public string FilePath => _path;

    public string DirectoryPath => Path.GetDirectoryName(_path);

    // Lines lost to backpressure this session.
    public long DroppedLines => Interlocked.Read(ref _totalDrops);

    // Timestamps the line and enqueues it. Thread-safe, non-blocking, never throws.
    public void Write(string level, string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var line = string.Concat(
            DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            " [", level, "] ",
            message ?? string.Empty);

        try
        {
            // TryAdd rather than Add so a full queue drops instead of blocking.
            if (!_queue.TryAdd(line))
                CountDrop();
        }
        catch (InvalidOperationException)
        {
            // Queue was completed by Dispose; line is discarded.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Lets the drain loop write what is already queued, waiting at most 2s.
        try { _queue.CompleteAdding(); } catch { }
        try { _writerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _queue.Dispose(); } catch { }
    }

    private void CountDrop()
    {
        Interlocked.Increment(ref _pendingDrops);
        Interlocked.Increment(ref _totalDrops);
    }

    // ---- writer thread -------------------------------------------------

    // Consumes the queue, batching lines and flushing them to the file.
    private void DrainLoop()
    {
        StreamWriter writer = null;
        try
        {
            writer = OpenWriter();

            var pending = new StringBuilder();
            var lastFlush = Environment.TickCount64;

            // Blocks until a line arrives; ends on CompleteAdding.
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                pending.AppendLine(line);

                // Keep batching unless the queue is empty or the flush interval elapsed.
                if (_queue.Count > 0 && Environment.TickCount64 - lastFlush < FlushIntervalMs)
                    continue;

                FlushBatch(ref writer, pending);
                lastFlush = Environment.TickCount64;
            }

            FlushBatch(ref writer, pending);
            ReportDrops(writer);
        }
        catch
        {
            // Swallowed; the writer thread has nowhere to report.
        }
        finally
        {
            try { writer?.Flush(); } catch { }
            try { writer?.Dispose(); } catch { }
        }
    }

    // Writes the pending buffer, updates the size estimate and rotates when over the cap.
    private void FlushBatch(ref StreamWriter writer, StringBuilder pending)
    {
        if (pending.Length == 0) return;

        var text = pending.ToString();
        pending.Clear();

        if (writer == null) return;

        try
        {
            writer.Write(text);
            writer.Flush();

            // Approximates bytes with the char count.
            _approxBytesWritten += text.Length;

            ReportDrops(writer);

            if (!_rollOverDisabled && _approxBytesWritten >= _maxBytes)
                writer = RollOver(writer);
        }
        catch
        {
            // Write failed; the batch is dropped and the loop continues.
        }
    }

    // Appends a warning line for any lines dropped since the last report.
    private void ReportDrops(StreamWriter writer)
    {
        if (writer == null) return;

        var dropped = Interlocked.Exchange(ref _pendingDrops, 0);
        if (dropped <= 0) return;

        try
        {
            writer.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff} [WARN] Log backpressure: {dropped} line(s) dropped. " +
                "Logging outran the disk, so the trace above has a hole in it.");
            writer.Flush();
        }
        catch { }
    }

    // Rotates the file mid-session once the size cap is hit and reopens a new writer.
    private StreamWriter RollOver(StreamWriter writer)
    {
        try { writer.Flush(); } catch { }
        try { writer.Dispose(); } catch { }

        MovePreviousAside();
        _approxBytesWritten = CurrentFileLength();

        // Rotation failed (file still over the cap); disable further attempts.
        if (_approxBytesWritten >= _maxBytes) _rollOverDisabled = true;

        var rolled = OpenWriter();
        try
        {
            rolled?.WriteLine(_rollOverDisabled
                ? $"=== Beasts V3 continued {DateTime.Now:yyyy-MM-dd HH:mm:ss} (size cap reached, rotation unavailable) ==="
                : $"=== Beasts V3 continued {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                  $"(size cap {_maxBytes / (1024 * 1024)} MB reached; earlier lines in {PreviousLogFileName}) ===");
            rolled?.Flush();
        }
        catch { }

        return rolled;
    }

    // Opens the log for append, or returns null when the file can't be opened.
    private StreamWriter OpenWriter()
    {
        try
        {
            // ReadWrite sharing so the file can be tailed or opened while in use.
            var stream = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 4096);

            return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
        }
        catch
        {
            // No file this session; logging becomes a no-op.
            return null;
        }
    }

    // Rotates the previous log aside and appends a session header. Called from the ctor.
    private void RotateForNewSession()
    {
        MovePreviousAside();

        // Appends rather than truncates, so a failed rotation doesn't lose the old session.
        try
        {
            File.AppendAllText(
                _path,
                $"=== Beasts V3 session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch { }

        // Seeds the size estimate from the file already on disk.
        _approxBytesWritten = CurrentFileLength();
    }

    private long CurrentFileLength()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    // Renames the current log to .prev.log, replacing any existing one.
    private void MovePreviousAside()
    {
        try
        {
            if (!File.Exists(_path)) return;

            if (File.Exists(_previousPath)) File.Delete(_previousPath);
            File.Move(_path, _previousPath);
        }
        catch
        {
            // Move failed; the existing file stays in place and is appended to.
        }
    }
}
