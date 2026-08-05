using System;
using System.Numerics;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;
using ImGuiNET;

namespace BeastsV3.Automation.Workflows;

// Floating ImGui buttons drawn beside the Bestiary panel and the Menagerie inventory.
public sealed class QuickButtons
{
    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly Runner _runner;
    private readonly BestiaryUi _bestiary;
    private readonly MenagerieRightClick _menagerieRightClick;

    // Wired by BeastsPlugin; the bestiary buttons stay hidden while null.
    public Action StartItemizeAll { get; set; }
    public Action StartDeleteAll { get; set; }

    public QuickButtons(GameController game, BeastsSettings settings, Runner runner,
        BestiaryUi bestiary, MenagerieRightClick menagerieRightClick)
    {
        _game = game;
        _settings = settings;
        _runner = runner;
        _bestiary = bestiary;
        _menagerieRightClick = menagerieRightClick;
    }

    public void Render()
    {
        DrawBestiaryButtons();
        DrawInventoryButton();
    }

    // ---- private -------------------------------------------------------

    // Draws the Itemize All / Delete All / Stop window next to the Bestiary panel.
    private void DrawBestiaryButtons()
    {
        if (!_settings.BestiaryAutomation.ShowBestiaryButtons.Value) return;
        if (!_bestiary.IsCapturedBeastsTabOpen) return;

        var strip = _bestiary.SubTabStrip;
        if (strip?.IsVisible != true) return;

        var rect = strip.GetClientRect();
        if (rect.Width <= 0 || rect.Height <= 0) return;

        ImGui.SetNextWindowPos(new Vector2(rect.Right + 8f, rect.Top), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.9f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
                                       ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##BeastsV3BestiaryQuickButtons", flags)) { ImGui.End(); return; }

        if (_runner.IsRunning)
        {
            ImGui.TextDisabled("Automation running...");
            if (ImGui.Button("Stop##BeastsV3Bestiary")) _runner.RequestStop();
        }
        else
        {
            if (StartItemizeAll != null && ImGui.Button("Itemize All##BeastsV3Bestiary")) StartItemizeAll();
            if (StartDeleteAll != null && ImGui.Button("Delete All##BeastsV3Bestiary")) StartDeleteAll();

            if (StartItemizeAll == null && StartDeleteAll == null)
            {
                ImGui.TextDisabled("Bestiary workflow not wired.");
            }
        }
        ImGui.End();
    }

    private void DrawInventoryButton()
    {
        if (!_settings.BestiaryAutomation.ShowInventoryButton.Value) return;
        if (!_menagerieRightClick.CanUse()) return;

        var inventoryPanel = _game?.IngameState?.IngameUi?.InventoryPanel?[InventoryIndex.PlayerInventory];
        if (inventoryPanel?.IsVisible != true) return;

        var rect = inventoryPanel.GetClientRect();
        if (rect.Width <= 0 || rect.Height <= 0) return;

        ImGui.SetNextWindowPos(new Vector2(rect.Left - 8f, rect.Top), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(0.9f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
                                       ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##BeastsV3InventoryQuickButton", flags)) { ImGui.End(); return; }

        if (_runner.IsRunning)
        {
            ImGui.TextDisabled("Automation running...");
            if (ImGui.Button("Stop##BeastsV3Inventory")) _runner.RequestStop();
        }
        else if (ImGui.Button("Right Click All Beasts##BeastsV3Inventory"))
        {
            Log.FireAndForget(() => _menagerieRightClick.RunAsync(), "Menagerie right-click");
        }

        ImGui.End();
    }
}
