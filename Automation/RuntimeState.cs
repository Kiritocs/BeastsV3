using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace BeastsV3.Automation;

// Per-run automation state shared by Runner, InputLock and the workflows.
public sealed class RuntimeState
{
    public bool IsRunning { get; set; }
    public bool IsInputLockActive { get; set; }
    public bool StopRequested { get; set; }
    public bool IsBestiaryClearRunning { get; set; }
    public CancellationTokenSource Cts { get; set; }

    // Hotkeys currently held down, so a held key does not retrigger each frame.
    public HashSet<Keys> HotkeysHeld { get; } = new();

    // Forces the overlay's delete-mode label during Bestiary delete runs.
    public bool? BestiaryDeleteModeOverride { get; set; }

    // Status overlay text and visibility.
    public string LastStatusMessage { get; set; } = string.Empty;
    public string OverlayMessage { get; set; } = string.Empty;
    public bool OverlayIsError { get; set; }
    public DateTime OverlayHideAtUtc { get; set; }
}
