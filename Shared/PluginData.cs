using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace BeastsV3.Shared;

// The one file for what the plugin remembers that is not a setting: config/global/BeastsV3_data.json.
// Each value sits under its own key. Settings themselves are saved by the host in
// BeastsV3.Plugin_settings.json.
public static class PluginData
{
    private const string FileName = "BeastsV3_data.json";

    private static readonly object Gate = new();
    private static JObject _root;

    public static T Get<T>(string key)
    {
        lock (Gate)
        {
            try
            {
                var value = Root()[key];
                return value == null || value.Type == JTokenType.Null ? default : value.ToObject<T>();
            }
            catch (Exception ex)
            {
                Log.Debug($"Could not read '{key}' from {FileName}: {ex.Message}");
                return default;
            }
        }
    }

    public static void Set(string key, object value)
    {
        lock (Gate)
        {
            try
            {
                Root()[key] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
                Write();
            }
            catch (Exception ex)
            {
                Log.Debug($"Could not write '{key}' to {FileName}: {ex.Message}");
            }
        }
    }

    private static string GlobalPath(string name) =>
        Path.Combine(Directory.GetCurrentDirectory(), "config", "global", name);

    private static JObject Root()
    {
        if (_root != null) return _root;

        var path = GlobalPath(FileName);
        if (File.Exists(path))
        {
            try { return _root = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { Log.Debug($"{FileName} is unreadable and starts empty: {ex.Message}"); }
        }

        _root = new JObject();
        ImportOldFiles();
        return _root;
    }

    private static void Write()
    {
        var path = GlobalPath(FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, _root.ToString());
    }

    // Before this file existed each value had a file of its own. They are taken over once and
    // removed.
    private static void ImportOldFiles()
    {
        try
        {
            var installId = GlobalPath("BeastsV3_installId.txt");
            if (File.Exists(installId))
            {
                _root["installId"] = File.ReadAllText(installId).Trim();
                File.Delete(installId);
            }

            var possibleBeasts = GlobalPath("BeastsV3_possibleBeasts.json");
            if (File.Exists(possibleBeasts))
            {
                _root["possibleBeasts"] = JObject.Parse(File.ReadAllText(possibleBeasts));
                File.Delete(possibleBeasts);
            }

            if (_root.Count > 0) Write();
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not take over the old data files: {ex.Message}");
        }
    }
}
