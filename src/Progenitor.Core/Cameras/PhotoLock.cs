using System;
using Progenitor.Inputs;
using Progenitor.Units;

namespace Progenitor.Cameras;

/// <summary>What one lock-key event or frame did, for the client to show.</summary>
internal enum PhotoLockEvent
{
    None = 0,

    /// <summary>The key went down: the preview (name tag, outline) starts following the cursor.</summary>
    TargetingStarted,

    /// <summary>Released over a unit: it is now locked (flash, "Locked: name").</summary>
    Locked,

    /// <summary>Tapped while locked: "Unlocked".</summary>
    Unlocked,

    /// <summary>Released over nothing (or over the locked unit after a long hold): the preview ends, nothing changes.</summary>
    Cancelled,

    /// <summary>The locked unit died, despawned, left view range or the world changed: "Target lost".</summary>
    TargetLost,
}

/// <summary>
/// Photo mode's lock-on, game-free: hold the lock key to target (the unit under the cursor is
/// previewed while held), release over a unit to lock it, release over nothing to cancel, tap while
/// locked to unlock. Nothing is selected while the key is up. The client feeds it key edges, the
/// unit under the cursor and whether the locked unit is still valid.
/// </summary>
internal sealed class PhotoLockState
{
    /// <summary>A press and release shorter than this, with no new unit under the cursor, is a tap.</summary>
    public const double TapSeconds = 0.25;

    private double _downAt;
    private bool _movedOntoNew;

    /// <summary>True while the key is held (the preview is live).</summary>
    public bool Targeting { get; private set; }

    /// <summary>The unit under the cursor while targeting; null otherwise.</summary>
    public InputEntityRef? Preview { get; private set; }

    /// <summary>The locked unit, or null.</summary>
    public InputEntityRef? Locked { get; private set; }

    /// <summary>The lock key went down at <paramref name="now"/> (seconds).</summary>
    public PhotoLockEvent KeyDown(double now, InputEntityRef? underCursor)
    {
        if (Targeting) return PhotoLockEvent.None;
        Targeting = true;
        _downAt = now;
        _movedOntoNew = false;
        SetPreview(underCursor);
        return PhotoLockEvent.TargetingStarted;
    }

    /// <summary>Per frame while held: the unit now under the cursor.</summary>
    public void Hover(InputEntityRef? underCursor)
    {
        if (!Targeting) return;
        SetPreview(underCursor);
    }

    /// <summary>The lock key came up at <paramref name="now"/> with <paramref name="underCursor"/> under the cursor.</summary>
    public PhotoLockEvent KeyUp(double now, InputEntityRef? underCursor)
    {
        if (!Targeting) return PhotoLockEvent.None;
        SetPreview(underCursor);
        Targeting = false;
        var preview = Preview;
        Preview = null;
        bool tap = now - _downAt < TapSeconds && !_movedOntoNew;

        if (Locked != null && tap)
        {
            Locked = null;
            return PhotoLockEvent.Unlocked;
        }

        if (preview != null && !Same(preview, Locked))
        {
            Locked = preview;
            return PhotoLockEvent.Locked;
        }

        if (preview != null && Locked == null)
        {
            Locked = preview;
            return PhotoLockEvent.Locked;
        }

        return PhotoLockEvent.Cancelled;
    }

    /// <summary>The locked unit is no longer valid (dead, despawned, out of range, world change).</summary>
    public PhotoLockEvent Lost()
    {
        if (Locked == null) return PhotoLockEvent.None;
        Locked = null;
        return PhotoLockEvent.TargetLost;
    }

    /// <summary>Lock <paramref name="target"/> directly (bridge <c>photo lock</c>).</summary>
    public PhotoLockEvent LockDirect(InputEntityRef target)
    {
        Targeting = false;
        Preview = null;
        Locked = target;
        return PhotoLockEvent.Locked;
    }

    /// <summary>Unlock directly (bridge <c>photo unlock</c>).</summary>
    public PhotoLockEvent UnlockDirect()
    {
        Targeting = false;
        Preview = null;
        if (Locked == null) return PhotoLockEvent.None;
        Locked = null;
        return PhotoLockEvent.Unlocked;
    }

    /// <summary>Photo mode ended or the key bind was lost mid-hold: forget everything, report nothing.</summary>
    public void Reset()
    {
        Targeting = false;
        Preview = null;
        Locked = null;
        _movedOntoNew = false;
    }

    private void SetPreview(InputEntityRef? underCursor)
    {
        if (underCursor != null && underCursor.Value.IsNull) underCursor = null;
        if (underCursor != null && !Same(underCursor, Preview) && !Same(underCursor, Locked)) _movedOntoNew = true;
        Preview = underCursor;
    }

    private static bool Same(InputEntityRef? a, InputEntityRef? b) =>
        a.HasValue && b.HasValue && a.Value.Equals(b.Value);
}

/// <summary>Photo mode's lock-on camera, game-free: keep facing the target while flying, orbit with the mouse, wheel for distance.</summary>
internal static class PhotoOrbit
{
    /// <summary>Closest and farthest the wheel puts the camera from a locked target.</summary>
    public const float MinDistance = 1.5f;
    public const float MaxDistance = 200f;

    /// <summary>Each wheel notch multiplies (or divides) the distance by this.</summary>
    public const float DistanceStep = 1.15f;

    /// <summary>The look-at aims this high above the unit's feet (its body, not the ground).</summary>
    public const float AimHeight = 1.2f;

    /// <summary>The point the camera looks at for a unit standing at <paramref name="feet"/>.</summary>
    public static UnitVec3 FocusOf(UnitVec3 feet) => new(feet.X, feet.Y + AimHeight, feet.Z);

    /// <summary>
    /// One locked frame: WASD / E / Q move the camera (fast / slow apply), the right mouse orbits it
    /// around <paramref name="focus"/> at constant distance, the wheel moves it along the view line
    /// (wheel up is closer), and the view always ends facing the focus.
    /// </summary>
    public static PhotoPose Step(PhotoPose pose, UnitVec3 focus, in PhotoFlightInput input, float speed, float sensitivity, float dt, int wheelNotches)
    {
        // Translate without turning, then re-aim.
        var move = input;
        move.Looking = false;
        var moved = PhotoFlight.Step(pose, move, speed, sensitivity, dt);
        var pos = new UnitVec3(moved.X, moved.Y, moved.Z);

        var (yaw, pitch) = Angles(pos, focus);
        float dist = Distance(pos, focus);
        if (!(dist > 0.01f)) dist = MinDistance;

        if (input.Looking && sensitivity > 0f)
        {
            yaw += input.LookX * PhotoFlight.DegreesPerPixel * sensitivity;
            pitch -= input.LookY * PhotoFlight.DegreesPerPixel * sensitivity;
            pitch = CameraMath.ClampPitch(pitch);
        }

        if (wheelNotches != 0)
            dist = (float)Math.Clamp(dist * Math.Pow(DistanceStep, -wheelNotches), MinDistance, MaxDistance);
        else
            dist = Math.Clamp(dist, MinDistance, MaxDistance);

        var f = PhotoFlight.Forward(yaw, pitch);
        var camera = new UnitVec3(focus.X - (f.X * dist), focus.Y - (f.Y * dist), focus.Z - (f.Z * dist));
        var (y2, p2) = Angles(camera, focus);
        return new PhotoPose(camera.X, camera.Y, camera.Z, y2, p2);
    }

    /// <summary>Distance between two points.</summary>
    public static float Distance(UnitVec3 a, UnitVec3 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>The yaw and pitch that look from <paramref name="from"/> at <paramref name="to"/> (the game's convention, pitch clamped).</summary>
    public static (float Yaw, float Pitch) Angles(UnitVec3 from, UnitVec3 to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        float flat = MathF.Sqrt((dx * dx) + (dz * dz));
        float yaw = flat > 1e-5f ? MathF.Atan2(dx, dz) * (180f / MathF.PI) : 0f;
        float pitch = MathF.Atan2(-dy, Math.Max(flat, 1e-5f)) * (180f / MathF.PI);
        return (CameraMath.WrapYaw(yaw), CameraMath.ClampPitch(pitch));
    }
}

/// <summary>A unit's name as photo mode shows it, and where it came from.</summary>
internal readonly struct PhotoUnitName
{
    public PhotoUnitName(string display, string prefab, string source)
    {
        Display = display;
        Prefab = prefab;
        Source = source;
    }

    /// <summary>What the notices and <c>lockedTarget.name</c> show.</summary>
    public string Display { get; }

    /// <summary>The prefab name (<c>CHAR_Critter_Rat</c>), or empty.</summary>
    public string Prefab { get; }

    /// <summary><c>player</c>, <c>localized</c>, <c>prefab</c> (prettified) or <c>id</c>.</summary>
    public string Source { get; }
}

/// <summary>
/// Game-free naming rules for lock-on, in the order the game's own target panel would name a unit:
/// a player's character name; the localized name of the unit's <c>ManagedCharacterHUD</c> (what the
/// hover HUD and target info panel show: "Rat", a boss's title); and only when neither resolves, a
/// prettified prefab name.
/// </summary>
internal static class PhotoUnitNames
{
    /// <summary>Prefab-name tokens that are categories, not names (dropped when something else is left).</summary>
    private static readonly string[] CategoryTokens = { "CHAR", "Critter", "Wildlife", "VBlood", "Unit", "Summon", "Minion" };

    public static PhotoUnitName Choose(string? playerName, string? localized, string? prefab, string id)
    {
        string p = (prefab ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(playerName)) return new PhotoUnitName(playerName!.Trim(), p, "player");
        if (!string.IsNullOrWhiteSpace(localized)) return new PhotoUnitName(localized!.Trim(), p, "localized");
        string pretty = Prettify(p);
        if (pretty.Length > 0) return new PhotoUnitName(pretty, p, "prefab");
        return new PhotoUnitName(id, p, "id");
    }

    /// <summary><c>CHAR_Critter_Rat</c> → <c>Rat</c>; <c>CHAR_Bandit_Foreman_VBlood</c> → <c>Bandit Foreman</c>; <c>CHAR_Undead_SkeletonCrossbow_Base</c> → <c>Undead Skeleton Crossbow Base</c>.</summary>
    public static string Prettify(string? prefab)
    {
        if (string.IsNullOrWhiteSpace(prefab)) return "";
        var tokens = new System.Collections.Generic.List<string>();
        foreach (var raw in prefab!.Trim().Split('_'))
            if (raw.Length > 0) tokens.Add(raw);
        var kept = tokens.FindAll(t => Array.IndexOf(CategoryTokens, t) < 0);
        if (kept.Count == 0) kept = tokens;
        var words = new System.Collections.Generic.List<string>();
        foreach (var t in kept) words.AddRange(SplitCamel(t));
        return string.Join(" ", words);
    }

    private static System.Collections.Generic.IEnumerable<string> SplitCamel(string token)
    {
        int start = 0;
        for (int i = 1; i < token.Length; i++)
        {
            if (char.IsUpper(token[i]) && char.IsLower(token[i - 1]))
            {
                yield return token.Substring(start, i - start);
                start = i;
            }
        }
        yield return token.Substring(start);
    }
}
