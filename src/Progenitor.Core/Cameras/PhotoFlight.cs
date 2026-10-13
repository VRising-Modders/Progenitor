using System;

namespace Progenitor.Cameras;

/// <summary>Where the photo-mode camera is and where it looks, in the game's camera convention (yaw 0 = +Z, positive pitch looks down).</summary>
internal readonly struct PhotoPose
{
    public readonly float X, Y, Z, Yaw, Pitch;

    public PhotoPose(float x, float y, float z, float yaw, float pitch)
    {
        X = x; Y = y; Z = z;
        Yaw = CameraMath.WrapYaw(yaw);
        Pitch = CameraMath.ClampPitch(pitch);
    }

    public override string ToString() =>
        "(" + X.ToString("0.00") + ", " + Y.ToString("0.00") + ", " + Z.ToString("0.00") + ") yaw " + Yaw.ToString("0.0") + " pitch " + Pitch.ToString("0.0");
}

/// <summary>The held flight keys photo mode registers as Photo-scope keybinds; the value is the slot in the held array.</summary>
internal enum FlightKey { Forward = 0, Back = 1, Left = 2, Right = 3, Up = 4, Down = 5, Fast = 6, Slow = 7 }

/// <summary>One frame of held flight input, sampled by the client runtime.</summary>
internal struct PhotoFlightInput
{
    /// <summary>+1 forward (W), -1 back (S).</summary>
    public float Forward;

    /// <summary>+1 right (D), -1 left (A).</summary>
    public float Right;

    /// <summary>+1 up (E), -1 down (Q). World up, not view up.</summary>
    public float Up;

    /// <summary>Mouse movement in pixels this frame; only applied while <see cref="Looking"/>.</summary>
    public float LookX, LookY;

    /// <summary>Right mouse button held: mouse movement turns the camera.</summary>
    public bool Looking;

    public bool Fast, Slow;
}

/// <summary>
/// Photo mode's fly camera, game-free: WASD along the view, E / Q straight up and down,
/// right mouse to look, R held fast, F held slow, the wheel for speed and Z / X for field of
/// view (default keys; every key is a rebindable Photo-scope keybind). Time is the caller's unscaled delta, so the camera keeps flying while the world is
/// paused. Pitch is only held inside the free camera's ±89° (<see cref="CameraMath.PitchLimit"/>);
/// there is no height, distance or ground limit.
/// </summary>
internal static class PhotoFlight
{
    public const float DefaultSpeed = 6f;
    public const float MinSpeed = 0.25f;
    public const float MaxSpeed = 100f;

    /// <summary>Each wheel notch multiplies (or divides) the speed by this.</summary>
    public const float SpeedStep = 1.25f;

    public const float FastMultiplier = 4f;
    public const float SlowMultiplier = 0.2f;

    /// <summary>Degrees of turn per pixel of mouse movement at sensitivity 1.</summary>
    public const float DegreesPerPixel = 0.12f;

    public const float MinFov = 10f;
    public const float MaxFov = 110f;
    public const float FovStep = 2.5f;

    /// <summary>A long frame (a hitch, a breakpoint) moves at most this much time, so the camera never jumps across the map.</summary>
    public const float MaxStepSeconds = 0.1f;

    /// <summary>Unit view direction for a pose.</summary>
    public static (float X, float Y, float Z) Forward(float yaw, float pitch)
    {
        double y = yaw * (Math.PI / 180.0), p = pitch * (Math.PI / 180.0);
        return ((float)(Math.Sin(y) * Math.Cos(p)), (float)-Math.Sin(p), (float)(Math.Cos(y) * Math.Cos(p)));
    }

    /// <summary>Unit right direction (horizontal) for a yaw.</summary>
    public static (float X, float Z) Right(float yaw)
    {
        double y = yaw * (Math.PI / 180.0);
        return ((float)Math.Cos(y), (float)-Math.Sin(y));
    }

    /// <summary>The pose after one frame of <paramref name="input"/> at <paramref name="speed"/> m/s.</summary>
    public static PhotoPose Step(PhotoPose pose, in PhotoFlightInput input, float speed, float sensitivity, float dt)
    {
        if (!(dt > 0f)) return pose;
        if (dt > MaxStepSeconds) dt = MaxStepSeconds;

        float yaw = pose.Yaw, pitch = pose.Pitch;
        if (input.Looking && sensitivity > 0f)
        {
            yaw += input.LookX * DegreesPerPixel * sensitivity;
            pitch -= input.LookY * DegreesPerPixel * sensitivity;   // mouse up looks up (negative pitch)
        }
        pitch = CameraMath.ClampPitch(pitch);

        float v = Math.Clamp(speed, MinSpeed, MaxSpeed);
        if (input.Fast) v *= FastMultiplier;
        if (input.Slow) v *= SlowMultiplier;
        float d = v * dt;

        var f = Forward(yaw, pitch);
        var r = Right(yaw);
        float fw = Math.Clamp(input.Forward, -1f, 1f), rt = Math.Clamp(input.Right, -1f, 1f), up = Math.Clamp(input.Up, -1f, 1f);
        // Diagonals are not faster than straight lines.
        float len = MathF.Sqrt((fw * fw) + (rt * rt) + (up * up));
        if (len > 1f) { fw /= len; rt /= len; up /= len; }

        float x = pose.X + ((f.X * fw) + (r.X * rt)) * d;
        float yy = pose.Y + ((f.Y * fw) + up) * d;
        float z = pose.Z + ((f.Z * fw) + (r.Z * rt)) * d;
        return new PhotoPose(x, yy, z, yaw, pitch);
    }

    /// <summary>How many <see cref="FlightKey"/> slots the held array has.</summary>
    public const int FlightKeyCount = 8;

    /// <summary>
    /// The key half of one frame's input from the held flags the Photo-scope binds keep
    /// (indexed by <see cref="FlightKey"/>). Opposite keys cancel. The pointer half is the caller's.
    /// </summary>
    public static PhotoFlightInput FromHeld(bool[] held)
    {
        bool H(FlightKey k) => held != null && (int)k < held.Length && held[(int)k];
        return new PhotoFlightInput
        {
            Forward = (H(FlightKey.Forward) ? 1f : 0f) - (H(FlightKey.Back) ? 1f : 0f),
            Right = (H(FlightKey.Right) ? 1f : 0f) - (H(FlightKey.Left) ? 1f : 0f),
            Up = (H(FlightKey.Up) ? 1f : 0f) - (H(FlightKey.Down) ? 1f : 0f),
            Fast = H(FlightKey.Fast),
            Slow = H(FlightKey.Slow),
        };
    }

    /// <summary>The speed after <paramref name="notches"/> wheel notches (positive = faster).</summary>
    public static float NextSpeed(float speed, int notches)
    {
        double s = Math.Clamp(speed, MinSpeed, MaxSpeed) * Math.Pow(SpeedStep, notches);
        return (float)Math.Clamp(s, MinSpeed, MaxSpeed);
    }

    /// <summary>The field of view after <paramref name="notches"/> zoom steps (positive = zoom in, narrower).</summary>
    public static float NextFov(float fov, int notches) =>
        Math.Clamp(fov - (notches * FovStep), MinFov, MaxFov);

    /// <summary>Wheel movement this frame as whole notches: the sign only, so every input backend's scale reads the same.</summary>
    public static int Notches(float scrollY) => scrollY > 0.01f ? 1 : scrollY < -0.01f ? -1 : 0;
}
