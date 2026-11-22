using System;
using System.Numerics;
using BeastsV3.Shared;
using Hexa.NET.ImGui;

namespace BeastsV3.Plugin.Settings.Menu;

// Whether this install has read and accepted the automation warning. Until then the Automation
// settings are hidden and no automation hotkey or button runs. Kept in BeastsV3_data.json, so it is
// asked once per install.
public static class AutomationConsent
{
    private const string Key = "automationWarningAcknowledged";

    private static bool? _acknowledged;

    public static bool Acknowledged => _acknowledged ??= PluginData.Get<DateTime?>(Key) != null;

    public static void Acknowledge()
    {
        PluginData.Set(Key, DateTime.UtcNow);
        _acknowledged = true;
        Log.Info("Automation warning acknowledged; Automation settings and hotkeys are unlocked.");
    }
}

// The one-time warning that unlocks Automation.
public static class AutomationWarning
{
    private static readonly Vector4 Blood = new(0.36f, 0.05f, 0.04f, 1f);
    private static readonly Vector4 Ember = new(0.92f, 0.30f, 0.16f, 1f);
    private static readonly Vector4 Ash = new(0.06f, 0.04f, 0.03f, 1f);

    private static bool _understood;

    // The one-time warning. Returns true on the frame it is accepted.
    public static bool DrawGate()
    {
        var width = ImGui.GetContentRegionAvail().X;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Ash);
        ImGui.PushStyleColor(ImGuiCol.Border, Ember);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 18f));
        ImGui.BeginChild("##bv3automationgate", new Vector2(width, 0f),
            ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding);

        ImGuiEx.PushFontScale(1.6f);
        ImGui.TextColored(Ember, "Abusing Automation can get you banned");
        ImGui.PopFont();
        ImGui.Spacing();

        Paragraph(MenuTheme.TextBright,
            "Itemizing, Faustus listing, restocking and loading the Map Device do dozens of clicks and key " +
            "presses for every one of yours. Used now and then while you play, that is fine. Abused, that is left " +
            "running unattended, looped for hours, or run faster than a human could, it looks like botting, " +
            "and Grinding Gear Games can ban your account for it. A ban can be permanent, and nothing in this " +
            "plugin can get your account back.");
        ImGui.Spacing();

        ImGui.TextColored(MenuTheme.Gold, "To keep it on the safe side:");
        Bullet("Stay at the keyboard. Never let a run go while you are away.");
        Bullet("One press, one task. Do not chain runs for hours.");
        Bullet("Bind Panic Stop first, so you can end any run at once.");
        Bullet("Turn on Humanization (Automation: Timing) for uneven delays, curved cursor moves and " +
               "off-centre clicks. To be honest, don't expect too much from it.");
        Bullet("Everything else in the plugin (prices, labels, overlays, analytics) is not affected.");
        ImGui.Spacing();

        Paragraph(MenuTheme.Muted,
            "The Automation settings and every automation hotkey and button stay switched off until you " +
            "accept below. You are asked once.");
        ImGui.Dummy(new Vector2(0f, 8f));

        ImGui.Checkbox("I understand abusing Automation can get my account banned. I use it at my own risk.", ref _understood);
        ImGui.Dummy(new Vector2(0f, 4f));

        var accepted = false;
        if (!_understood) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, Blood);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Ember);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Ember);
        ImGuiEx.PushFontScale(1.25f);
        if (ImGui.Button("Unlock Automation##bv3gate", new Vector2(-1f, 0f)))
        {
            AutomationConsent.Acknowledge();
            accepted = true;
        }
        ImGui.PopFont();
        ImGui.PopStyleColor(3);
        if (!_understood) ImGui.EndDisabled();

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        return accepted;
    }

    private static void Paragraph(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    private static void Bullet(string text)
    {
        ImGui.TextColored(Ember, "  >");
        ImGui.SameLine();
        Paragraph(MenuTheme.Text, text);
    }
}
