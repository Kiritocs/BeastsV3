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
        var previousAreaHash = _state.ActiveMapAreaHash;
        var previousAreaName = _state.ActiveMapAreaName;
        var previousAreaInstanceId = _state.ActiveMapInstanceId;
        var newAreaHash = GameHelpers.TryGetAreaHashText(area) ?? string.Empty;
        var newAreaName = GameHelpers.TryGetAreaName(area) ?? string.Empty;
        var newAreaInstanceId = GameHelpers.TryGetAreaInstanceId(area);
        var newAreaTrackable = GameHelpers.IsRunnableMap(area);

        if (!newAreaTrackable)
        {
            var shouldFinalize = _state.CurrentMapWasComplete &&
                                 (_state.CurrentMapElapsed > TimeSpan.Zero || hasCurrentMapProgress);

            _state.IsCurrentAreaTrackable = false;
            _state.IsInFinalizedMap = false;
            if (_state.CurrentMapWasComplete)
            {
                _state.MapWasFinalized = true;
                _state.CurrentMapWasComplete = false;
            }

            return new AreaTransitionDecision(
                AreaTransitionKind.EnteredNonTrackableArea,
                previousAreaHash, previousAreaName, previousAreaInstanceId,
                newAreaHash, newAreaName, newAreaInstanceId,
                shouldFinalize);
        }

        var hashMatches = !string.IsNullOrWhiteSpace(previousAreaHash) &&
                          !string.IsNullOrWhiteSpace(newAreaHash) &&
                          string.Equals(newAreaHash, previousAreaHash, StringComparison.Ordinal);
        var instanceMatches = previousAreaInstanceId >= 0 && newAreaInstanceId >= 0 &&
                              newAreaInstanceId == previousAreaInstanceId;

        if (hashMatches || instanceMatches)
        {
            _state.ActiveMapAreaHash = newAreaHash;
            _state.ActiveMapAreaName = newAreaName;
            _state.ActiveMapInstanceId = newAreaInstanceId;

            if (_state.MapWasFinalized)
            {
                _state.IsCurrentAreaTrackable = false;
                _state.IsInFinalizedMap = true;

                return new AreaTransitionDecision(
                    AreaTransitionKind.ReenteredFinalizedMap,
                    previousAreaHash, previousAreaName, previousAreaInstanceId,
                    newAreaHash, newAreaName, newAreaInstanceId,
                    false);
            }

            _state.IsCurrentAreaTrackable = true;
            _state.IsInFinalizedMap = false;

            return new AreaTransitionDecision(
                AreaTransitionKind.ReenteredActiveMap,
                previousAreaHash, previousAreaName, previousAreaInstanceId,
                newAreaHash, newAreaName, newAreaInstanceId,
                false);
        }

        var shouldFinalizeTrackable = _state.IsCurrentAreaTrackable ||
                                      _state.CurrentMapElapsed > TimeSpan.Zero ||
                                      hasCurrentMapProgress;

        _state.MapWasFinalized = false;
        _state.IsInFinalizedMap = false;
        _state.ActiveMapAreaHash = newAreaHash;
        _state.ActiveMapAreaName = newAreaName;
        _state.ActiveMapInstanceId = newAreaInstanceId;
        // CurrentMapElapsed is left set; the recorder clears it after finalizing.
        _state.IsCurrentAreaTrackable = true;
        _state.CurrentMapWasComplete = false;

        return new AreaTransitionDecision(
            AreaTransitionKind.EnteredNewTrackableMap,
            previousAreaHash, previousAreaName, previousAreaInstanceId,
            newAreaHash, newAreaName, newAreaInstanceId,
            shouldFinalizeTrackable);
    }
}
