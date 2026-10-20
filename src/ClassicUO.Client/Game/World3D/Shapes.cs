#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Turns a static sprite into 3D planes that reproduce it exactly in the classic view.
//
// The UO projection is affine (view 0, absolute screen coordinates, y down):
//   sx = 22 (X - Z),  sy = 22 (X + Z) - 4 h      (tile X / Z, height h in z units)
// so a plane in the world, textured with the sprite through the screen position of
// its corners, shows the sprite pixel for pixel in view 0 - and is a real plane from
// every other side.
//
// Walls: the sprite's bottom edge is the wall's foot. Sloping down to the right ("\")
// it runs along X, down to the left ("/") along Z; a V or Λ is a corner, two planes.
// Roofs and floors: the flat or sloped plane over the tile that covers the sprite best.
// Anything that fits nothing stays a billboard (caller).

using Microsoft.Xna.Framework;

namespace Uo3d;

/// <summary>A textured quad: world corners (tile X, tile Z, height) and sprite UVs.</summary>
/// <param name="EndFace">The short end of a wall block (or its top): in a continuous wall it
/// sits between two tiles, hidden in the classic view, so other views leave it out.</param>
public sealed record Quad(Vector3[] Corners, Vector2[] Uv, bool EndFace = false);

public static class Shapes
{
    /// <summary>Fit quality of the last Wall (residual px) / Plane (score) call, for reports.</summary>
    public static double LastFit;

    public static Vector2 Screen(float x, float z, float h) => new(22f * (x - z), 22f * (x + z) - 4f * h);

    public static Vector2 Ground(Vector2 s, float h)
    {
        float a = s.X / 22f, b = (s.Y + 4f * h) / 22f;
        return new Vector2((a + b) / 2f, (b - a) / 2f);
    }

    /// <summary>Top-left of the sprite of a static at tile (x, y, z), view 0.</summary>
    public static Vector2 SpriteOrigin(int x, int y, int z, Image img) =>
        new(22f * (x - y) - img.Width / 2f, 22f * (x + y + 2) - 4f * z - img.Height);

    static bool Opaque(Image img, int px, int py) => (img.Pixels[py * img.Width + px] >> 24) != 0;

    static Quad Make(Vector3[] c, Vector2 origin, Image img)
    {
        var uv = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            var s = Screen(c[i].X, c[i].Y, c[i].Z);
            uv[i] = new Vector2((s.X - origin.X) / img.Width, (s.Y - origin.Y) / img.Height);
        }
        return new Quad(c, uv);
    }

    // ── walls ─────────────────────────────────────────────────────────────────────

    /// <summary>Wall planes from the sprite's bottom edge; null when it is no clean wall.</summary>
    public static List<Quad>? Wall(int x, int y, int z, Image img, int height = 0, double maxError = 2.5)
    {
        int w = img.Width, h = img.Height;
        var bottom = new int[w];
        var top = new int[w];
        int first = -1, last = -1;
        for (int px = 0; px < w; px++)
        {
            bottom[px] = -1;
            top[px] = -1;
            for (int py = 0; py < h; py++)
                if (Opaque(img, px, py))
                {
                    if (top[px] < 0) top[px] = py;
                    bottom[px] = py;
                }
            if (bottom[px] >= 0)
            {
                if (first < 0) first = px;
                last = px;
            }
        }
        LastFit = double.NaN;
        if (first < 0 || last - first < 8) return null;

        // one segment, or two meeting at the lowest (V) / highest (Λ) foot point
        var options = new List<(double err, List<(int a, int b, int slope)> segs)>();
        foreach (int s in new[] { 1, -1 })
            options.Add((Residual(bottom, first, last, s), new() { (first, last, s) }));
        int lowest = first, highest = first;
        for (int px = first; px <= last; px++)
        {
            if (bottom[px] > bottom[lowest]) lowest = px;
            if (bottom[px] >= 0 && bottom[px] < bottom[highest]) highest = px;
        }
        foreach (var (k, ls, rs) in new[] { (lowest, 1, -1), (highest, -1, 1) })
            if (k - first >= 4 && last - k >= 4)
            {
                double e = (Residual(bottom, first, k, ls) * (k - first + 1) + Residual(bottom, k, last, rs) * (last - k + 1))
                           / (last - first + 2);
                options.Add((e, new() { (first, k, ls), (k, last, rs) }));
            }
        var best = options.MinBy(o => o.err);
        LastFit = best.err;
        if (best.err > maxError) return null;

        var origin = SpriteOrigin(x, y, z, img);
        var quads = new List<Quad>();
        // ground length of each segment's foot: a short one next to a full one is the end
        // of the block (a stone wall's end, a log wall's log ends), not a corner
        var lengths = best.segs.Select(sg =>
        {
            double c = Median(sg.a, sg.b, p => bottom[p] - sg.slope * p);
            var f0 = Ground(new Vector2(origin.X + sg.a, origin.Y + (float)(sg.slope * sg.a + c + 1)), z);
            var f1 = Ground(new Vector2(origin.X + sg.b + 1, origin.Y + (float)(sg.slope * (sg.b + 1) + c + 1)), z);
            return Vector2.Distance(f0, f1);
        }).ToList();
        int segIndex = -1;
        foreach (var (a, b, slope) in best.segs)
        {
            segIndex++;
            double c = Median(a, b, p => bottom[p] - slope * p);
            float Foot(float u) => (float)(slope * u + c + 1); // pixel-edge y of the foot at column edge u
            int hpx = 0;
            for (int p = a; p <= b; p++)
                if (top[p] >= 0) hpx = Math.Max(hpx, (int)Math.Ceiling(Foot(p + 0.5f) - top[p]));
            float hz = hpx / 4f;
            // the foot runs from the left edge of column a to the right edge of column b
            float u0 = a, u1 = b + 1;
            var g0 = Ground(new Vector2(origin.X + u0, origin.Y + Foot(u0)), z);
            var g1 = Ground(new Vector2(origin.X + u1, origin.Y + Foot(u1)), z);

            // The sprite draws a block: front face, the top of the wall (its thickness seen
            // from above) and often an end. The face is as high as tiledata says; what is
            // above it is the top, laid flat on the wall, going back away from the viewer
            // (north for a wall along X, west for one along Z).
            float face = height > 0 && height < hz - 0.5f ? height : hz;
            bool end = lengths.Count == 2 && lengths[segIndex] < 0.6f && lengths[1 - segIndex] >= 0.85f;
            quads.Add(end, Make(new[]
            {
                new Vector3(g0.X, g0.Y, z + face), new Vector3(g1.X, g1.Y, z + face),
                new Vector3(g1.X, g1.Y, z), new Vector3(g0.X, g0.Y, z),
            }, origin, img));
            if (face < hz)
            {
                int capPx = 0;
                for (int p = a; p <= b; p++)
                    if (top[p] >= 0) capPx = Math.Max(capPx, (int)Math.Ceiling(Foot(p + 0.5f) - 4 * face - top[p]));
                float t = Math.Clamp(capPx / 22f, 0.05f, 0.6f);
                var back = slope > 0 ? new Vector2(0, -t) : new Vector2(-t, 0);
                var b0 = g0 + back;
                var b1 = g1 + back;
                quads.Add(end, Make(new[]
                {
                    new Vector3(b0.X, b0.Y, z + face), new Vector3(b1.X, b1.Y, z + face),
                    new Vector3(g1.X, g1.Y, z + face), new Vector3(g0.X, g0.Y, z + face),
                }, origin, img));
            }
        }
        return quads;
    }

    static void Add(this List<Quad> list, bool end, Quad q) => list.Add(q with { EndFace = end });

    /// <summary>Which way a block-like sprite (furniture against a wall) faces, from its foot line:
    /// +1 = the foot runs down to the right ("\", along X, front to the south), -1 = down to the
    /// left ("/", along Y, front to the east), 0 = no clear line.</summary>
    public static int FacingOf(Image img)
    {
        int w = img.Width, h = img.Height;
        var bottom = new int[w];
        int first = -1, last = -1;
        for (int px = 0; px < w; px++)
        {
            bottom[px] = -1;
            for (int py = h - 1; py >= 0; py--)
                if (Opaque(img, px, py)) { bottom[px] = py; break; }
            if (bottom[px] >= 0) { if (first < 0) first = px; last = px; }
        }
        if (first < 0 || last - first < 8) return 0;
        // the longer straight run of the foot decides: compare both slopes over each half
        int mid = (first + last) / 2;
        double down = Residual(bottom, first, last, 1), up = Residual(bottom, first, last, -1);
        double leftDown = Residual(bottom, first, mid, 1) + Residual(bottom, mid, last, -1);
        double leftUp = Residual(bottom, first, mid, -1) + Residual(bottom, mid, last, 1);
        if (Math.Min(down, up) * 2 < Math.Min(leftDown, leftUp)) return down < up ? 1 : -1;
        // a corner (V): the wider arm is the front
        int lowest = first;
        for (int px = first; px <= last; px++) if (bottom[px] > bottom[lowest]) lowest = px;
        return lowest - first >= last - lowest ? 1 : -1;
    }

    static double Residual(int[] bottom, int a, int b, int slope)
    {
        double c = Median(a, b, p => bottom[p] - slope * p);
        double sum = 0;
        int n = 0;
        for (int p = a; p <= b; p++)
            if (bottom[p] >= 0)
            {
                sum += Math.Abs(bottom[p] - (slope * p + c));
                n++;
            }
        return n == 0 ? double.MaxValue : sum / n;
    }

    static double Median(int a, int b, Func<int, double> f)
    {
        var v = new List<double>();
        for (int p = a; p <= b; p++) v.Add(f(p));
        v.Sort();
        return v[v.Count / 2];
    }

    /// <summary>A flat sprite of any size laid on the ground at height <paramref name="h"/>: the
    /// whole image rectangle, mapped back onto the ground (exact, the projection is affine).
    /// Pieces of mosaics larger than a tile (shrine floors, big rugs) keep their full art.</summary>
    public static Quad FlatSprite(int x, int y, int z, Image img, float h = 0)
    {
        var origin = SpriteOrigin(x, y, z, img);
        float lift = z + h;
        Vector3 At(float sx, float sy)
        {
            var g = Ground(new Vector2(origin.X + sx, origin.Y + sy), lift);
            return new Vector3(g.X, g.Y, lift);
        }
        return Make(new[] { At(0, 0), At(img.Width, 0), At(img.Width, img.Height), At(0, img.Height) }, origin, img);
    }

    /// <summary>A step block filling its tile: top face at the block's height plus the south and east
    /// faces the classic view shows, all textured from the sprite (exact in the classic view).
    /// The height comes from the sprite: everything below the 44 px diamond is the block's side.</summary>
    public static List<Quad>? Block(int x, int y, int z, Image img)
    {
        if (img.Width != 44 || img.Height <= 44) return null;
        float hz = (img.Height - 44) / 4f;
        var origin = SpriteOrigin(x, y, z, img);
        float top = z + hz;
        var nw = new Vector3(x, y, top);
        var ne = new Vector3(x + 1, y, top);
        var se = new Vector3(x + 1, y + 1, top);
        var sw = new Vector3(x, y + 1, top);
        Vector3 Down(Vector3 v) => new(v.X, v.Y, z);
        return new List<Quad>
        {
            Make(new[] { nw, ne, se, sw }, origin, img),                    // top
            Make(new[] { sw, se, Down(se), Down(sw) }, origin, img),        // south face
            Make(new[] { se, ne, Down(ne), Down(se) }, origin, img),        // east face
        };
    }

    /// <summary>The flat tile diamond at the static's height (water, lily pads).</summary>
    public static Quad Flat(int x, int y, int z, Image img) => Make(new[]
    {
        new Vector3(x, y, z), new Vector3(x + 1, y, z), new Vector3(x + 1, y + 1, z), new Vector3(x, y + 1, z),
    }, SpriteOrigin(x, y, z, img), img);

    // ── roofs and floors ──────────────────────────────────────────────────────────

    /// <summary>The flat or sloped plane over the tile that covers the sprite best.</summary>
    public static List<Quad>? Plane(int x, int y, int z, int height, Image img, double minScore = 0.72)
    {
        var origin = SpriteOrigin(x, y, z, img);
        var pts = new List<Vector2>();
        for (int py = 0; py < img.Height; py++)
        for (int px = 0; px < img.Width; px++)
            if (Opaque(img, px, py)) pts.Add(new Vector2(px + 0.5f, py + 0.5f));
        if (pts.Count == 0) return null;
        var sample = pts.Where((_, i) => i % 3 == 0).ToList();

        // A plane over the tile (grown by e on every side), lifted by b and rising by d
        // towards one of the four edges; dir 0 N, 1 S, 2 W, 3 E.
        Vector3[] Corners(int b, int dir, int d, float e)
        {
            float x0 = x - e, x1 = x + 1 + e, y0 = y - e, y1 = y + 1 + e;
            float H(float u, float v) => z + b + d * dir switch { 0 => 1 - v, 1 => v, 2 => 1 - u, _ => u };
            return new[]
            {
                new Vector3(x0, y0, H(0, 0)), new Vector3(x1, y0, H(1, 0)),
                new Vector3(x1, y1, H(1, 1)), new Vector3(x0, y1, H(0, 1)),
            };
        }
        double Score(Vector3[] c, List<Vector2> set, int total)
        {
            var poly = c.Select(p => Screen(p.X, p.Y, p.Z) - origin).ToArray();
            double area = Math.Abs(Shoelace(poly));
            if (area < 1) return 0;
            int inside = set.Count(q => Inside(poly, q.X, q.Y));
            double scale = (double)total / set.Count;
            double recall = (double)inside / set.Count, precision = Math.Min(1, inside * scale / area);
            return 2 * recall * precision / Math.Max(1e-9, recall + precision);
        }

        var es = new[] { 0f, 1f / 22, 2f / 22 };
        var coarse = new List<(double s, int b, int dir, int d, float e)>();
        for (int bb = -4; bb <= 28; bb += 2)
        for (int dir = 0; dir < 4; dir++)
        for (int d = 0; d <= 28; d += 2)
        {
            if (d == 0 && dir > 0) continue; // flat once
            foreach (float e in es) coarse.Add((Score(Corners(bb, dir, d, e), sample, pts.Count), bb, dir, d, e));
        }
        double bestScore = 0;
        Vector3[]? bestCorners = null;
        foreach (var (_, b0, dir, d0, e) in coarse.OrderByDescending(c => c.s).Take(6))
        for (int bb = b0 - 1; bb <= b0 + 1; bb++)
        for (int d = Math.Max(0, d0 - 1); d <= d0 + 1; d++)
        {
            var c = Corners(bb, dir, d, e);
            double sc = Score(c, pts, pts.Count);
            if (sc > bestScore)
            {
                bestScore = sc;
                bestCorners = c;
            }
        }
        LastFit = bestScore;
        return bestCorners != null && bestScore >= minScore ? new List<Quad> { Make(bestCorners, origin, img) } : null;
    }

    static double Shoelace(Vector2[] p)
    {
        double s = 0;
        for (int i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    static bool Inside(Vector2[] p, float x, float y)
    {
        bool pos = false, neg = false;
        for (int i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            float cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            if (cross > 0) pos = true;
            if (cross < 0) neg = true;
            if (pos && neg) return false;
        }
        return true;
    }
}
