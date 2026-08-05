using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using BeastsV3.Shared;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace BeastsV3.Plugin.Settings.Menu;

// Drawing primitives for the settings menu: rows, toggles, cards, pills and the
// per-node-type editors. Holds no plugin state, so it keeps working after an unload.
public static class MenuWidgets
{
    public const float RowLabelMin = 120f;
    public const float RowLabelMax = 260f;

    // Id of the hotkey row currently capturing a key, or null.
    private static string _capturingHotkeyId;

    // The key chosen but not yet committed - it is still held down. See DrawHotkey.
    private static Keys _pendingKey = Keys.None;

    // Keys already down when capture opened, ignored until they come back up.
    private static readonly HashSet<Keys> HeldAtCaptureStart = new();

    public static float LabelWidth()
    {
        var available = ImGui.GetContentRegionAvail().X;
        return Math.Clamp(available * 0.46f, RowLabelMin, RowLabelMax);
    }

    public static void Tip(string tooltip)
    {
        if (string.IsNullOrWhiteSpace(tooltip)) return;
        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
        ImGui.TextUnformatted(tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // Starts a row with the label on the left and the control right-aligned.
    public static void BeginRow(string label, string tooltip, float labelWidth)
    {
        var startX = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        Tip(tooltip);
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), startX + labelWidth));
        ImGui.SetNextItemWidth(-1f);
    }

    public static bool ToggleSwitch(string id, ref bool value)
    {
        var frameHeight = ImGui.GetFrameHeight();
        var height = MathF.Max(12f, frameHeight * 0.72f);
        var width = height * 1.95f;
        var origin = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, new Vector2(width, frameHeight));
        var clicked = ImGui.IsItemClicked();
        if (clicked) value = !value;

        var hovered = ImGui.IsItemHovered();
        var top = origin.Y + ((frameHeight - height) * 0.5f);
        var min = new Vector2(origin.X, top);
        var max = new Vector2(origin.X + width, top + height);
        var radius = height * 0.5f;

        var track = value ? MenuTheme.GoodFill : MenuTheme.Card;
        var edge = value ? MenuTheme.GoodFill : (hovered ? MenuTheme.BorderStrong : MenuTheme.Border);
        var knob = value ? MenuTheme.TextBright : (hovered ? MenuTheme.Text : MenuTheme.Muted);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, MenuTheme.U32(track), radius);
        draw.AddRect(min, max, MenuTheme.U32(edge), radius);

        var knobRadius = radius - 2f;
        var knobX = value ? max.X - knobRadius - 2f : min.X + knobRadius + 2f;
        draw.AddCircleFilled(new Vector2(knobX, top + radius), knobRadius, MenuTheme.U32(knob));

        return clicked;
    }

    // Right-aligns the next control against the edge of the content region.
    public static void AlignRight(float width)
    {
        var available = ImGui.GetContentRegionAvail().X;
        if (available > width) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - width);
    }

    public static void MetricCard(string id, string label, string value, Vector4 valueColor, float width)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, MenuTheme.Card);
        ImGui.BeginChild($"##card{id}", new Vector2(width, 54f), MenuTheme.PlainChild,
            ImGuiWindowFlags.NoScrollbar);

        ImGui.SetCursorPos(new Vector2(10f, 8f));
        ImGui.TextColored(MenuTheme.Muted, label);

        ImGui.SetCursorPos(new Vector2(10f, 24f));
        ImGui.SetWindowFontScale(1.3f);
        ImGui.TextColored(valueColor, value);
        ImGui.SetWindowFontScale(1f);

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    // Draws a read-only status chip. Clicks are ignored.
    public static void Pill(string text, Vector4 color, string tooltip = null)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, MenuTheme.WithAlpha(color, 0.16f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MenuTheme.WithAlpha(color, 0.24f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, MenuTheme.WithAlpha(color, 0.24f));
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.SmallButton(text);
        ImGui.PopStyleColor(4);
        Tip(tooltip);
    }

    public static void SectionHeading(string text, string tooltip = null)
    {
        ImGui.TextColored(MenuTheme.Gold, text);
        Tip(tooltip);
        ImGui.Spacing();
    }

    public static void Caption(string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(MenuTheme.Muted, text);
        ImGui.PopTextWrapPos();
    }

    // ---- node editors --------------------------------------------------

    // Draws the editor matching the item's node type.
    public static void DrawItem(MenuItem item, float labelWidth)
    {
        if (item?.Node == null) return;

        var id = "##bv3" + item.Id;

        switch (item.Node)
        {
            case ToggleNode toggle:
            {
                BeginRow(item.Label, item.Tooltip, labelWidth);
                AlignRight(ImGui.GetFrameHeight() * 1.45f);
                var value = toggle.Value;
                if (ToggleSwitch(id, ref value)) toggle.Value = value;
                break;
            }

            case RangeNode<int> range:
            {
                BeginRow(item.Label, item.Tooltip, labelWidth);
                var value = range.Value;
                // Falls back to a drag control when no range was resolved.
                var changed = item.HasRange
                    ? ImGui.SliderInt(id, ref value, (int)item.Min, (int)item.Max)
                    : ImGui.DragInt(id, ref value);
                if (changed) range.Value = value;
                break;
            }

            case RangeNode<float> range:
            {
                BeginRow(item.Label, item.Tooltip, labelWidth);
                var value = range.Value;
                var changed = item.HasRange
                    ? ImGui.SliderFloat(id, ref value, item.Min, item.Max, "%.2f")
                    : ImGui.DragFloat(id, ref value, 0.1f);
                if (changed) range.Value = value;
                break;
            }

            case ColorNode color:
            {
                BeginRow(item.Label, item.Tooltip, labelWidth);
                var value = ImGuiEx.ToVec4(color.Value);
                const ImGuiColorEditFlags flags = ImGuiColorEditFlags.NoInputs
                    | ImGuiColorEditFlags.AlphaBar
                    | ImGuiColorEditFlags.AlphaPreviewHalf;
                if (ImGui.ColorEdit4(id, ref value, flags))
                {
                    color.Value = new SharpDX.Color(
                        (byte)Math.Clamp(value.X * 255f, 0f, 255f),
                        (byte)Math.Clamp(value.Y * 255f, 0f, 255f),
                        (byte)Math.Clamp(value.Z * 255f, 0f, 255f),
                        (byte)Math.Clamp(value.W * 255f, 0f, 255f));
                }

                break;
            }

            case TextNode text:
            {
                BeginRow(item.Label, item.Tooltip, labelWidth);
                var value = text.Value ?? string.Empty;
                if (ImGui.InputText(id, ref value, 256)) text.Value = value;
                break;
            }

            case HotkeyNodeV2 hotkey:
            {
                BeginRow(item.Label, item.Tooltip, labelWidth);
                DrawHotkey(item, hotkey, id);
                break;
            }

            case ButtonNode button:
            {
                // Buttons carry their own label and take the full width.
                if (ImGui.Button(item.Label + id, new Vector2(-1f, 0f))) button.OnPressed?.Invoke();
                Tip(item.Tooltip);
                break;
            }

            case CustomNode custom:
            {
                // Panels draw their own layout, so the label becomes a heading.
                ImGui.TextColored(MenuTheme.TextBright, item.Label);
                Tip(item.Tooltip);
                ImGui.Indent(6f);
                try { custom.DrawDelegate?.Invoke(); }
                catch (Exception ex) { ImGui.TextColored(MenuTheme.Bad, $"Panel failed to draw: {ex.Message}"); }

                ImGui.Unindent(6f);
                break;
            }
        }
    }

    private static void DrawHotkey(MenuItem item, HotkeyNodeV2 hotkey, string id)
    {
        var capturing = _capturingHotkeyId == item.Id;
        var current = hotkey.Value.Key;

        var label = !capturing
            ? current == Keys.None ? "Not bound" : current.ToString()
            : _pendingKey == Keys.None
                ? "Press a key...  (Esc clears, click to cancel)"
                : $"Release {_pendingKey} to bind";

        if (capturing) ImGui.PushStyleColor(ImGuiCol.Button, MenuTheme.GoldDim);
        var clicked = ImGui.Button(label + id, new Vector2(-1f, 0f));
        // Popped before any early return below, or a cancelling click would leak a colour
        // onto the host's style stack and tint every window drawn after this one.
        if (capturing) ImGui.PopStyleColor();

        Tip(item.Tooltip);

        if (clicked)
        {
            if (capturing) EndCapture();
            else BeginCapture(item.Id);
            return;
        }

        if (!capturing) return;

        // Two phases, and the split is the whole point. Binding the instant the key goes
        // down leaves it physically held with the workflow now listening for it, and the
        // automation poller reads that same press as a trigger - the hotkey fired itself the
        // moment you assigned it. Committing on release means there is no press left to see.
        if (_pendingKey == Keys.None)
        {
            if (TryReadPressedKey(out var pressed)) _pendingKey = pressed;
            return;
        }

        if (ExileCore.Input.IsKeyDown((int)_pendingKey)) return;

        var target = _pendingKey == Keys.Escape ? Keys.None : _pendingKey;
        EndCapture();

        if (!TrySetHotkey(item, hotkey, target))
            Log.Error($"Could not rebind '{item.Label}' - this ExileCore build stores hotkeys in a shape the menu does not recognise.");
    }

    private static void BeginCapture(string itemId)
    {
        _capturingHotkeyId = itemId;
        _pendingKey = Keys.None;

        // Whatever is already down when capture opens is not a choice — it is the Space or
        // Enter that activated the button. Those keys become bindable again once released.
        HeldAtCaptureStart.Clear();
        foreach (var candidate in BindableKeys)
            if (ExileCore.Input.IsKeyDown((int)candidate))
                HeldAtCaptureStart.Add(candidate);
    }

    private static void EndCapture()
    {
        _capturingHotkeyId = null;
        _pendingKey = Keys.None;
        HeldAtCaptureStart.Clear();
    }

    // Bindable keys, excluding modifiers and mouse buttons.
    private static readonly Keys[] BindableKeys = Enum.GetValues(typeof(Keys))
        .Cast<Keys>()
        .Where(key => key is not (Keys.None or Keys.LButton or Keys.RButton
            or Keys.ShiftKey or Keys.ControlKey or Keys.Menu
            or Keys.LShiftKey or Keys.RShiftKey
            or Keys.LControlKey or Keys.RControlKey
            or Keys.LMenu or Keys.RMenu
            or Keys.Shift or Keys.Control or Keys.Alt or Keys.Modifiers))
        .Distinct()
        .ToArray();

    private static bool TryReadPressedKey(out Keys key)
    {
        foreach (var candidate in BindableKeys)
        {
            var down = ExileCore.Input.IsKeyDown((int)candidate);

            if (HeldAtCaptureStart.Contains(candidate))
            {
                // Held since capture opened, so it cannot be a deliberate choice. Once it
                // comes up, a later press of the same key counts normally.
                if (!down) HeldAtCaptureStart.Remove(candidate);
                continue;
            }

            if (!down) continue;

            key = candidate;
            return true;
        }

        key = Keys.None;
        return false;
    }

    private static readonly Dictionary<Type, PropertyInfo> HotkeyValueProperty = new();

    // Sets a hotkey's key, trying each HotkeyNodeV2 shape and finally replacing the node.
    private static bool TrySetHotkey(MenuItem item, HotkeyNodeV2 hotkey, Keys key)
    {
        try
        {
            var nodeType = hotkey.GetType();
            if (!HotkeyValueProperty.TryGetValue(nodeType, out var valueProperty))
            {
                valueProperty = nodeType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                HotkeyValueProperty[nodeType] = valueProperty;
            }

            if (valueProperty != null)
            {
                var valueType = valueProperty.PropertyType;

                if (valueProperty.CanWrite)
                {
                    var implicitCast = valueType.GetMethod("op_Implicit",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Keys) }, null);
                    if (implicitCast != null)
                    {
                        valueProperty.SetValue(hotkey, implicitCast.Invoke(null, new object[] { key }));
                        return true;
                    }

                    var constructor = valueType.GetConstructor(new[] { typeof(Keys) });
                    if (constructor != null)
                    {
                        valueProperty.SetValue(hotkey, constructor.Invoke(new object[] { key }));
                        return true;
                    }
                }

                var current = valueProperty.GetValue(hotkey);
                if (current != null)
                {
                    var keyProperty = valueType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                    if (keyProperty is { CanWrite: true })
                    {
                        keyProperty.SetValue(current, key);
                        if (valueProperty.CanWrite) valueProperty.SetValue(hotkey, current);
                        return true;
                    }

                    var keyField = valueType.GetField("Key", BindingFlags.Public | BindingFlags.Instance);
                    if (keyField != null)
                    {
                        keyField.SetValue(current, key);
                        if (valueProperty.CanWrite) valueProperty.SetValue(hotkey, current);
                        return true;
                    }
                }
            }

            if (item.Owner != null && item.Property is { CanWrite: true })
            {
                item.Property.SetValue(item.Owner, new HotkeyNodeV2(key));
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to rebind '{item.Label}'", ex);
        }

        return false;
    }
}
