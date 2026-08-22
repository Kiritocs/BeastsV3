using System;

namespace BeastsV3.Automation.Workflows;

// Thrown when the map device has no maps left to load. The farming loop treats this as a clean
// stop rather than an error, so it must be distinguishable from every other device failure -
// matching on the message text would break the moment the wording changes.
public sealed class MapSupplyExhaustedException : InvalidOperationException
{
    public MapSupplyExhaustedException(string message) : base(message) { }
}
