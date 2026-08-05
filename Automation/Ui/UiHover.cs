using ExileCore.PoEMemory.Elements.InventoryElements;

namespace BeastsV3.Automation.Ui;

// What the game reports the cursor is actually over.

public static class UiHover
{
    // Whether a hover read refers to the same item as the one being targeted.
    public static bool IsSameItem(NormalInventoryItem hovered, NormalInventoryItem target)
    {
        var hoveredEntity = hovered?.Item?.Address ?? 0;
        var targetEntity = target?.Item?.Address ?? 0;
        if (hoveredEntity != 0 && targetEntity != 0) return hoveredEntity == targetEntity;

        var hoveredElement = hovered?.Address ?? 0;
        var targetElement = target?.Address ?? 0;
        return hoveredElement != 0 && hoveredElement == targetElement;
    }
}
