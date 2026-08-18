using System;
using System.Collections.Generic;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
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

    // Frames an entity may fail classification before being written off for the area.
    // Rarity and Stats read as unset for a frame or two after an entity enters the list.
    private const int MaxRejectRetries = 10;

    // Ceiling on distinct unknown metadata paths reported, so a surprise cannot fill the log.
    private const int MaxLoggedUnknownMetadata = 200;

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

    // Entities written off for this area, skipped before any memory read. Only verdicts that
    // cannot change go here; see NoteRejection.
    private readonly HashSet<long> _rejected = new();

    // Failed classification attempts per entity, promoted to _rejected once the budget runs out.
    private readonly Dictionary<long, int> _rejectAttempts = new();

    private readonly Dictionary<long, string> _beastNameByEntityId = new();

    // Metadata paths already reported as missing. Session-wide, so one log line per path.
    private readonly HashSet<string> _loggedUnknownMetadata = new(StringComparer.Ordinal);

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

    // Resets area state. With startingNewMap false, counts and markers are kept and only the
    // live entity map and per-entity caches are dropped. isTrackableArea lets a return from a
    // side zone restart the capturing-grace clock instead of resolving it against time away.
    public void OnAreaChanged(bool startingNewMap = true, bool isTrackableArea = true)
    {
        _liveTracked.Clear();

        // Entity ids are reassigned on load, so id-keyed caches are dropped every transition.
        _rejected.Clear();
        _rejectAttempts.Clear();
        _beastNameByEntityId.Clear();
        _liveInfo.Clear();

        if (!startingNewMap)
        {
            var now = DateTime.UtcNow;

            // Demote every marker to cached until Reconcile sees its entity again.
            MarkAllCached(now);

            if (isTrackableArea)
            {
                // Back in a trackable map: restart the grace clock for markers that were mid-capture,
                // rather than resolving them against time spent in a side zone and dropping the capture.
                RefreshCapturingMarkersTimestamp(now);
            }

            RebuildMarkerSnapshot();
            return;
        }

        _countedRareIds.Clear();
        _capturedIds.Clear();
        _markers.Clear();
        _markerSnapshot.Clear();
    }

    // Restamps LastSeenUtc on cached markers still mid-capture, so their grace is measured
    // from "back in the map".
    private void RefreshCapturingMarkersTimestamp(DateTime nowUtc)
    {
        _scratchIds.Clear();
        foreach (var (id, marker) in _markers)
        {
            if (!marker.IsLive && marker.CaptureState == BeastCaptureState.Capturing)
                _scratchIds.Add(id);
        }

        foreach (var id in _scratchIds)
        {
            _markers[id] = _markers[id] with { LastSeenUtc = nowUtc };
        }
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

    // What a first-sight entity turned out to be.
    private enum BeastVerdict
    {
        // Nothing readable yet. Carries no information, so it is never cached as a rejection.
        Unknown,

        // Not a rare capturable monster.
        NotABeast,

        // Rare and capturable but absent from the catalog. Counts for the quest, gets no marker.
        Uncatalogd,

        // Rare, capturable and in the catalog.
        Tracked,
    }

    // Classifies an entity from scratch. Pure: reads no caches and records no verdict.
    private BeastVerdict Classify(Entity entity, out string beastName)
    {
        beastName = null;
        if (entity == null) return BeastVerdict.Unknown;

        if (!BeastCaptureStates.IsRareCapturable(entity)) return BeastVerdict.NotABeast;

        // Rare and capturable but no metadata means the entity is still being populated - not
        // a catalog miss.
        if (string.IsNullOrEmpty(entity.Metadata)) return BeastVerdict.Unknown;

        return TryResolveTrackedBeastName(entity.Metadata, out beastName)
            ? BeastVerdict.Tracked
            : BeastVerdict.Uncatalogd;
    }

    public void OnEntityAdded(Entity entity)
    {
        if (entity == null) return;

        var verdict = Classify(entity, out var beastName);

        // EntityAdded fires the instant an entity enters the list, when Rarity and Stats are
        // least likely to be readable, so a negative verdict here is never cached.
        if (verdict is BeastVerdict.Unknown or BeastVerdict.NotABeast) return;

        if (verdict != BeastVerdict.Tracked)
        {
            RejectUncatalogd(entity, DateTime.UtcNow);
            return;
        }

        RegisterRareBeast(entity.Id, beastName, DateTime.UtcNow);

        _liveTracked[entity.Id] = entity;
        _beastNameByEntityId[entity.Id] = beastName;
        _rejectAttempts.Remove(entity.Id);
    }

    // A rare capturable monster with no catalog entry: counts toward the beast counter, gets
    // no name, price or marker, and is written off since metadata never changes per entity.
    private void RejectUncatalogd(Entity entity, DateTime nowUtc)
    {
        RegisterRareBeast(entity.Id, null, nowUtc);
        LogUnknownMetadataOnce(entity.Metadata);

        _rejected.Add(entity.Id);
        _rejectAttempts.Remove(entity.Id);
    }

    // Reports each unseen capturable metadata path once, so catalog drift after a league
    // shows up in a user's log without them reproducing anything.
    private void LogUnknownMetadataOnce(string metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return;
        if (_loggedUnknownMetadata.Count >= MaxLoggedUnknownMetadata) return;
        if (!_loggedUnknownMetadata.Add(metadata)) return;

        Log.Warn($"Capturable rare beast missing from the catalog: '{metadata}'. " +
                 "It counts toward the beast counter but has no name, price or marker.");
    }

    public void OnEntityRemoved(Entity entity)
    {
        if (entity != null) _liveTracked.Remove(entity.Id);
    }

    // Records first sight of a rare capturable beast. Both EntityAdded and Reconcile go
    // through here, so the counter cannot drift from what is rendered. True on first sighting.
    private bool RegisterRareBeast(long entityId, string beastName, DateTime nowUtc)
    {
        if (!_countedRareIds.Add(entityId)) return false;

        RareBeastSeen?.Invoke(entityId, beastName, nowUtc);
        return true;
    }

    // Counts a failed classification; the entity is only written off once the retry budget is
    // spent, so a transient miss cannot blacklist a beast for the whole map.
    private void NoteRejection(long entityId)
    {
        var attempts = _rejectAttempts.TryGetValue(entityId, out var previous) ? previous + 1 : 1;

        if (attempts >= MaxRejectRetries)
        {
            _rejectAttempts.Remove(entityId);
            _rejected.Add(entityId);
            return;
        }

        _rejectAttempts[entityId] = attempts;
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
            if (_rejected.Contains(id)) continue;

            if (!TryResolveTrackedBeast(entity, id, now, out var beastName)) continue;

            // Single read of the buff list, answering both questions below.
            ReadBuffState(entity, out var killOnExpiry, out var captureState);

            // Self-removing monsters are dropped unrecorded. The buff never comes off, so the entity
            // is written off rather than re-read every frame.
            if (killOnExpiry)
            {
                _liveTracked.Remove(id);
                _markers.Remove(id);
                _rejected.Add(id);
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

        DropEntitiesNoLongerLive();
        AgeOutMarkers(now);
        RebuildMarkerSnapshot();
    }

    // The catalog name for a live entity, cached after the first sighting. False when the entity
    // is not a tracked beast, in which case the verdict is recorded for later frames.
    private bool TryResolveTrackedBeast(Entity entity, long id, DateTime nowUtc, out string beastName)
    {
        // Cached name lookup; only first-sight entities run the checks below.
        if (_beastNameByEntityId.TryGetValue(id, out beastName)) return true;

        var verdict = Classify(entity, out beastName);
        if (verdict != BeastVerdict.Tracked)
        {
            if (verdict == BeastVerdict.Uncatalogd) RejectUncatalogd(entity, nowUtc);
            else NoteRejection(id);

            return false;
        }

        _beastNameByEntityId[id] = beastName;

        // Safety net for a beast EntityAdded missed because Rarity or Stats were unreadable.
        // Registering here too is what keeps the counter and the markers in sync.
        if (RegisterRareBeast(id, beastName, nowUtc))
        {
            // A recovery means EntityAdded missed a beast; frequent lines here point at entity
            // population timing rather than the catalog.
            var attempts = _rejectAttempts.TryGetValue(id, out var tries) ? tries : 0;
            Log.Debug($"Reconcile recovered '{beastName}' (entity {id}) that EntityAdded missed " +
                      $"after {attempts} failed classification attempt(s).");
        }

        _rejectAttempts.Remove(id);
        return true;
    }

    // Drops tracked entries that no longer appear in the live entity list.
    private void DropEntitiesNoLongerLive()
    {
        _scratchIds.Clear();
        foreach (var id in _liveTracked.Keys)
        {
            if (!_seenThisFrame.Contains(id)) _scratchIds.Add(id);
        }
        foreach (var id in _scratchIds) _liveTracked.Remove(id);
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

        // A stalled capture only resolves while we are actually in the map: time spent in a
        // non-trackable side zone says nothing about the entity, and letting the timeout fire
        // there would drop the marker as "captured" with no analytics crediting it.
        var areaTrackable = GameHelpers.IsRunnableMap(_game?.Area?.CurrentArea);

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

            if (!areaTrackable) continue;

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
