using System;
using BeastsV3.Shared;
using ExileCore;

namespace BeastsV3.Analytics;

// Classification of an area change.
public enum AreaTransitionKind
{
    EnteredNonTrackableArea,
    ReenteredFinalizedMap,
    ReenteredActiveMap,
    EnteredSideZone,
    EnteredNewTrackableMap,
}

// Result of an area change: what kind it was, the areas involved, and whether the previous
// map needs finalizing.
public sealed record AreaTransitionDecision(
    AreaTransitionKind Kind,
    string PreviousAreaHash,
    string PreviousAreaName,
    int PreviousAreaInstanceId,
    string NewAreaHash,
    string NewAreaName,
    int NewAreaInstanceId,
    bool ShouldFinalizePreviousMap);

// Classifies area changes and updates the map-tracking fields of SessionState in place.
public sealed class AreaTransitions
{
    private readonly SessionState _state;

    public AreaTransitions(SessionState state)
    {
        _state = state;
    }

    // Applies the transition for the newly entered area and returns the decision.
    public AreaTransitionDecision Evaluate(AreaInstance area, bool hasCurrentMapProgress)
    {
        var move = new AreaMove(
            _state.ActiveMapAreaHash, _state.ActiveMapAreaName, _state.ActiveMapInstanceId,
            GameHelpers.TryGetAreaHashText(area) ?? string.Empty,
            GameHelpers.TryGetAreaName(area) ?? string.Empty,
            GameHelpers.TryGetAreaInstanceId(area));

        if (!GameHelpers.IsRunnableMap(area)) return LeaveTrackableArea(move, hasCurrentMapProgress);
        if (IsSameArea(move)) return ReenterSameMap(move);
        if (IsSideZone(move)) return EnterSideZone(move);
        return EnterNewMap(move, hasCurrentMapProgress);
    }

    // The area being left and the one being entered. Every decision reports both, so they travel
    // together rather than as six positional arguments.
    private readonly record struct AreaMove(
        string PreviousHash, string PreviousName, int PreviousInstanceId,
        string NewHash, string NewName, int NewInstanceId)
    {
        public AreaTransitionDecision Decide(AreaTransitionKind kind, bool shouldFinalize) =>
            new(kind, PreviousHash, PreviousName, PreviousInstanceId,
                NewHash, NewName, NewInstanceId, shouldFinalize);
    }

    // The same instance, by area hash or by instance id - either is enough, since both can read
    // as unset.
    private static bool IsSameArea(AreaMove move)
    {
        var hashMatches = !string.IsNullOrWhiteSpace(move.PreviousHash) &&
                          !string.IsNullOrWhiteSpace(move.NewHash) &&
                          string.Equals(move.NewHash, move.PreviousHash, StringComparison.Ordinal);
        var instanceMatches = move.PreviousInstanceId >= 0 && move.NewInstanceId >= 0 &&
                              move.NewInstanceId == move.PreviousInstanceId;
        return hashMatches || instanceMatches;
    }

    // A key for remembering an area: its hash, else its instance id, else none.
    private static string AreaKey(string hash, int instanceId) =>
        !string.IsNullOrWhiteSpace(hash) ? hash : instanceId >= 0 ? "instance:" + instanceId : null;

    // A side zone of the active map: entered straight from the map (or another of its side
    // zones), or one already known, e.g. returned to through a portal from the hideout. A map
    // cannot be entered straight from another map, so a direct step is always a side zone.
    private bool IsSideZone(AreaMove move)
    {
        if (string.IsNullOrWhiteSpace(_state.ActiveMapAreaHash) && _state.ActiveMapInstanceId < 0) return false;

        var key = AreaKey(move.NewHash, move.NewInstanceId);
        if (key != null && _state.ActiveMapSideZones.Contains(key)) return true;

        return _state.IsCurrentAreaTrackable || _state.IsInFinalizedMap || _state.IsInSideZone;
    }

    // Entered a side zone: the map stays the active one, neither banked nor restarted. Its clock
    // runs as in the map itself, unless the map is already banked.
    private AreaTransitionDecision EnterSideZone(AreaMove move)
    {
        var key = AreaKey(move.NewHash, move.NewInstanceId);
        if (key != null) _state.ActiveMapSideZones.Add(key);

        _state.IsInSideZone = true;
        _state.IsCurrentAreaTrackable = !_state.MapWasFinalized;
        _state.IsInFinalizedMap = _state.MapWasFinalized;

        return move.Decide(AreaTransitionKind.EnteredSideZone, false);
    }

    // Stepped out of a map: hideout or town. The map is only banked when it was already complete.
    private AreaTransitionDecision LeaveTrackableArea(AreaMove move, bool hasCurrentMapProgress)
    {
        var shouldFinalize = _state.CurrentMapWasComplete &&
                             (_state.CurrentMapElapsed > TimeSpan.Zero || hasCurrentMapProgress);

        _state.IsCurrentAreaTrackable = false;
        _state.IsInFinalizedMap = false;
        _state.IsInSideZone = false;
        if (_state.CurrentMapWasComplete)
        {
            _state.MapWasFinalized = true;
            _state.CurrentMapWasComplete = false;
        }

        return move.Decide(AreaTransitionKind.EnteredNonTrackableArea, shouldFinalize);
    }

    // Walked back into the same instance: either a map still being run, or one already banked,
    // which keeps showing its frozen totals.
    private AreaTransitionDecision ReenterSameMap(AreaMove move)
    {
        _state.ActiveMapAreaHash = move.NewHash;
        _state.ActiveMapAreaName = move.NewName;
        _state.ActiveMapInstanceId = move.NewInstanceId;
        _state.IsInSideZone = false;

        if (_state.MapWasFinalized)
        {
            _state.IsCurrentAreaTrackable = false;
            _state.IsInFinalizedMap = true;
            return move.Decide(AreaTransitionKind.ReenteredFinalizedMap, false);
        }

        _state.IsCurrentAreaTrackable = true;
        _state.IsInFinalizedMap = false;
        return move.Decide(AreaTransitionKind.ReenteredActiveMap, false);
    }

    // A genuinely new map, which is the only transition that clears the previous map's ids.
    private AreaTransitionDecision EnterNewMap(AreaMove move, bool hasCurrentMapProgress)
    {
        var shouldFinalize = _state.IsCurrentAreaTrackable ||
                             _state.CurrentMapElapsed > TimeSpan.Zero ||
                             hasCurrentMapProgress;

        _state.MapWasFinalized = false;
        _state.IsInFinalizedMap = false;
        _state.IsInSideZone = false;
        _state.ActiveMapSideZones.Clear();
        _state.ActiveMapAreaHash = move.NewHash;
        _state.ActiveMapAreaName = move.NewName;
        _state.ActiveMapInstanceId = move.NewInstanceId;
        // CurrentMapElapsed is left set; the recorder clears it after finalizing.
        _state.IsCurrentAreaTrackable = true;
        _state.CurrentMapWasComplete = false;

        return move.Decide(AreaTransitionKind.EnteredNewTrackableMap, shouldFinalize);
    }
}
