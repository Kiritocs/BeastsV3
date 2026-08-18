using System;
using System.IO;
using BeastsV3.Shared;
using ImGuiNET;

namespace BeastsV3.Plugin.Settings;

// Draws the session log's status in the settings menu: path, size and dropped-line count.
public sealed class LogFilePanel
{
    private readonly BeastsSettings _settings;
    private readonly Func<LogFile> _logFile;

    public LogFilePanel(BeastsSettings settings, Func<LogFile> logFile)
    {
        _settings = settings;
        _logFile = logFile;
    }

    public void Draw()
    {
        var file = _logFile?.Invoke();

        if (file == null)
        {
            ImGui.TextDisabled(_settings.LogFile.Enabled.Value
                ? "Log file is enabled but not open yet. Reload the plugin to start it."
                : "Log file is off. Enable it above, then reload the plugin.");
            return;
        }

        ImGui.TextDisabled(file.FilePath);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy path##logfile"))
        {
            try { ImGui.SetClipboardText(file.FilePath); }
            catch (Exception ex) { Log.Error("Could not copy the log path to the clipboard", ex); }
        }

        DrawSizeLine("Current", file.FilePath);
        DrawSizeLine("Previous", Path.Combine(file.DirectoryPath ?? string.Empty, LogFile.PreviousLogFileName));

        var dropped = file.DroppedLines;
        if (dropped > 0)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.55f, 0.2f, 1f),
                $"{dropped} line{ImGuiEx.PluralSuffix((int)Math.Min(dropped, int.MaxValue))} dropped - this log is incomplete.");
            ImGui.TextDisabled("Logging outran the disk. The gaps are marked inline in the file.");
        }
    }

    private static void DrawSizeLine(string label, string path)
    {
        try
        {
            var info = new FileInfo(path);
            ImGui.TextDisabled(info.Exists
                ? $"{label}: {FormatSize(info.Length)}  (written {info.LastWriteTime:HH:mm:ss})"
                : $"{label}: not present");
        }
        catch
        {
            // Size could not be read.
            ImGui.TextDisabled($"{label}: unreadable");
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024L ? $"{bytes / (1024f * 1024f):0.0} MB"
        : bytes >= 1024L ? $"{bytes / 1024f:0.0} KB"
        : $"{bytes} B";
}
