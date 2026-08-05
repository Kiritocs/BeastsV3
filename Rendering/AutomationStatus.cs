using BeastsV3.Automation;
using BeastsV3.Plugin.Settings;
using ExileCore;

namespace BeastsV3.Rendering;

// Draws the automation status banner from Runner's current message, or a preview label
// while idle.
public sealed class AutomationStatus
{
    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly Runner _runner;

    public AutomationStatus(GameController game, BeastsSettings settings, Runner runner)
    {
        _game = game;
        _settings = settings;
        _runner = runner;
    }

    public void Render()
    {
        var overlay = _settings.AutomationStatus;
        if (!overlay.Show.Value) return;

        var hasLiveMessage = _runner.TryGetLiveOverlay(out var message, out var isError);
        if (!hasLiveMessage)
        {
            if (!overlay.ShowPreviewWhileIdle.Value) return;
            message = "Automation status preview";
            isError = false;
        }

        var style = new OverlayWindow.Style(
            Text: isError ? overlay.ErrorTextColor.Value : overlay.TextColor.Value,
            Border: isError ? overlay.ErrorBorderColor.Value : overlay.BorderColor.Value,
            Background: overlay.BackgroundColor.Value,
            Padding: overlay.Padding.Value,
            BorderThickness: overlay.BorderThickness.Value,
            BorderRounding: overlay.BorderRounding.Value,
            TextScale: overlay.TextScale.Value);

        OverlayWindow.Draw(_game, "##BeastsV3AutomationStatus", message,
            overlay.XPos.Value, overlay.YPos.Value, style);
    }
}
