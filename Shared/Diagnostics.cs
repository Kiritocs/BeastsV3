using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BeastsV3.Plugin.Settings;
using ExileCore;

namespace BeastsV3.Shared;

// Writes environment, settings and live-state blocks to the log file.
public static class Diagnostics
{
    // ---- startup ------------------------------------------------------

    // Writes the environment block: plugin, runtime, ExileCore, window, league and area.
    public static void LogSessionHeader(GameController game)
    {
        var lines = new List<string>
        {
            $"Plugin      : Beasts V3 {PluginVersion()}",
            $"Runtime     : {Environment.Version} on {RuntimeInformationOs()}",
            $"Process     : {(Environment.Is64BitProcess ? "x64" : "x86")}, {Environment.ProcessorCount} cores",
            $"Culture     : {CultureInfo.CurrentCulture.Name} (UI {CultureInfo.CurrentUICulture.Name})",
            $"ExileCore   : {AssemblyVersion("ExileCore")}",
            $"Window      : {WindowSize(game)}",
            $"League      : {Describe(GameHelpers.TryGetServerLeague(game))}",
            $"Area        : {DescribeArea(game)}",
        };

        Log.Section("Environment", lines.ToArray());
    }

    // Writes every setting that differs from a fresh install.
    public static void LogNonDefaultSettings(BeastsSettings settings)
    {
        if (settings == null) return;

        try
        {
            var defaults = new BeastsSettings();
            var diffs = new List<string>();
            CollectDifferences(settings, defaults, string.Empty, diffs, depth: 0);

            if (diffs.Count == 0)
            {
                Log.Section("Settings", "All settings are at their defaults.");
                return;
            }

            diffs.Sort(StringComparer.OrdinalIgnoreCase);
            Log.Section($"Settings ({diffs.Count} changed from default)", diffs.ToArray());
        }
        catch (Exception ex)
        {
            // Reflection failures must not take down startup.
            Log.Warn($"Could not dump settings: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- on demand ----------------------------------------------------

    // Writes a snapshot of live state, triggered by the diagnostics hotkey or button.
    public static void DumpSnapshot(GameController game, BeastsSettings settings, params (string Label, string Value)[] extra)
    {
        try
        {
            Log.Section("DIAGNOSTIC SNAPSHOT", $"Requested at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            LogSessionHeader(game);
            LogNonDefaultSettings(settings);

            Log.Section("Game state",
                $"Area          : {DescribeArea(game)}",
                $"In town/hideout: {GameHelpers.IsTownOrHideout(game?.Area?.CurrentArea)}",
                $"Runnable map  : {GameHelpers.IsRunnableMap(game?.Area?.CurrentArea)}",
                $"Escape state  : {game?.Game?.IsEscapeState}",
                $"Latency       : {game?.Game?.IngameState?.ServerData?.Latency}");

            Log.Section("UI reachability", DescribeUiPanels(game));

            if (extra != null && extra.Length > 0)
            {
                Log.Section("Live state", extra
                    .Where(x => !string.IsNullOrWhiteSpace(x.Label))
                    .Select(x => $"{x.Label,-22}: {x.Value}")
                    .ToArray());
            }

            Log.Section("END SNAPSHOT");
        }
        catch (Exception ex)
        {
            Log.Error("Diagnostic snapshot failed", ex);
        }
    }

    // Reports which game UI panels are currently resolvable and visible.
    private static string[] DescribeUiPanels(GameController game)
    {
        var ui = game?.IngameState?.IngameUi;
        if (ui == null) return ["IngameUi is null."];

        return
        [
            $"StashElement   : {DescribeVisible(() => ui.StashElement?.IsVisible)}",
            $"VisibleStash   : {Describe(TryGet(() => ui.StashElement?.VisibleStash?.InvType.ToString()))}",
            $"InventoryPanel : {DescribeVisible(() => ui.InventoryPanel?.IsVisible)}",
            $"MapDeviceWindow: {DescribeVisible(() => ui.MapDeviceWindow?.IsVisible)}",
            $"Atlas          : {DescribeVisible(() => ui.Atlas?.IsVisible)}",
            $"QuestTracker   : {DescribeVisible(() => ui.QuestTracker?.IsVisible)}",
            $"OpenLeftPanel  : {DescribeVisible(() => ui.OpenLeftPanel?.IsVisible)}",
            $"OpenRightPanel : {DescribeVisible(() => ui.OpenRightPanel?.IsVisible)}",
            $"LargeMap       : {DescribeVisible(() => ui.Map?.LargeMap?.IsVisible)}",
        ];
    }

    // ---- settings reflection -------------------------------------------

    private const int MaxSettingsDepth = 4;

    private static void CollectDifferences(object actual, object baseline, string prefix, List<string> diffs, int depth)
    {
        if (actual == null || baseline == null || depth > MaxSettingsDepth) return;

        foreach (var property in actual.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            if (!property.CanRead) continue;

            object actualValue, baselineValue;
            try
            {
                actualValue = property.GetValue(actual);
                baselineValue = property.GetValue(baseline);
            }
            catch
            {
                continue;
            }

            if (actualValue == null) continue;

            var name = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";

            // Setting nodes are compared by their Value.
            var actualNode = TryReadNodeValue(actualValue);
            if (actualNode != null)
            {
                var baselineNode = TryReadNodeValue(baselineValue);
                if (!string.Equals(actualNode, baselineNode, StringComparison.Ordinal))
                    diffs.Add($"{name} = {actualNode}   (default {Describe(baselineNode)})");
                continue;
            }

            // String collections are reported by count and contents.
            //
            // Ordered lists are printed in their own order, never sorted. Sorting them once
            // reported a tab chain of "Beasts, Beasts2" for a config that actually filled
            // Beasts2 first — the order IS the setting for those, and a log that quietly
            // reorders it sends you looking for a bug that isn't there. Sets have no
            // meaningful order, so those are sorted to keep the diff stable between runs.
            if (actualValue is ICollection<string> set)
            {
                var baselineCount = (baselineValue as ICollection<string>)?.Count ?? 0;
                if (set.Count == baselineCount && set.Count == 0) continue;

                IEnumerable<string> ordered = set;
                if (actualValue is not IList<string>)
                    ordered = set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

                diffs.Add($"{name} = {set.Count} item(s): {string.Join(", ", ordered)}");
                continue;
            }

            // Recurses into nested settings classes only, never into ExileCore's types.
            var type = actualValue.GetType();
            if (type.IsClass && type.Namespace?.StartsWith("BeastsV3", StringComparison.Ordinal) == true)
            {
                CollectDifferences(actualValue, baselineValue, name, diffs, depth + 1);
            }
        }
    }

    // Returns the node's Value as a string, or null when this isn't a settings node.
    private static string TryReadNodeValue(object node)
    {
        if (node == null) return null;

        var type = node.GetType();
        if (type.Namespace?.StartsWith("ExileCore", StringComparison.Ordinal) != true) return null;

        var valueProperty = type.GetProperty("Value");
        if (valueProperty == null) return null;

        try
        {
            var value = valueProperty.GetValue(node);
            return value switch
            {
                null => "null",
                float f => f.ToString("0.###", CultureInfo.InvariantCulture),
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    // ---- small helpers -------------------------------------------------

    private static string PluginVersion()
    {
        var assembly = typeof(Diagnostics).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return !string.IsNullOrWhiteSpace(informational)
            ? informational
            : assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static string AssemblyVersion(string simpleName)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            return assembly?.GetName().Version?.ToString() ?? "not loaded";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string RuntimeInformationOs()
    {
        try { return Environment.OSVersion.VersionString; }
        catch { return "unknown OS"; }
    }

    private static string WindowSize(GameController game)
    {
        try
        {
            var rect = game?.Window?.GetWindowRectangle() ?? default;
            return rect.Width > 0 ? $"{rect.Width:0}x{rect.Height:0}" : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string DescribeArea(GameController game)
    {
        var area = game?.Area?.CurrentArea;
        if (area == null) return "none";

        var name = GameHelpers.TryGetAreaName(area);
        var hash = GameHelpers.TryGetAreaHashText(area);
        var instance = GameHelpers.TryGetAreaInstanceId(area);
        return $"'{name}' hash={Describe(hash)} instance={instance}";
    }

    private static string DescribeVisible(Func<bool?> read)
    {
        try
        {
            var visible = read();
            return visible switch { null => "null", true => "visible", _ => "hidden" };
        }
        catch (Exception ex)
        {
            return $"error({ex.GetType().Name})";
        }
    }

    private static string TryGet(Func<string> read)
    {
        try { return read(); }
        catch { return null; }
    }

    private static string Describe(string value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
}
