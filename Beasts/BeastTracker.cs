using System;
using System.Collections.Generic;
using BeastsV3.Plugin.Settings;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;

namespace BeastsV3.Beasts;

// Tracks rare beasts in the current area and reconciles their capture state each frame.
// Owns the marker cache for the map overlay and raises RareBeastSeen / BeastCaptured.
public sealed class BeastTracker
{
    private const string CapturedBuffName = "capture_monster_captured";
    private const string TrappedBuffName = "capture_monster_trapped";

    // Marks temporary/summoned monsters that despawn on their own; these are never cached.
    private const string KillOnExpiryBuffName = "kill_on_expiry";

    // How long a marker stuck in Capturing is held before it counts as captured.
    private static readonly TimeSpan CapturingGrace = TimeSpan.FromSeconds(2);

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly Dictionary<string, string> _beastNameByMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _countedRareIds = new();
    private readonly HashSet<long> _capturedIds = new();
    private readonly Dictionary<long, Entity> _liveTracked = new();

    private readonly Dictionary<long, TrackedBeastMarker> _markers = new();
    private readonly List<TrackedBeastMarker> _markerSnapshot = new();
    private readonly HashSet<long> _seenThisFrame = new();
    private readonly List<long> _scratchIds = new();

    // Per-entity verdict cache for the Reconcile pre-filter, cleared on area change.
    private readonly HashSet<long> _notCapturable = new();
    private readonly Dictionary<long, string> _beastNameByEntityId = new();

    // Rebuilt each Reconcile; read by WorldLabels.
    private readonly Dictionary<long, LiveBeastInfo> _liveInfo = new();

    // Event args: entityId, resolvedBeastName (null when not in catalog), utcNow.
    public event Action<long, string, DateTime> RareBeastSeen;
    public event Action<long, string, DateTime> BeastCaptured;

    public BeastTracker(GameController game, BeastsSettings settings)
    {
        _game = game;
        _settings = settings;
    }

    public int RareBeastsFound => _countedRareIds.Count;

    public IReadOnlyDictionary<long, Entity> LiveTracked => _liveTracked;

    // Returns the name and capture state Reconcile resolved for a live beast this frame.
    public bool TryGetLiveInfo(long entityId, out LiveBeastInfo info) =>
        _liveInfo.TryGetValue(entityId, out info);

    public readonly record struct LiveBeastInfo(string BeastName, BeastCaptureState CaptureState);

    // Snapshot of every tracked beast in the area, live and remembered. Rebuilt per frame.
    public IReadOnlyList<TrackedBeastMarker> Markers => _markerSnapshot;

    // Resets area state. When startingNewMap is false, counts and markers are kept and only
    // the live entity map and per-entity caches are dropped.
    public void OnAreaChanged(bool startingNewMap = true)
    {
        _liveTracked.Clear();

        // Entity ids are reassigned on load, so id-keyed caches are dropped every transition.
        _notCapturable.Clear();
        _beastNameByEntityId.Clear();
        _liveInfo.Clear();

        if (!startingNewMap)
        {
            // Demote every marker to cached until Reconcile sees its entity again.
            MarkAllCached(DateTime.UtcNow);
            RebuildMarkerSnapshot();
            return;
        }

        _countedRareIds.Clear();
        _capturedIds.Clear();
        _markers.Clear();
        _markerSnapshot.Clear();
        _notCapturable.Clear();
        _beastNameByEntityId.Clear();
    }

    // Flips every live marker to cached and stamps the time.
    private void MarkAllCached(DateTime nowUtc)
    {
        _scratchIds.Clear();
        foreach (var (id, marker) in _markers)
        {
            if (marker.IsLive) _scratchIds.Add(id);
        }

        foreach (var id in _scratchIds)
        {
            _markers[id] = _markers[id] with { IsLive = false, LastSeenUtc = nowUtc };
        }
    }

    public void OnEntityAdded(Entity entity)
    {
        if (!BeastCaptureStates.IsRareCapturable(entity))
        {
            // Primes Reconcile's reject cache.
            if (entity != null) _notCapturable.Add(entity.Id);
            return;
        }

        if (!_countedRareIds.Add(entity.Id)) return;

        TryResolveTrackedBeastName(entity.Metadata, out var beastName);
        if (beastName != null)
        {
            _liveTracked[entity.Id] = entity;
            _beastNameByEntityId[entity.Id] = beastName;
        }
        else
        {
            // Capturable but not in the catalog, so it is rejected too.
            _notCapturable.Add(entity.Id);
        }

        RareBeastSeen?.Invoke(entity.Id, beastName, DateTime.UtcNow);
    }

    public void OnEntityRemoved(Entity entity)
    {
        if (entity != null) _liveTracked.Remove(entity.Id);
    }

    // Per-frame pass over the entity list: refreshes live tracking, fires BeastCaptured for
    // newly captured beasts and advances the marker cache.
    public void Reconcile()
    {
        var live = _game?.EntityListWrapper?.Entities;
        if (live == null) return;

        var now = DateTime.UtcNow;
        _seenThisFrame.Clear();
        _liveInfo.Clear();

        foreach (var entity in live)
        {
            if (entity?.IsValid != true) continue;

            var id = entity.Id;

            // Reject cache, checked before any memory read.
            if (_notCapturable.Contains(id)) continue;

            // Cached name lookup; only first-sight entities run the checks below.
            if (!_beastNameByEntityId.TryGetValue(id, out var beastName))
            {
                if (!BeastCaptureStates.IsRareCapturable(entity) ||
                    !TryResolveTrackedBeastName(entity.Metadata, out beastName))
                {
                    _notCapturable.Add(id);
                    continue;
                }

                _beastNameByEntityId[id] = beastName;
            }

            // Single read of the buff list, answering both questions below.
            ReadBuffState(entity, out var killOnExpiry, out var captureState);

            // Self-removing monsters are dropped without being recorded.
            if (killOnExpiry)
            {
                _liveTracked.Remove(id);
                _markers.Remove(id);
                continue;
            }

            _liveTracked[id] = entity;
            _seenThisFrame.Add(id);
            _liveInfo[id] = new LiveBeastInfo(beastName, captureState);

            UpsertLiveMarker(entity, beastName, captureState, now);

            if (_capturedIds.Contains(id)) continue;
            if (captureState != BeastCaptureState.Captured) continue;

            MarkCaptured(id, beastName, now);
        }

        // Drop tracked entries that no longer appear in the live entity list.
        _scratchIds.Clear();
        foreach (var id in _liveTracked.Keys)
        {
            if (!_seenThisFrame.Contains(id)) _scratchIds.Add(id);
        }
        foreach (var id in _scratchIds) _liveTracked.Remove(id);

        AgeOutMarkers(now);
        RebuildMarkerSnapshot();
    }

    // Writes or refreshes the marker for a live beast; captured beasts drop their marker.
    private void UpsertLiveMarker(Entity entity, string beastName, BeastCaptureState captureState, DateTime nowUtc)
    {
        if (captureState == BeastCaptureState.Captured)
        {
            _markers.Remove(entity.Id);
            return;
        }

        var positioned = entity.GetComponent<Positioned>();
        if (positioned == null) return;

        _markers[entity.Id] = new TrackedBeastMarker(
            entity.Id, beastName, positioned.GridPosNum, captureState, IsLive: true, LastSeenUtc: nowUtc);
    }

    // Advances markers whose entity is no longer live: live markers become cached, and
    // cached Capturing markers count as captured once CapturingGrace elapses.
    private void AgeOutMarkers(DateTime nowUtc)
    {
        _scratchIds.Clear();

        // Removals are collected and applied after the loop.
        foreach (var (id, marker) in _markers)
        {
            if (_seenThisFrame.Contains(id)) continue;

            if (marker.IsLive)
            {
                // Stamped once on the transition, not every frame.
                _markers[id] = marker with { IsLive = false, LastSeenUtc = nowUtc };
                continue;
            }

            if (marker.CaptureState == BeastCaptureState.Capturing &&
                nowUtc - marker.LastSeenUtc > CapturingGrace)
            {
                _scratchIds.Add(id);
            }
        }

        foreach (var id in _scratchIds)
        {
            if (!_markers.TryGetValue(id, out var marker)) continue;
            _markers.Remove(id);
            MarkCaptured(id, marker.BeastName, nowUtc);
        }
    }

    // Records a capture once, drops the marker and raises BeastCaptured.
    private void MarkCaptured(long entityId, string beastName, DateTime nowUtc)
    {
        if (!_capturedIds.Add(entityId)) return;

        _markers.Remove(entityId);
        BeastCaptured?.Invoke(entityId, beastName, nowUtc);
    }

    // Copies the marker map into the list handed to renderers.
    private void RebuildMarkerSnapshot()
    {
        _markerSnapshot.Clear();
        foreach (var marker in _markers.Values) _markerSnapshot.Add(marker);
    }

    // Reads the buff list once, returning the kill-on-expiry flag and the capture state.
    private static void ReadBuffState(Entity entity, out bool killOnExpiry, out BeastCaptureState captureState)
    {
        killOnExpiry = false;
        captureState = BeastCaptureState.None;

        var buffs = entity?.Buffs;
        if (buffs == null) return;

        foreach (var buff in buffs)
        {
            var name = buff?.Name;
            if (name == null) continue;

            if (string.Equals(name, KillOnExpiryBuffName, StringComparison.Ordinal))
            {
                killOnExpiry = true;
                return;
            }

            // Captured overrides Capturing.
            if (string.Equals(name, CapturedBuffName, StringComparison.Ordinal))
            {
                captureState = BeastCaptureState.Captured;
                continue;
            }

            if (captureState == BeastCaptureState.None &&
                string.Equals(name, TrappedBuffName, StringComparison.Ordinal))
            {
                captureState = BeastCaptureState.Capturing;
            }
        }
    }

    // True once no uncaptured markers for enabled beasts remain in this map.
    public bool AllTrackedValuableBeastsCaptured()
    {
        // No beasts seen yet does not count as complete.
        if (_countedRareIds.Count == 0) return false;

        var enabled = _settings.BeastPrices.EnabledBeasts;
        foreach (var marker in _markers.Values)
        {
            if (enabled.Count > 0 && !enabled.Contains(marker.BeastName)) continue;
            return false;
        }

        return true;
    }

    // Reads an entity's capture state directly from its buffs.
    public BeastCaptureState GetCaptureState(Entity entity)
    {
        var buffs = entity?.Buffs;
        if (buffs == null) return BeastCaptureState.None;

        if (buffs.Find(b => b.Name == CapturedBuffName) != null) return BeastCaptureState.Captured;
        if (buffs.Find(b => b.Name == TrappedBuffName) != null) return BeastCaptureState.Capturing;

        return BeastCaptureState.None;
    }

    // Maps entity metadata to a catalog beast name.
    public bool TryResolveTrackedBeastName(string metadata, out string beastName)
    {
        beastName = null;
        if (string.IsNullOrEmpty(metadata)) return false;

        if (_beastNameByMetadata.TryGetValue(metadata, out beastName))
            return beastName is not null;

        // Longest matching metadata prefix wins; the result is cached per metadata string.
        var bestLength = 0;
        string bestName = null;

        foreach (var beast in BeastCatalog.All)
        {
            foreach (var pattern in beast.MetadataPatterns)
            {
                if (pattern.Length <= bestLength) continue;
                if (!metadata.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) continue;

                bestLength = pattern.Length;
                bestName = beast.Name;
            }
        }

        _beastNameByMetadata[metadata] = bestName;
        beastName = bestName;
        return bestName is not null;
    }
}
