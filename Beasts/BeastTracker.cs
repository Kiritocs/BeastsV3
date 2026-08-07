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

    // How many frames an entity may fail classification before it is written off for the
    // rest of the area. Rarity and Stats read as unset for a frame or two after an entity
    // enters the list, so a single miss must never blacklist a beast for the whole map.
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

    // Entities written off for this area, skipped by Reconcile before any memory read.
    // Only verdicts that cannot change are recorded here; see NoteRejection.
    private readonly HashSet<long> _rejected = new();

    // Failed classification attempts per entity, promoted to _rejected once the budget runs out.
    private readonly Dictionary<long, int> _rejectAttempts = new();

    private readonly Dictionary<long, string> _beastNameByEntityId = new();

    // Metadata paths already reported as missing from the catalog. Kept for the whole
    // session, not per area, so each path costs exactly one log line.
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

    // Resets area state. When startingNewMap is false, counts and markers are kept and only
    // the live entity map and per-entity caches are dropped. isTrackableArea describes the
    // area just entered, so a return from a side zone can restart the
    // capturing-grace clock instead of resolving it against time spent away.
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
                // Back in a trackable map: a marker that was mid-capture when we left has been
                // sitting cached the whole time. Restart its grace clock here rather than
                // resolving it against however long we spent in a side zone, which would drop
                // it as "captured" without ever crediting the capture.
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

    // Restamps LastSeenUtc on cached markers still mid-capture, so their grace timeout is
    // measured from "back in the map" rather than from before a side-zone detour.
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

        // Rare and capturable but absent from the catalog. Counts toward the quest counter
        // and never gets a marker.
        Uncatalogued,

        // Rare, capturable and in the catalog.
        Tracked,
    }

    // Classifies an entity from scratch. Pure: it reads no caches and records no verdict,
    // so both entry points below can decide for themselves what to do with the answer.
    private BeastVerdict Classify(Entity entity, out string beastName)
    {
        beastName = null;
        if (entity == null) return BeastVerdict.Unknown;

        if (!BeastCaptureStates.IsRareCapturable(entity)) return BeastVerdict.NotABeast;

        // Rare and capturable but no metadata means the entity is still being populated;
        // treating that as "not in the catalog" would be a false negative.
        if (string.IsNullOrEmpty(entity.Metadata)) return BeastVerdict.Unknown;

        return TryResolveTrackedBeastName(entity.Metadata, out beastName)
            ? BeastVerdict.Tracked
            : BeastVerdict.Uncatalogued;
    }

    public void OnEntityAdded(Entity entity)
    {
        if (entity == null) return;

        var verdict = Classify(entity, out var beastName);

        // EntityAdded fires the instant an entity enters the list, which is exactly when
        // Rarity and Stats are least likely to be readable. A negative verdict here is
        // therefore never cached; Reconcile re-checks the entity on later frames.
        if (verdict is BeastVerdict.Unknown or BeastVerdict.NotABeast) return;

        if (verdict != BeastVerdict.Tracked)
        {
            RejectUncatalogued(entity, DateTime.UtcNow);
            return;
        }

        RegisterRareBeast(entity.Id, beastName, DateTime.UtcNow);

        _liveTracked[entity.Id] = entity;
        _beastNameByEntityId[entity.Id] = beastName;
        _rejectAttempts.Remove(entity.Id);
    }

    // Handles a rare capturable monster with no catalog entry. It still counts toward the
    // beast counter, but it has no name, price or marker, and it is written off for the
    // area because metadata never changes for a given entity id.
    private void RejectUncatalogued(Entity entity, DateTime nowUtc)
    {
        RegisterRareBeast(entity.Id, null, nowUtc);
        LogUnknownMetadataOnce(entity.Metadata);

        _rejected.Add(entity.Id);
        _rejectAttempts.Remove(entity.Id);
    }

    // Reports each unseen capturable metadata path once. A league that adds or renames
    // beasts surfaces here, so a user's log alone is enough to spot catalog drift without
    // them having to reproduce anything.
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
    // through here, so the counter cannot drift from what is being tracked and rendered.
    // Returns true when this was the first sighting.
    private bool RegisterRareBeast(long entityId, string beastName, DateTime nowUtc)
    {
        if (!_countedRareIds.Add(entityId)) return false;

        RareBeastSeen?.Invoke(entityId, beastName, nowUtc);
        return true;
    }

    // Counts a failed classification. The entity is only written off once the retry budget
    // is spent, which keeps a transient miss from blacklisting a beast for the whole map
    // while still letting the per-frame pass shed non-beasts after a handful of frames.
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

            // Cached name lookup; only first-sight entities run the checks below.
            if (!_beastNameByEntityId.TryGetValue(id, out var beastName))
            {
                var verdict = Classify(entity, out beastName);

                if (verdict != BeastVerdict.Tracked)
                {
                    if (verdict == BeastVerdict.Uncatalogued) RejectUncatalogued(entity, now);
                    else NoteRejection(id);

                    continue;
                }

                _beastNameByEntityId[id] = beastName;

                // Safety net for a beast EntityAdded missed because Rarity or Stats were
                // not readable when it fired. Registering here as well is what keeps the
                // counter and the markers from diverging.
                if (RegisterRareBeast(id, beastName, now))
                {
                    // Logged because a recovery means EntityAdded missed a beast, which is
                    // the failure this pass exists to cover. Frequent lines here in a user's
                    // log point at entity population timing rather than at the catalog.
                    var attempts = _rejectAttempts.TryGetValue(id, out var tries) ? tries : 0;
                    Log.Debug($"Reconcile recovered '{beastName}' (entity {id}) that EntityAdded missed " +
                              $"after {attempts} failed classification attempt(s).");
                }

                _rejectAttempts.Remove(id);
            }

            // Single read of the buff list, answering both questions below.
            ReadBuffState(entity, out var killOnExpiry, out var captureState);

            // Self-removing monsters are dropped without being recorded. The buff does not
            // come back off, so the entity is written off rather than re-read every frame.
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

        // Resolving a stalled capture only means something while we're actually in the map.
        // If we've stepped into a non-trackable side zone (Starfall Crater, hideout, ...),
        // elapsed real time there is not a reliable signal for what happened to the entity;
        // letting the timeout fire anyway would silently drop the marker as "captured"
        // without analytics ever crediting it, since it isn't running there either.
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
