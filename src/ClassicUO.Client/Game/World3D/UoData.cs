#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Minimal readers for the classic MUL files (client 6.0.x): map, statics, tiledata
// (old 26/37-byte format), art, texmaps. Read-only, just what the prototype needs.

using System.Buffers.Binary;

namespace Uo3d;

public readonly record struct LandCell(ushort Id, sbyte Z);
public readonly record struct StaticCell(ushort Id, byte X, byte Y, sbyte Z, ushort Hue);

[Flags]
public enum TileFlag : uint
{
    Background = 0x1,
    Translucent = 0x8,
    Wet = 0x80,
    Wall = 0x10,
    Impassable = 0x40,
    Surface = 0x200,
    Bridge = 0x400,
    Window = 0x1000,
    Foliage = 0x20000,
    Roof = 0x10000000,
    Door = 0x20000000,
}

public sealed class TileData
{
    public readonly uint[] LandFlags = new uint[0x4000];
    public readonly ushort[] LandTex = new ushort[0x4000];
    public readonly uint[] StaticFlags;
    public readonly byte[] StaticHeight;
    public readonly string[] StaticName;

    public TileData(string path)
    {
        var d = File.ReadAllBytes(path);
        int p = 0;
        for (int i = 0; i < 0x4000; i++)
        {
            if ((i & 31) == 0) p += 4;
            LandFlags[i] = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
            LandTex[i] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 4));
            p += 26;
        }
        int count = (d.Length - p) / 1188 * 32;
        StaticFlags = new uint[count];
        StaticHeight = new byte[count];
        StaticName = new string[count];
        for (int i = 0; i < count; i++)
        {
            if ((i & 31) == 0) p += 4;
            StaticFlags[i] = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
            StaticHeight[i] = d[p + 16];
            StaticName[i] = System.Text.Encoding.ASCII.GetString(d, p + 17, 20).TrimEnd('\0');
            p += 37;
        }
    }

    /// <summary>Invisible placeholders ("nodraw", "no draw") the client never shows.</summary>
    public bool NoDraw(ushort id) =>
        id < StaticName.Length && StaticName[id].Replace(" ", "").Equals("nodraw", StringComparison.OrdinalIgnoreCase);

    public bool Has(ushort id, TileFlag f) => id < StaticFlags.Length && (StaticFlags[id] & (uint)f) != 0;
}

public sealed class UoMap
{
    readonly FileStream _map, _idx, _sta;
    public readonly int Width, Height;
    int BlocksH => Height / 8;

    public UoMap(string dir, int index, int width, int height)
    {
        _map = File.OpenRead(UoFiles.Find(dir, $"map{index}.mul"));
        _idx = File.OpenRead(UoFiles.Find(dir, $"staidx{index}.mul"));
        _sta = File.OpenRead(UoFiles.Find(dir, $"statics{index}.mul"));
        Width = width;
        Height = height;
    }

    readonly Dictionary<int, LandCell[]> _land = new();
    readonly Dictionary<int, List<StaticCell>> _statics = new();

    LandCell[] LandBlock(int bx, int by)
    {
        int b = bx * BlocksH + by;
        if (_land.TryGetValue(b, out var c)) return c;
        var buf = new byte[196];
        _map.Position = (long)b * 196;
        _map.ReadExactly(buf);
        c = new LandCell[64];
        for (int i = 0; i < 64; i++)
            c[i] = new LandCell(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(4 + i * 3)), (sbyte)buf[4 + i * 3 + 2]);
        return _land[b] = c;
    }

    public LandCell Land(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        return LandBlock(x >> 3, y >> 3)[(y & 7) * 8 + (x & 7)];
    }

    public List<StaticCell> StaticsBlock(int bx, int by)
    {
        int b = bx * BlocksH + by;
        if (_statics.TryGetValue(b, out var list)) return list;
        list = new List<StaticCell>();
        var ib = new byte[12];
        _idx.Position = (long)b * 12;
        _idx.ReadExactly(ib);
        int off = BinaryPrimitives.ReadInt32LittleEndian(ib);
        int len = BinaryPrimitives.ReadInt32LittleEndian(ib.AsSpan(4));
        if (off >= 0 && len > 0)
        {
            var buf = new byte[len];
            _sta.Position = off;
            _sta.ReadExactly(buf);
            for (int i = 0; i + 7 <= len; i += 7)
                list.Add(new StaticCell(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i)), buf[i + 2], buf[i + 3],
                    (sbyte)buf[i + 4], BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i + 5))));
        }
        return _statics[b] = list;
    }
}

/// <summary>UO file lookup that ignores the case of the name (installs copied to Linux often
/// have Map0.mul, Tiledata.mul, ...).</summary>
public static class UoFiles
{
    public static string Find(string dir, string name)
    {
        string exact = Path.Combine(dir, name);
        if (File.Exists(exact)) return exact;
        foreach (var f in Directory.EnumerateFiles(dir))
            if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                return f;
        return exact;
    }
}

/// <summary>An indexed MUL file (idx: offset, length, extra per entry).</summary>
public sealed class IndexedMul
{
    readonly byte[] _idx;
    readonly FileStream _mul;

    public IndexedMul(string idx, string mul)
    {
        _idx = File.ReadAllBytes(idx);
        _mul = File.OpenRead(mul);
    }

    public byte[]? Read(int index, out int extra)
    {
        extra = 0;
        if (index < 0 || (index + 1) * 12 > _idx.Length) return null;
        int off = BinaryPrimitives.ReadInt32LittleEndian(_idx.AsSpan(index * 12));
        int len = BinaryPrimitives.ReadInt32LittleEndian(_idx.AsSpan(index * 12 + 4));
        extra = BinaryPrimitives.ReadInt32LittleEndian(_idx.AsSpan(index * 12 + 8));
        if (off < 0 || len <= 0) return null;
        var b = new byte[len];
        _mul.Position = off;
        _mul.ReadExactly(b);
        return b;
    }
}

/// <summary>A decoded image: ARGB pixels (0 = transparent).</summary>
public sealed record Image(int Width, int Height, uint[] Pixels);

public static class UoImages
{
    public static uint Rgb555(ushort c)
    {
        uint r = (uint)((c >> 10) & 31) * 255 / 31;
        uint g = (uint)((c >> 5) & 31) * 255 / 31;
        uint b = (uint)(c & 31) * 255 / 31;
        return 0xFF000000 | (b << 16) | (g << 8) | r; // ABGR as XNA Color packs it
    }

    /// <summary>Land art: the 44x44 diamond.</summary>
    public static Image? Land(IndexedMul art, ushort id)
    {
        var d = art.Read(id, out _);
        if (d == null || d.Length < 2024) return null;
        var px = new uint[44 * 44];
        int p = 0;
        for (int y = 0; y < 44; y++)
        {
            int w = y < 22 ? (y + 1) * 2 : (44 - y) * 2;
            int x0 = 22 - w / 2;
            for (int x = 0; x < w; x++, p += 2)
                px[y * 44 + x0 + x] = Rgb555(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)));
        }
        return new Image(44, 44, px);
    }

    /// <summary>Static art (run-length encoded).</summary>
    public static Image? Static(IndexedMul art, ushort id)
    {
        var d = art.Read(0x4000 + id, out _);
        if (d == null || d.Length < 8) return null;
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(4));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6));
        if (w <= 0 || h <= 0 || w > 1024 || h > 1024) return null;
        var px = new uint[w * h];
        int start = 8 + h * 2;
        for (int y = 0; y < h; y++)
        {
            int pos = start + BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8 + y * 2)) * 2;
            int x = 0;
            while (pos + 4 <= d.Length)
            {
                int xo = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos));
                int run = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos + 2));
                pos += 4;
                if (xo + run == 0) break;
                x += xo;
                for (int i = 0; i < run && pos + 2 <= d.Length; i++, pos += 2, x++)
                    if (x < w) px[y * w + x] = Rgb555(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos)));
            }
        }
        return new Image(w, h, px);
    }

    /// <summary>Terrain texture (64x64 or 128x128).</summary>
    public static Image? Texmap(IndexedMul tex, ushort id)
    {
        var d = tex.Read(id, out _);
        if (d == null) return null;
        int size = d.Length >= 0x8000 ? 128 : 64;
        if (d.Length < size * size * 2) return null;
        var px = new uint[size * size];
        for (int i = 0; i < px.Length; i++)
            px[i] = Rgb555(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i * 2)));
        return new Image(size, size, px);
    }
}
