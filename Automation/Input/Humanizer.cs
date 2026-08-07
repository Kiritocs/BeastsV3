using System;
using System.Collections.Generic;
using BeastsV3.Plugin.Settings;
using RectangleF = SharpDX.RectangleF;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Input;

// Randomises synthetic input so a run stops emitting a perfectly regular signature:
// Gaussian-spread delays, curved cursor travel, off-centre click points and the occasional
// hesitation pause.
//
// Everything is derived from the *configured* base value rather than replacing it, so the
// per-action tuning in the Timing menu survives -- humanisation only spreads it. Poll
// intervals and timeouts are deliberately left alone; randomising those would make waits
// non-deterministic without making the input look any more human.

public sealed class Humanizer
{
    // Cursor travel is capped so a pathological wind/gravity combination cannot spin for
    // thousands of points and stall a run.
    private const int MaxPathPoints = 400;

    // Kept clear of an element's edge when jittering a click, so a jittered point can never
    // land on the neighbouring cell.
    private const float EdgeMarginPx = 3f;

    private static readonly double Sqrt3 = Math.Sqrt(3.0);
    private static readonly double Sqrt5 = Math.Sqrt(5.0);

    private readonly BeastsSettings _settings;
    private readonly Random _rng = new();

    public Humanizer(BeastsSettings settings)
    {
        _settings = settings;
    }

    private TimingHumanizationSettings Cfg => _settings?.Timing?.Humanization;

    public bool Enabled => Cfg?.Enable?.Value == true;

    // ---- delays ----------------------------------------------------------

    // Spreads an already-scaled delay over a Gaussian centred on it, clamped to the
    // configured percentage band. A tuned 10ms delay stays in the tens of milliseconds;
    // it just stops being exactly 10ms every single time.
    public int Delay(int scaledMs)
    {
        var cfg = Cfg;
        if (cfg is not { Enable.Value: true }) return scaledMs;

        var baseMs = Math.Max(0, scaledMs);
        var floorJitter = Math.Max(0, cfg.MinJitterMs.Value);
        var sigma = MathF.Max(baseMs * (cfg.DelayVariancePercent.Value / 100f), floorJitter);
        if (sigma <= 0f) return baseMs;

        var low = baseMs * (cfg.MinDelayPercent.Value / 100f);
        var high = baseMs * (cfg.MaxDelayPercent.Value / 100f) + floorJitter;
        if (high < low) high = low;

        var value = NextGaussian(baseMs, sigma);
        return (int)MathF.Round(Math.Clamp(value, low, high));
    }

    // How long a key stays down between KeyDown and KeyUp. Real key presses cluster in the
    // 40-90ms range regardless of what the caller asked for, so this replaces the base value
    // rather than scaling it -- but never shortens a caller that deliberately holds longer.
    public int KeyHold(int baseMs)
    {
        var cfg = Cfg;
        if (cfg is not { Enable.Value: true }) return baseMs;

        var min = Math.Min(cfg.KeyHoldMinMs.Value, cfg.KeyHoldMaxMs.Value);
        var max = Math.Max(cfg.KeyHoldMinMs.Value, cfg.KeyHoldMaxMs.Value);
        if (max <= 0) return baseMs;

        var mean = (min + max) / 2f;
        var sigma = MathF.Max(1f, (max - min) / 4f);
        var hold = (int)MathF.Round(Math.Clamp(NextGaussian(mean, sigma), (float)min, (float)max));

        // A caller asking for a longer hold usually needs it (drag, charge, held modifier).
        return Math.Max(hold, baseMs);
    }

    // An occasional "the human looked away" pause, in milliseconds. Returns 0 most of the
    // time; the caller just adds whatever comes back to its next wait.
    public int Hesitation()
    {
        var cfg = Cfg;
        if (cfg is not { Enable.Value: true }) return 0;

        var chance = Math.Clamp(cfg.HesitationChancePercent.Value, 0, 100);
        if (chance <= 0 || _rng.Next(100) >= chance) return 0;

        var min = Math.Max(0, Math.Min(cfg.HesitationMinMs.Value, cfg.HesitationMaxMs.Value));
        var max = Math.Max(min, Math.Max(cfg.HesitationMinMs.Value, cfg.HesitationMaxMs.Value));
        return min == max ? min : _rng.Next(min, max + 1);
    }

    // ---- click points ----------------------------------------------------

    // Picks where inside a target to actually click. Without bounds it stays within a small
    // fixed radius; with bounds it may use a share of the element, never within
    // EdgeMarginPx of the edge.
    public SharpVec2 AimPoint(SharpVec2 target, RectangleF? bounds)
    {
        var cfg = Cfg;
        if (cfg is not { Enable.Value: true } || !cfg.ClickPointJitter) return target;

        var radius = MathF.Max(0f, cfg.ClickJitterRadiusPx.Value);
        if (radius <= 0f) return target;

        var radiusX = radius;
        var radiusY = radius;

        if (bounds.HasValue)
        {
            var share = Math.Clamp(cfg.ClickJitterElementPercent.Value, 0, 100) / 100f;
            var rect = bounds.Value;
            radiusX = MathF.Min(radiusX, MathF.Max(0f, rect.Width / 2f - EdgeMarginPx) * share);
            radiusY = MathF.Min(radiusY, MathF.Max(0f, rect.Height / 2f - EdgeMarginPx) * share);
        }

        if (radiusX <= 0f && radiusY <= 0f) return target;

        // sigma = radius/2 puts ~95% of samples inside the radius before the clamp, so the
        // distribution stays centre-weighted instead of piling up on the edge.
        var dx = radiusX <= 0f ? 0f : Math.Clamp(NextGaussian(0f, radiusX / 2f), -radiusX, radiusX);
        var dy = radiusY <= 0f ? 0f : Math.Clamp(NextGaussian(0f, radiusY / 2f), -radiusY, radiusY);

        return new SharpVec2(target.X + dx, target.Y + dy);
    }

    // ---- cursor travel ---------------------------------------------------

    public bool UseCursorPath => Cfg is { Enable.Value: true, UseWindMouse.Value: true };

    // Distance below which travelling is pointless and the cursor just teleports.
    public float MinPathDistance => Cfg?.MinPathDistancePx?.Value ?? 0;

    // Milliseconds to wait between two points of a cursor path.
    public int PathStepDelay()
    {
        var cfg = Cfg;
        if (cfg == null) return 0;

        var min = Math.Max(0, Math.Min(cfg.PathStepMinDelayMs.Value, cfg.PathStepMaxDelayMs.Value));
        var max = Math.Max(min, Math.Max(cfg.PathStepMinDelayMs.Value, cfg.PathStepMaxDelayMs.Value));
        return min == max ? min : _rng.Next(min, max + 1);
    }

    // WindMouse: the cursor is a mass pulled toward the target by gravity while a randomly
    // fluctuating "wind" pushes it sideways, damped once it is close. Produces the
    // overshoot-and-settle arc a hand makes instead of a straight line.
    //
    // Credit: Ben Land's WindMouse, https://ben.land/post/2021/04/25/windmouse-human-mouse-movement/
    public List<SharpVec2> BuildPath(SharpVec2 from, SharpVec2 to)
    {
        var points = new List<SharpVec2>();
        var cfg = Cfg;
        if (cfg == null) return points;

        double startX = from.X, startY = from.Y;
        double destX = to.X, destY = to.Y;

        double gravity = Math.Max(0.01f, cfg.GravityStrength.Value);
        double windBase = Math.Max(0f, cfg.WindStrength.Value);
        double maxStep = Math.Max(0.5f, cfg.StepSize.Value);
        double targetArea = Math.Max(0f, cfg.TargetArea.Value);

        double veloX = 0, veloY = 0, windX = 0, windY = 0, dist;

        while ((dist = Hypot(startX - destX, startY - destY)) >= 1.0 && points.Count < MaxPathPoints)
        {
            var wind = Math.Min(windBase, dist);

            if (dist >= targetArea)
            {
                windX = windX / Sqrt3 + (2 * _rng.NextDouble() - 1) * wind / Sqrt5;
                windY = windY / Sqrt3 + (2 * _rng.NextDouble() - 1) * wind / Sqrt5;
            }
            else
            {
                windX /= Sqrt3;
                windY /= Sqrt3;
                maxStep = maxStep < 3 ? _rng.NextDouble() * 3 + 3 : maxStep / Sqrt5;
            }

            veloX += windX + gravity * (destX - startX) / dist;
            veloY += windY + gravity * (destY - startY) / dist;

            var veloMag = Hypot(veloX, veloY);
            if (veloMag > maxStep)
            {
                var clipped = maxStep / 2 + _rng.NextDouble() * maxStep / 2;
                veloX = veloX / veloMag * clipped;
                veloY = veloY / veloMag * clipped;
            }

            startX += veloX;
            startY += veloY;

            points.Add(new SharpVec2((float)Math.Round(startX), (float)Math.Round(startY)));
        }

        // The loop stops within a pixel of the target, and the cap can stop it further out.
        // Either way the caller needs to actually land on the point it asked for.
        if (points.Count == 0 || points[^1] != to) points.Add(to);

        return points;
    }

    // ---- idle drift ------------------------------------------------------

    public bool DriftDuringPauses => Cfg is { Enable.Value: true, CursorDriftDuringPauses.Value: true };

    // A pixel or two of wander, for use while the run is already sitting still. Never moves
    // far enough to leave the element under the cursor.
    public SharpVec2 Drift(SharpVec2 anchor)
    {
        var cfg = Cfg;
        if (cfg == null) return anchor;

        var radius = Math.Max(0, cfg.CursorDriftRadiusPx.Value);
        if (radius <= 0) return anchor;

        return new SharpVec2(
            anchor.X + _rng.Next(-radius, radius + 1),
            anchor.Y + _rng.Next(-radius, radius + 1));
    }

    // ---- primitives ------------------------------------------------------

    // Box-Muller. Random.Shared is deliberately avoided so the sequence is per-instance.
    private float NextGaussian(float mean, float standardDeviation)
    {
        var u1 = 1.0f - (float)_rng.NextDouble();
        var u2 = 1.0f - (float)_rng.NextDouble();
        var standardNormal = MathF.Sqrt(-2.0f * MathF.Log(u1)) * MathF.Sin(2.0f * MathF.PI * u2);
        return mean + standardDeviation * standardNormal;
    }

    private static double Hypot(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
}
