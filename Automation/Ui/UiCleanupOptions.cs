namespace BeastsV3.Automation.Ui;

// Panels a workflow wants left open; UiCleanup skips these.
public sealed class UiCleanupOptions
{
    public bool SkipUiCleanup { get; init; }
    public bool KeepBestiary { get; init; }
    public bool KeepAtlas { get; init; }
    public bool KeepStash { get; init; }
    public bool KeepMerchant { get; init; }
    public bool KeepInventory { get; init; }
    public bool KeepMapDeviceWindow { get; init; }
}
