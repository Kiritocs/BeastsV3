using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore.Shared.Nodes;

namespace BeastsV3.Automation.Input;

// Polls hotkeys on the rising edge, and also while held during a run so the same key can
// stop it. RuntimeState.HotkeysHeld prevents retriggering while a key stays down.
public sealed class HotkeyTracker
{
    private readonly RuntimeState _state;

    // Keys whose stale first edge has been discarded.
    private readonly HashSet<Keys> _primed = new();

    public HotkeyTracker(RuntimeState state)
    {
        _state = state;
    }

    // True when the hotkey fired this frame; usedHeldFallback marks a still-held trigger.
    public bool TryGet(HotkeyNodeV2 hotkey, bool isRunning, out Keys key, out bool usedHeldFallback)
    {
        key = hotkey?.Value.Key ?? Keys.None;
        usedHeldFallback = false;
        if (key == Keys.None) return false;

        var isKeyDown = ExileCore.Input.IsKeyDown((int)key);
        if (!isKeyDown) _state.HotkeysHeld.Remove(key);

        // Discards the spurious press a freshly loaded hotkey reports on its first poll.
        if (_primed.Add(key))
        {
            hotkey.PressedOnce();
            return false;
        }

        var alreadyHandled = _state.HotkeysHeld.Contains(key);

        if (hotkey.PressedOnce())
        {
            if (alreadyHandled) return false;
            _state.HotkeysHeld.Add(key);
            return true;
        }

        if (!isRunning || !isKeyDown || alreadyHandled) return false;

        _state.HotkeysHeld.Add(key);
        usedHeldFallback = true;
        return true;
    }
}
