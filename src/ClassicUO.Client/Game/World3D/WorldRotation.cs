#nullable enable
// The current view rotation, shared by the 3D renderer and the 2D client code that works in
// screen space (object screen positions for overheads / picking, mouse walking).
//
// Rotation is the target in quarter turns (0..3); Angle follows it smoothly (quarter turns,
// 0..4, wrapping) so a change of view turns over a short animation.

using System;
using Microsoft.Xna.Framework;

namespace Uo3d;

public static class WorldRotation
{
    /// <summary>Seconds a quarter turn takes.</summary>
    const float QuarterSeconds = 0.3f;

    /// <summary>Target view, 0..3 quarter turns; 0 = classic view.</summary>
    public static int Rotation;

    /// <summary>The angle shown right now, in quarter turns [0, 4).</summary>
    public static float Angle;

    /// <summary>Tile coordinates the view turns about (the player, with walking offset).</summary>
    public static float PivotX, PivotY;

    /// <summary>Quarter turns still to go, one after the other (a full turn is four).</summary>
    static int _pending;

    /// <summary>Seconds a quarter turn takes while a free-turn hotkey is held.</summary>
    const float HoldQuarterSeconds = 0.75f;

    /// <summary>+1 / -1 while a free-turn hotkey is held (set every frame), else 0.</summary>
    public static int Hold;

    static int _held;

    /// <summary>Turns the view by quarter turns (hotkeys), when the experimental option is on.</summary>
    public static void TurnBy(int quarters)
    {
        if (ClassicUO.Configuration.ProfileManager.CurrentProfile?.Enable3DRotation == true)
            _pending += quarters;
    }

    /// <summary>Back to the classic view at once, nothing left to turn.</summary>
    public static void Reset()
    {
        Rotation = 0;
        Angle = 0f;
        _pending = 0;
        Hold = _held = 0;
    }

    /// <summary>True while the view is not the classic one (also during the turn back).</summary>
    public static bool Turned => Rotation != 0 || Angle != 0f || _pending != 0;

    /// <summary>Advances <see cref="Angle"/> towards <see cref="Rotation"/>, shortest way round;
    /// once there, starts the next pending quarter turn.</summary>
    public static void Step(float seconds)
    {
        // held: turn freely; let go: on to the next quarter view in the direction of the turn
        if (Hold != 0)
        {
            _pending = 0;
            _held = Hold;
            Angle = ((Angle + Hold * seconds / HoldQuarterSeconds) % 4f + 4f) % 4f;
            return;
        }
        if (_held != 0)
        {
            Rotation = ((int)(_held > 0 ? MathF.Ceiling(Angle) : MathF.Floor(Angle)) % 4 + 4) % 4;
            _held = 0;
        }
        if (_pending != 0 && Angle == Rotation)
        {
            int dir = Math.Sign(_pending);
            Rotation = ((Rotation + dir) % 4 + 4) % 4;
            _pending -= dir;
        }
        float diff = ((Rotation - Angle) % 4f + 6f) % 4f - 2f; // [-2, 2)
        float step = seconds / QuarterSeconds;
        if (MathF.Abs(diff) <= step) Angle = Rotation;
        else Angle = ((Angle + MathF.Sign(diff) * step) % 4f + 4f) % 4f;
    }

    static (float c, float s) Turn
    {
        get
        {
            float a = Angle * MathF.PI / 2f;
            return (MathF.Cos(a), MathF.Sin(a));
        }
    }

    /// <summary>Map offset (ex, ey) turned by the current angle.</summary>
    public static Vector2 TurnMap(float ex, float ey)
    {
        var (c, s) = Turn;
        return new Vector2(c * ex - s * ey, s * ex + c * ey);
    }

    /// <summary>Screen shift (px, y down) of a world point between the classic view and the
    /// turned one, both keeping the pivot in place.</summary>
    public static void ScreenDelta(float x, float y, out int dx, out int dy)
    {
        if (!Turned)
        {
            dx = dy = 0;
            return;
        }
        float ex = x - PivotX, ey = y - PivotY;
        var r = TurnMap(ex, ey);
        dx = (int)MathF.Round(22f * ((r.X - r.Y) - (ex - ey)));
        dy = (int)MathF.Round(22f * ((r.X + r.Y) - (ex + ey)));
    }

    /// <summary>How near the viewer an object is in the turned view, for picking the one under the
    /// mouse: the turned diagonal plus height; mobiles win ties against what they stand among.</summary>
    public static float PickDepth(int x, int y, int z, bool mobile)
    {
        var r = TurnMap(x + 0.5f - PivotX, y + 0.5f - PivotY);
        return (r.X + r.Y) * 100f + z + (mobile ? 50f : 0f);
    }

    /// <summary>Eighth turns the screen is turned by, for the 8 UO directions.</summary>
    public static int ScreenEighths => Turned ? Eighths : 0;

    static int Eighths => ((int)MathF.Round(Angle * 2f) % 8 + 8) % 8;

    /// <summary>A map direction as seen on the turned screen (animations).</summary>
    public static byte ToScreenDirection(byte worldDirection) =>
        !Turned ? worldDirection : (byte)(((worldDirection & 7) + Eighths) % 8 | (worldDirection & ~7));

    /// <summary>A direction picked on screen (mouse, arrows) as a map direction.</summary>
    public static int ToWorldDirection(int screenDirection) =>
        !Turned ? screenDirection : ((screenDirection & 7) - Eighths + 16) % 8 | (screenDirection & ~7);

    /// <summary>A walking offset (screen px, classic view) as seen on the turned screen.</summary>
    public static float OffX(Vector3 o) => TurnOffset(o).X;

    /// <inheritdoc cref="OffX"/>
    public static float OffY(Vector3 o) => TurnOffset(o).Y;

    static Vector2 TurnOffset(Vector3 o)
    {
        if (!Turned) return new Vector2(o.X, o.Y);
        // screen offset -> map offset -> turned -> screen offset
        var r = TurnMap((o.X + o.Y) / 44f, (o.Y - o.X) / 44f);
        return new Vector2(22f * (r.X - r.Y), 22f * (r.X + r.Y));
    }

    /// <summary>Unit vector (screen px, y down) along which map north points on screen.</summary>
    public static Vector2 NorthOnScreen()
    {
        var r = TurnMap(0, -1);
        var v = new Vector2(r.X - r.Y, r.X + r.Y);
        v.Normalize();
        return v;
    }
}
