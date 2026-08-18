using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Navigation;

// Opens world entities such as stashes and NPCs by walking to them, hovering their
// ground label and clicking, retrying until an "is open" probe succeeds.
public sealed class WorldEntity
{
    private readonly GameController _game;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly Navigate _navigate;
    private readonly BeastsSettings _settings;

    public WorldEntity(GameController game, AutomationInput input, Waits waits, Navigate navigate,
        BeastsSettings settings)
    {
        _game = game;
        _input = input;
        _waits = waits;
        _navigate = navigate;
        _settings = settings;
    }

    // Walks to and clicks the target until isOpen returns true or the timeout expires.
    public async Task<bool> EnsureOpenAsync(
        Func<bool> isOpen,
        Func<Entity> findEntity,
        MouseButtons button = MouseButtons.Left,
        int overallTimeoutMs = 12000,
        params Keys[] modifiers)
    {
        if (isOpen()) return true;

        var timing = _settings.Timing;
        var scaledTimeout = _input.ScaleTimeout(overallTimeoutMs);
        var startedAt = DateTime.UtcNow;

        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < scaledTimeout)
        {
            _input.ThrowIfStopRequested();

            if (isOpen()) return true;

            var entity = findEntity();
            if (entity == null)
            {
                // No candidate visible; retry after a short delay.
                await _input.DelayAsync(timing.Polling.StashOpenPollDelayMs.Value);
                continue;
            }

            var distance = _navigate.DistanceToEntity(entity);
            var interactionDistance = _settings.Timing.Timeouts.StashInteractionDistance.Value;

            if (distance.HasValue && distance.Value <= interactionDistance)
            {
                // In range: hover and click.
                if (await TryHoverAndClickAsync(entity, button, modifiers))
                {
                    // Give the UI a moment to open.
                    if (await _waits.WaitForAsync(isOpen, 800, timing.Polling.FastPollDelayMs.Value))
                        return true;
                }
            }
            else
            {
                // Out of range: walk one step closer.
                await _navigate.WalkTowardsAsync(entity);
            }

            await _input.DelayAsync(timing.Polling.FastPollDelayMs.Value);
        }

        return isOpen();
    }

    // Hovers the entity's ground label and clicks it; false when no label was found.
    public async Task<bool> TryHoverAndClickAsync(Entity entity, MouseButtons button, params Keys[] modifiers)
    {
        var labelCenter = TryGetLabelCenter(entity);
        if (!labelCenter.HasValue) return false;

        var timing = _settings.Timing;
        await _input.MoveCursorToAsync(labelCenter.Value);
        await _input.DelayAsync(Math.Max(10, timing.Polling.FastPollDelayMs.Value));

        // Hover confirmation is optional; the click happens either way.
        var hovered = await _waits.WaitForAsync(
            () => IsHoveringLabel(entity),
            timeoutMs: Math.Max(40, timing.Clicks.UiClickPreDelayMs.Value + timing.Polling.FastPollDelayMs.Value),
            pollDelayMs: Math.Max(10, timing.Polling.FastPollDelayMs.Value));
        if (!hovered) Log.Debug($"WorldEntity click without confirmed hover. entity={entity?.Metadata}");

        await _input.ClickAsync(button,
            preDelayMs: 0,
            postDelayMs: timing.Timeouts.MapDeviceOpenTimeoutMs.Value > 0
                ? timing.Polling.OpenStashPostClickDelayMs.Value
                : timing.Clicks.UiClickPostDelayMs.Value,
            modifiers: modifiers ?? Array.Empty<Keys>());
        return true;
    }

    // ---- private -------------------------------------------------------

    // Finds the entity's visible ground label, matching by address then id/path/metadata.
    private SharpVec2? TryGetLabelCenter(Entity entity)
    {
        if (entity == null) return null;

        var labels = _game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                     ?? _game?.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.LabelsOnGround;
        if (labels == null) return null;

        var label = labels.FirstOrDefault(x =>
            x?.ItemOnGround != null && x.Label?.IsVisible == true &&
            (x.ItemOnGround.Address != 0 && entity.Address != 0
                ? x.ItemOnGround.Address == entity.Address
                : x.ItemOnGround.Id == entity.Id ||
                  string.Equals(x.ItemOnGround.Path, entity.Path, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(x.ItemOnGround.Metadata, entity.Metadata, StringComparison.OrdinalIgnoreCase)));

        var rect = label?.Label?.GetClientRect();
        return rect.HasValue ? new SharpVec2(rect.Value.Center.X, rect.Value.Center.Y) : null;
    }

    private bool IsHoveringLabel(Entity entity)
    {
        if (entity == null) return false;
        var container = _game?.IngameState?.IngameUi?.ItemsOnGroundLabelElement;
        var hoverPath = container?.ItemOnHoverPath;
        var hovered = container?.LabelOnHover;
        if (hovered == null || string.IsNullOrWhiteSpace(hoverPath)) return false;

        return string.Equals(hoverPath, entity.Path, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hoverPath, entity.Metadata, StringComparison.OrdinalIgnoreCase);
    }
}
