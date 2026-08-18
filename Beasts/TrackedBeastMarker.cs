using System;
using Vector2 = System.Numerics.Vector2;

namespace BeastsV3.Beasts;

// A tracked beast's last known grid position and capture state.
// IsLive is true while the entity is loaded; cached markers hold a frozen position.
public readonly record struct TrackedBeastMarker(
    long EntityId,
    string BeastName,
    Vector2 GridPos,
    BeastCaptureState CaptureState,
    bool IsLive,
    DateTime LastSeenUtc);
