#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Objects UO builds from many statics that only fit together in the classic view
// (ships): composed into one image exactly as the classic client draws them and shown
// as a single camera-facing billboard. From other sides it keeps the original angle,
// but stays whole.

namespace Uo3d;

public sealed record Piece(int X, int Y, int Z, ushort Id, Image Img, int Prio);

/// <summary>A composed image and where it sits relative to its anchor tile in the classic
/// view (screen pixels, y down): left edge from the tile centre, bottom edge above the
/// tile's front corner.</summary>
public sealed record CompositeImage(Image Img, float Left, float BottomUp, int AnchorX, int AnchorY, int AnchorZ);

public static class Composite
{
    static readonly string[] Names = { "ship", "deck", "sail", "mast", "hull", "rudder", "tiller", "hatch", "boat", "ankh" };

    /// <summary>Indoor staircases: wooden steps and their banisters, one picture together (the
    /// steps alone would be planes, the banisters loose sprites that drift apart when turned).</summary>
    public static bool IsStairPart(TileData t, ushort id)
    {
        var n = t.StaticName[id].ToLowerInvariant();
        return n == "wooden stairs" || n == "wooden banister" || n == "wooden steps";
    }

    /// <summary>Furniture drawn across several tiles that only fits together as one picture.</summary>
    static readonly string[] Whole = { "bed", "bedroll" };

    /// <summary>Shrine floor mosaics: tiles named after the virtue, each a piece of one picture.</summary>
    static readonly string[] Virtues =
        { "honesty", "compassion", "justice", "valor", "spirituality", "honor", "humility", "sacrifice" };

    public static bool IsPart(TileData t, ushort id)
    {
        if (id >= t.StaticName.Length) return false;
        var n = t.StaticName[id].ToLowerInvariant();
        return Names.Any(k => n.Contains(k)) || Virtues.Contains(n) || Whole.Contains(n);
    }

    /// <summary>Connected groups (8-neighbourhood) of pieces.</summary>
    public static List<List<Piece>> Clusters(List<Piece> pieces)
    {
        var byTile = pieces.GroupBy(p => (p.X, p.Y)).ToDictionary(g => g.Key, g => g.ToList());
        var seen = new HashSet<(int, int)>();
        var result = new List<List<Piece>>();
        foreach (var start in byTile.Keys)
        {
            if (!seen.Add(start)) continue;
            var cluster = new List<Piece>();
            var stack = new Stack<(int X, int Y)>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                cluster.AddRange(byTile[c]);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var n = (c.X + dx, c.Y + dy);
                    if (byTile.ContainsKey(n) && seen.Add(n)) stack.Push(n);
                }
            }
            result.Add(cluster);
        }
        return result;
    }

    /// <summary>The cluster drawn in the classic client's order into one image.</summary>
    public static CompositeImage Compose(List<Piece> cluster)
    {
        float Sx(Piece p) => 22f * (p.X - p.Y) - p.Img.Width / 2f;
        float Sy(Piece p) => 22f * (p.X + p.Y + 2) - 4f * p.Z - p.Img.Height;
        int minX = (int)Math.Floor(cluster.Min(Sx)), minY = (int)Math.Floor(cluster.Min(Sy));
        int maxX = (int)Math.Ceiling(cluster.Max(p => Sx(p) + p.Img.Width));
        int maxY = (int)Math.Ceiling(cluster.Max(p => Sy(p) + p.Img.Height));
        int w = maxX - minX, h = maxY - minY;
        var px = new uint[w * h];
        foreach (var p in cluster.OrderBy(p => p.X + p.Y).ThenBy(p => p.Z).ThenBy(p => p.Prio))
        {
            int ox = (int)Math.Round(Sx(p)) - minX, oy = (int)Math.Round(Sy(p)) - minY;
            for (int y = 0; y < p.Img.Height; y++)
            for (int x = 0; x < p.Img.Width; x++)
            {
                uint c = p.Img.Pixels[y * p.Img.Width + x];
                int tx = ox + x, ty = oy + y;
                if ((c >> 24) != 0 && tx >= 0 && ty >= 0 && tx < w && ty < h) px[ty * w + tx] = c;
            }
        }
        // anchor: the front-most tile at the lowest height, so the billboard stands where
        // the ship touches the water
        int az = cluster.Min(p => p.Z);
        var front = cluster.OrderByDescending(p => p.X + p.Y).First();
        int ax = front.X, ay = front.Y;
        float cx = 22f * (ax - ay), bottom = 22f * (ax + ay + 2) - 4f * az;
        return new CompositeImage(new Image(w, h, px), minX - cx, bottom - maxY, ax, ay, az);
    }
}
