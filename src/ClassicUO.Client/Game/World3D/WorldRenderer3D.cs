#nullable enable
// Proof of concept: draws the world as 3D geometry in UO's own oblique projection, so the
// view can rotate in 90° steps. At rotation 0 TazUO draws as always; at 90/180/270 the
// game scene hands its world render target to this renderer instead.
//
// Projection, view space (y up), rotated about the anchor tile (cx, cy):
//   x  = 22 (X' - Y'),  y = -22 (X' + Y') + 4 Z,  depth = X' + Y' + 11 Z
// The anchor's tile corner sits where TazUO would draw it (from the scene's draw offset),
// so rotation 0 matches the 2D client; rotated views turn about the player.
//
// Data comes straight from the MUL files of the UO directory (land, statics, art); server
// items and mobiles are not drawn yet.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Uo3d;

public sealed class WorldRenderer3D
{
    const int Radius = 40;
    /// <summary>How far the player may walk from the built area's centre before a rebuild.</summary>
    const int Drift = 10;
    const float LightMax = 1.3f;
    static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0, 1, 1)); // map x east, y south, up

    readonly GraphicsDevice _gd;
    readonly string _uoDir;
    readonly TileData _tiles;
    readonly IndexedMul _art;
    readonly Func<int, (int w, int h)> _mapSize;
    UoMap? _map;
    int _mapIndex = -1;

    readonly BasicEffect _terrainFx;
    readonly AlphaTestEffect _spriteFx;
    readonly Dictionary<int, Texture2D?> _landArt = new(), _staticArt = new();
    readonly Dictionary<int, Image?> _staticImg = new();
    readonly Dictionary<string, (CompositeImage, Texture2D)> _composites = new();
    readonly Dictionary<int, List<Quad>?> _shapes = new();
    readonly Dictionary<Texture2D, List<VertexPositionTexture>> _sprites = new(), _dynSprites = new();
    // shadows of trees, foliage and rocks, flat on the ground, cast away from the sun (south)
    readonly Dictionary<Texture2D, List<VertexPositionTexture>> _shadows = new(), _dynShadows = new();
    Dictionary<Texture2D, List<VertexPositionTexture>> _shadowTarget;
    readonly BasicEffect _shadowFx;
    Func<ushort, bool>? _castsShadow;
    // picking: the drawn triangles (2 per quad, view space) of each server item, per serial
    readonly Dictionary<uint, List<(VertexPositionTexture[] Tris, Image? Img, bool Padded)>> _pickQuads = new();
    uint _pickSerial;
    Image? _pickImage;
    // view space -> render target pixels of the last frame, and the target's shift
    float _pickOx, _pickOy, _pickShiftX, _pickShiftY;
    Vector2 _pickRtShift;
    // circle of transparency: statics / items TazUO fades this frame (AlphaHue < 255) are left out
    // of the built geometry and drawn each frame with their alpha, without writing depth
    HashSet<(int, int, int, ushort)> _fadedKeys = new();
    readonly Dictionary<Texture2D, List<VertexPositionColorTexture>> _faded = new();
    readonly BasicEffect _fadeFx;
    bool _builtShadows;
    Dictionary<Texture2D, List<VertexPositionTexture>> _target;
    readonly Dictionary<Texture2D, List<VertexPositionColorTexture>> _terrain = new();
    // FNA has no border address mode: plane textures get a 1 px transparent frame instead,
    // so UVs outside the sprite (planes reach past it) clamp to nothing
    readonly Dictionary<int, Texture2D?> _paddedArt = new();

    int _cx, _cy;
    float _builtAngle = -1f;
    /// <summary>Roof hiding, as TazUO decides it: statics from this height up and land above
    /// the ground limit are left out (inside houses, caves).</summary>
    int _maxZ = 127, _maxGroundZ = 127;
    bool _dirty = true;

    /// <summary>The view angle in quarter turns (any value; 0 = the classic view).</summary>
    public float Angle { get; set; }

    public WorldRenderer3D(GraphicsDevice gd, string uoDir, Func<int, (int w, int h)> mapSize)
    {
        _gd = gd;
        _uoDir = uoDir;
        _mapSize = mapSize;
        _tiles = new TileData(UoFiles.Find(uoDir, "tiledata.mul"));
        _art = new IndexedMul(UoFiles.Find(uoDir, "artidx.mul"), UoFiles.Find(uoDir, "art.mul"));
        // vertex colours carry the light / LightMax so they stay <= 1; the diffuse colour
        // scales back, so a flat tile keeps its art exactly
        _target = _sprites;
        _shadowTarget = _shadows;
        _fadeFx = new BasicEffect(gd) { TextureEnabled = true, VertexColorEnabled = true };
        _shadowFx = new BasicEffect(gd) { TextureEnabled = true, DiffuseColor = Vector3.Zero, Alpha = 0.45f };
        _terrainFx = new BasicEffect(gd) { TextureEnabled = true, VertexColorEnabled = true, DiffuseColor = new Vector3(LightMax) };
        _spriteFx = new AlphaTestEffect(gd) { ReferenceAlpha = 128, AlphaFunction = CompareFunction.Greater };
    }

    /// <summary>What the scene tells the renderer each frame.</summary>
    /// <param name="Offset">TazUO's world draw offset (GameScene._offset).</param>
    /// <param name="RtShift">The world render target's own translation (the world matrix).</param>
    /// <param name="PlayerOffset">The player's walking offset in screen pixels (Mobile.Offset).</param>
    public readonly record struct Frame(
        int MapIndex, int PlayerX, int PlayerY, int PlayerZ, Vector3 PlayerOffset, Point Offset, Vector2 RtShift,
        IReadOnlyList<(ushort Graphic, int X, int Y, int Z, uint Serial)> Items, int MaxZ = 127, int MaxGroundZ = 127,
        Func<ushort, bool>? CastsShadow = null,
        IReadOnlyList<(ushort Graphic, int X, int Y, int Z, byte Alpha)>? Faded = null);

    // ── projection ──────────────────────────────────────────────────────────────

    Vector3 Project(float x, float y, float z) => Project(x, y, z, Angle);

    Vector3 Project(float x, float y, float z, float angle)
    {
        float dx = x - _cx, dy = y - _cy;
        float a = angle * MathF.PI / 2f;
        float c = MathF.Cos(a), s = MathF.Sin(a);
        float xr = c * dx - s * dy;
        float yr = s * dx + c * dy;
        return new Vector3(22f * (xr - yr), -22f * (xr + yr) + 4f * z, xr + yr + 11f * z - 3000f);
    }

    /// <summary>The 3D depth (nearer = larger) of something standing on tile (x, y, z) and
    /// <paramref name="heightPx"/> tall, matching the billboards: the tile's front corner,
    /// raised by part of the height so it stays in front of walls it stands before.</summary>
    public float DepthOf(float x, float y, float z, float heightPx)
    {
        float best = float.MinValue, bestY = float.MaxValue;
        foreach (var p in new[] { Project(x, y, z), Project(x + 1, y, z), Project(x + 1, y + 1, z), Project(x, y + 1, z) })
            if (p.Y < bestY) { bestY = p.Y; best = p.Z; }
        return best + 3000f + 11f * heightPx / 4f * 0.6f;
    }

    /// <summary>The 3D depth of a point standing on the ground at map position (x, y), height z
    /// (tile coordinates, any fraction).</summary>
    public float DepthAt(float x, float y, float z) => Project(x, y, z).Z + 3000f;

    /// <summary>Depth gained per screen pixel upwards on a vertical plane: 11 per z, 4 px per z.</summary>
    public const float DepthPerPixelUp = 11f / 4f;

    /// <summary>Whether the mouse (TazUO world pixels, as SelectedObject has it) is on a drawn,
    /// opaque pixel of the server item with this serial, as the 3D view shows it.</summary>
    public bool HitTest(uint serial, Point mouse)
    {
        if (!_pickQuads.TryGetValue(serial, out var quads)) return false;
        // world pixel -> render target pixel -> view space
        float mx = mouse.X + _pickRtShift.X - _pickOx - _pickShiftX;
        float my = _pickOy - (mouse.Y + _pickRtShift.Y) - _pickShiftY;
        foreach (var (v, img, padded) in quads)
        {
            for (int t = 0; t + 2 < v.Length; t += 3)
            {
                var a = v[t]; var b = v[t + 1]; var c = v[t + 2];
                if (!Barycentric(new Vector2(mx, my), a, b, c, out float wa, out float wb, out float wc)) continue;
                if (img == null) return true;
                var uv = a.TextureCoordinate * wa + b.TextureCoordinate * wb + c.TextureCoordinate * wc;
                float tw = padded ? img.Width + 2 : img.Width, th = padded ? img.Height + 2 : img.Height;
                int px = (int)(uv.X * tw) - (padded ? 1 : 0), py = (int)(uv.Y * th) - (padded ? 1 : 0);
                if (px >= 0 && py >= 0 && px < img.Width && py < img.Height && (img.Pixels[py * img.Width + px] >> 24) != 0)
                    return true;
            }
        }
        return false;
    }

    static bool Barycentric(Vector2 p, VertexPositionTexture a, VertexPositionTexture b, VertexPositionTexture c,
        out float wa, out float wb, out float wc)
    {
        var pa = new Vector2(a.Position.X, a.Position.Y);
        var pb = new Vector2(b.Position.X, b.Position.Y);
        var pc = new Vector2(c.Position.X, c.Position.Y);
        float d = (pb.Y - pc.Y) * (pa.X - pc.X) + (pc.X - pb.X) * (pa.Y - pc.Y);
        wa = wb = wc = 0;
        if (MathF.Abs(d) < 1e-6f) return false;
        wa = ((pb.Y - pc.Y) * (p.X - pc.X) + (pc.X - pb.X) * (p.Y - pc.Y)) / d;
        wb = ((pc.Y - pa.Y) * (p.X - pc.X) + (pa.X - pc.X) * (p.Y - pc.Y)) / d;
        wc = 1 - wa - wb;
        return wa >= 0 && wb >= 0 && wc >= 0;
    }

    /// <summary>The batcher's depth range that maps <see cref="DepthOf"/> values onto this
    /// renderer's depth buffer (ortho 0..6000, view z = depth - 3000).</summary>
    public const float BatcherNear = -3000f, BatcherFar = 3000f;

    // ── drawing ─────────────────────────────────────────────────────────────────

    /// <summary>Draws the world into the bound render target (size w x h).</summary>
    public void Draw(Frame f, int w, int h)
    {
        EnsureMap(f.MapIndex);
        if (_map == null) return;
        if (_dirty || _builtAngle != Angle || Math.Abs(f.PlayerX - _cx) > Drift || Math.Abs(f.PlayerY - _cy) > Drift
            || f.MaxZ != _maxZ || f.MaxGroundZ != _maxGroundZ || (f.CastsShadow != null) != _builtShadows
            || !FadedKeysMatch(f.Faded))
        {
            _fadedKeys = new HashSet<(int, int, int, ushort)>();
            if (f.Faded != null)
                foreach (var (g, x, y, z, _) in f.Faded)
                    _fadedKeys.Add((x, y, z, g));
            _castsShadow = f.CastsShadow;
            _builtShadows = f.CastsShadow != null;
            (_maxZ, _maxGroundZ) = (f.MaxZ, f.MaxGroundZ);
            (_cx, _cy) = (f.PlayerX, f.PlayerY);
            Build();
        }

        BuildDynamics(f.Items);
        BuildFaded(f.Faded);
        _gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState = RasterizerState.CullNone;
        _gd.BlendState = BlendState.Opaque;
        _gd.SamplerStates[0] = SamplerState.PointClamp;

        // where the anchor's tile corner lands in the render target in the classic view
        float ox = 22f * (_cx - _cy) - f.Offset.X + f.RtShift.X;
        float oy = 22f * (_cx + _cy) - f.Offset.Y - 22f + f.RtShift.Y;
        var proj = Matrix.CreateOrthographicOffCenter(-ox, w - ox, oy - h, oy, 0f, 6000f);

        // turn about the player: keep the player where the classic view has them
        // the same pivot the 2D objects turn about (tile centre plus walking offset)
        float px = WorldRotation.PivotX, py = WorldRotation.PivotY;
        var p0 = Project(px, py, f.PlayerZ, 0);
        var pr = Project(px, py, f.PlayerZ);
        var view = Matrix.CreateTranslation(p0.X - pr.X, p0.Y - pr.Y, 0);
        (_pickOx, _pickOy, _pickShiftX, _pickShiftY, _pickRtShift) = (ox, oy, p0.X - pr.X, p0.Y - pr.Y, f.RtShift);

        _terrainFx.Projection = _spriteFx.Projection = proj;
        _terrainFx.View = _spriteFx.View = view;
        _terrainFx.World = _spriteFx.World = Matrix.Identity;
        DrawBatches(_terrain, _terrainFx);
        if (_shadows.Count > 0 || _dynShadows.Count > 0)
        {
            _shadowFx.Projection = proj;
            _shadowFx.View = view;
            _shadowFx.World = Matrix.Identity;
            _gd.BlendState = BlendState.AlphaBlend;
            _gd.DepthStencilState = DepthStencilState.DepthRead;
            DrawBatches(_shadows, _shadowFx);
            DrawBatches(_dynShadows, _shadowFx);
            _gd.BlendState = BlendState.Opaque;
            _gd.DepthStencilState = DepthStencilState.Default;
        }
        DrawBatches(_sprites, _spriteFx);
        DrawBatches(_dynSprites, _spriteFx);
        if (_faded.Count > 0)
        {
            _fadeFx.Projection = proj;
            _fadeFx.View = view;
            _fadeFx.World = Matrix.Identity;
            _gd.BlendState = BlendState.AlphaBlend;
            _gd.DepthStencilState = DepthStencilState.DepthRead;
            DrawBatches(_faded, _fadeFx);
            _gd.BlendState = BlendState.Opaque;
            _gd.DepthStencilState = DepthStencilState.Default;
        }
        _gd.DepthStencilState = DepthStencilState.None;
    }

    void EnsureMap(int index)
    {
        if (index == _mapIndex) return;
        _mapIndex = index;
        _dirty = true;
        string file = UoFiles.Find(_uoDir, $"map{index}.mul");
        if (!File.Exists(file)) { _map = null; return; }
        var (w, h) = _mapSize(index);
        _map = new UoMap(_uoDir, index, w, h);
    }

    void DrawBatches<T>(Dictionary<Texture2D, List<T>> batches, Effect fx) where T : struct, IVertexType
    {
        foreach (var (tex, verts) in batches)
        {
            if (fx is BasicEffect be) be.Texture = tex;
            if (fx is AlphaTestEffect ae) ae.Texture = tex;
            fx.CurrentTechnique.Passes[0].Apply();
            var arr = verts.ToArray();
            for (int i = 0; i < arr.Length; i += 30000)
                _gd.DrawUserPrimitives(PrimitiveType.TriangleList, arr, i, Math.Min(30000, arr.Length - i) / 3);
        }
    }

    // ── textures ────────────────────────────────────────────────────────────────

    Texture2D? ToTexture(Image? img)
    {
        if (img == null) return null;
        var t = new Texture2D(_gd, img.Width, img.Height);
        t.SetData(img.Pixels);
        return t;
    }

    Texture2D? Padded(Image img)
    {
        var px = new uint[(img.Width + 2) * (img.Height + 2)];
        for (int y = 0; y < img.Height; y++)
            Array.Copy(img.Pixels, y * img.Width, px, (y + 1) * (img.Width + 2) + 1, img.Width);
        var t = new Texture2D(_gd, img.Width + 2, img.Height + 2);
        t.SetData(px);
        return t;
    }

    Texture2D? Cached(Dictionary<int, Texture2D?> cache, int id, Func<Image?> load)
    {
        if (!cache.TryGetValue(id, out var t)) cache[id] = t = ToTexture(load());
        return t;
    }

    void Add(Dictionary<Texture2D, List<VertexPositionTexture>> batches, Texture2D tex, params VertexPositionTexture[] v)
    {
        if (_pickSerial != 0 && ReferenceEquals(batches, _dynSprites))
        {
            if (!_pickQuads.TryGetValue(_pickSerial, out var list))
                _pickQuads[_pickSerial] = list = new List<(VertexPositionTexture[], Image?, bool)>();
            // padded plane textures carry a 1 px frame: undo it for the pixel test
            bool padded = _pickImage != null && tex.Width == _pickImage.Width + 2;
            list.Add((v, _pickImage, padded));
        }
        AddRaw(batches, tex, v);
    }

    static void Add<T>(Dictionary<Texture2D, List<T>> batches, Texture2D tex, params T[] v) where T : struct
        => AddRaw(batches, tex, v);

    static void AddRaw<T>(Dictionary<Texture2D, List<T>> batches, Texture2D tex, T[] v)
    {
        if (!batches.TryGetValue(tex, out var l)) batches[tex] = l = new List<T>();
        l.AddRange(v);
    }

    // ── terrain light (after CentrED# / kaczy93 UO3D-Sandbox) ──────────────────────

    float CornerLight(int x, int y)
    {
        const float edge = 31.1127f, zpx = 4f;
        float zl = _map!.Land(x - 1, y).Z, zr = _map.Land(x + 1, y).Z;
        float zu = _map.Land(x, y - 1).Z, zd = _map.Land(x, y + 1).Z;
        var n = Vector3.Normalize(new Vector3(-(zr - zl) * zpx / (2 * edge), -(zd - zu) * zpx / (2 * edge), 1));
        float b = MathF.Max(Vector3.Dot(n, LightDir), 0) / 2f + 0.5f;
        const float flat = 0.85355339f, bright = 1.5f;
        float lit = b + (bright * (b - flat) - (b - flat));
        return Math.Clamp(lit / flat, 0.3f, LightMax);
    }

    VertexPositionColorTexture Lit(int x, int y, sbyte z, Vector2 uv)
    {
        float l = CornerLight(x, y) / LightMax;
        return new VertexPositionColorTexture(Project(x, y, z), new Color(l, l, l), uv);
    }

    // ── geometry ────────────────────────────────────────────────────────────────

    void Build()
    {
        var map = _map!;
        _terrain.Clear();
        _sprites.Clear();
        _shadows.Clear();
        int x0 = _cx - Radius, x1 = _cx + Radius, y0 = _cy - Radius, y1 = _cy + Radius;

        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            var l = map.Land(x, y);
            if (l.Z > _maxGroundZ) continue;
            var tex = Cached(_landArt, l.Id, () => UoImages.Land(_art, l.Id));
            if (tex == null) continue;
            sbyte zt = l.Z, ze = map.Land(x + 1, y).Z, zs = map.Land(x, y + 1).Z, zse = map.Land(x + 1, y + 1).Z;
            var a = Lit(x, y, zt, new(0.5f, 0));
            var b = Lit(x + 1, y, ze, new(1, 0.5f));
            var c = Lit(x + 1, y + 1, zse, new(0.5f, 1));
            var d = Lit(x, y + 1, zs, new(0, 0.5f));
            Add(_terrain, tex, a, b, c, a, c, d);
        }

        var parts = new List<Piece>();
        // walls first: furniture checks them below (and the server items' walls of the last frame)
        _staticWalls.Clear();
        _staticTwins.Clear();
        for (int by = Math.Max(0, y0 >> 3); by <= (y1 - 1) >> 3; by++)
        for (int bx = Math.Max(0, x0 >> 3); bx <= (x1 - 1) >> 3; bx++)
        foreach (var s in map.StaticsBlock(bx, by))
        {
            if (IsWallPiece(s.Id))
                _staticWalls.Add((bx * 8 + s.X, by * 8 + s.Y, s.Z));
            // ovens and fireplaces carry the wall flag too
            if (UseTwins && s.Id > 1 && TwinOf(s.Id) != null)
                _staticTwins[(bx * 8 + s.X, by * 8 + s.Y, s.Z)] = s.Id;
        }
        for (int by = Math.Max(0, y0 >> 3); by <= (y1 - 1) >> 3; by++)
        for (int bx = Math.Max(0, x0 >> 3); bx <= (x1 - 1) >> 3; bx++)
        foreach (var s in map.StaticsBlock(bx, by))
        {
            int x = bx * 8 + s.X, y = by * 8 + s.Y;
            if (x < x0 || x >= x1 || y < y0 || y >= y1 || s.Id <= 1 || _tiles.NoDraw(s.Id) || s.Z >= _maxZ) continue;
            if (_fadedKeys.Contains((x, y, s.Z, s.Id))) continue;
            PlaceStatic(s.Id, x, y, s.Z, parts);
        }
        foreach (var cluster in Composite.Clusters(parts))
        {
            var key = string.Join(";", cluster.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ThenBy(p => p.Id)
                .Select(p => $"{p.X},{p.Y},{p.Z},{p.Id}"));
            if (!_composites.TryGetValue(key, out var ci))
            {
                var c = Composite.Compose(cluster);
                _composites[key] = ci = (c, ToTexture(c.Img)!);
            }
            AddComposite(ci, cluster);
        }
        _builtAngle = Angle;
        _dirty = false;
    }

    /// <summary>One static (map or server item) into <see cref="_target"/>: 3D planes when its
    /// shape is known, else a billboard; ship parts go to <paramref name="parts"/>.</summary>
    void PlaceStatic(ushort id, int x, int y, int z, List<Piece>? parts)
    {
        if (!_staticImg.TryGetValue(id, out var img)) _staticImg[id] = img = UoImages.Static(_art, id);
        if (img == null) return;
        int prio = _tiles.Has(id, TileFlag.Background) || _tiles.Has(id, TileFlag.Surface) ? 0 : 1;
        if (parts != null && Composite.IsPart(_tiles, id))
        {
            parts.Add(new Piece(x, y, z, id, img, prio));
            return;
        }
        var tex = Cached(_staticArt, id, () => img);
        if (tex == null) return;
        var run = UseTwins && Mirrored && TwinOf(id) != null ? TwinRun(id, x, y, z) : default;
        // things standing or hanging against a wall: nothing to show from behind
        if (ShowsBack(id, img, x, y, z, run.Facing)) return;
        // from the side views a south-facing piece looks like its east-facing twin in the classic
        // view (and the other way round): use that art, as it is drawn, instead of a mirror image
        if (UseTwins && Mirrored && TwinOf(id) is { } twin && _staticImg.TryGetValue(twin, out var twinImg) && twinImg != null
            && Cached(_staticArt, twin, () => twinImg) is { } twinTex)
        {
            // a row of pieces (oven, fireplace) turned onto the twin's axis can come out in the
            // twin's reverse order: each piece's twin then goes to the mirrored place in the row
            if (run.Reversed)
            {
                if (run.AlongX) x = run.Lo + run.Hi - x;
                else y = run.Lo + run.Hi - y;
            }
            AddBillboard(twinTex, x, y, z, 0, 0, twinTex.Width, twinTex.Height,
                0.02f * prio + 0.0001f * _tiles.StaticHeight[id], mirrorable: false);
            return;
        }
        var shape = ShapeOf(id, img);
        if (shape != null)
        {
            if (!_paddedArt.TryGetValue(id, out var ptex)) _paddedArt[id] = ptex = Padded(img);
            float sw = img.Width, sh = img.Height;
            foreach (var q in shape)
            {
                if (q.EndFace && Angle != 0) continue;
                var v = new VertexPositionTexture[4];
                for (int i = 0; i < 4; i++)
                {
                    var uv = new Vector2((q.Uv[i].X * sw + 1) / (sw + 2), (q.Uv[i].Y * sh + 1) / (sh + 2));
                    v[i] = new VertexPositionTexture(Project(x + q.Corners[i].X, y + q.Corners[i].Y, z + q.Corners[i].Z), uv);
                }
                Add(_target, ptex!, v[0], v[1], v[2], v[0], v[2], v[3]);
            }
            return;
        }
        // furniture standing against a wall: from behind there is no art, and the classic sprite
        // would poke through the wall - leave it out while the camera looks at its back
        if (ShowsBack(id, img, x, y, z)) return;
        AddBillboard(tex, x, y, z, 0, 0, tex.Width, tex.Height, 0.02f * prio + 0.0001f * _tiles.StaticHeight[id]);
        if (_castsShadow != null && _castsShadow(id))
            AddShadow(tex, x, y, z, tex.Width, tex.Height);
    }

    /// <summary>The sprite laid on the ground behind its foot, away from the sun: the foot edge
    /// runs along the screen's horizontal, the top falls northwards by a share of the height.</summary>
    void AddShadow(Texture2D tex, int x, int y, int z, int w, int h)
    {
        // the billboard's foot: the tile corner lowest on screen, halfway to the tile centre
        var corners = new[] { new Vector2(x, y), new Vector2(x + 1, y), new Vector2(x + 1, y + 1), new Vector2(x, y + 1) };
        Vector2 foot = corners[0];
        float lowest = float.MaxValue;
        foreach (var c in corners)
        {
            float sy = Project(c.X, c.Y, z).Y;
            if (sy < lowest) { lowest = sy; foot = c; }
        }
        foot = (foot + new Vector2(x + 0.5f, y + 0.5f)) / 2f;
        // one screen pixel along the screen's horizontal, as a map vector
        float a = Angle * MathF.PI / 2f, c0 = MathF.Cos(a), s0 = MathF.Sin(a);
        var screenRight = new Vector2(c0 / 44f - s0 / 44f, -s0 / 44f - c0 / 44f);
        var sun = new Vector2(0.35f, -1f); // shadows fall north, a little east
        float length = h / 40f;
        var bl = foot - screenRight * (w / 2f);
        var br = foot + screenRight * (w / 2f);
        var tl = bl + sun * length;
        var tr = br + sun * length;
        float gz = z + 0.2f;
        bool mirror = Mirrored;
        float u0 = mirror ? 1 : 0, u1 = 1 - u0;
        var v0 = new VertexPositionTexture(Project(tl.X, tl.Y, gz), new Vector2(u0, 0));
        var v1 = new VertexPositionTexture(Project(tr.X, tr.Y, gz), new Vector2(u1, 0));
        var v2 = new VertexPositionTexture(Project(br.X, br.Y, gz), new Vector2(u1, 1));
        var v3 = new VertexPositionTexture(Project(bl.X, bl.Y, gz), new Vector2(u0, 1));
        Add(_shadowTarget, tex, v0, v1, v2, v0, v2, v3);
    }

    /// <summary>Server items and multi parts of this frame (graphic, x, y, z), drawn with the world.</summary>
    void BuildDynamics(IReadOnlyList<(ushort Graphic, int X, int Y, int Z, uint Serial)> items)
    {
        _dynSprites.Clear();
        _dynShadows.Clear();
        _pickQuads.Clear();
        _target = _dynSprites;
        _shadowTarget = _dynShadows;
        var parts = new List<Piece>();
        _dynWalls.Clear();
        _dynTwins.Clear();
        foreach (var (g, x, y, z, _) in items)
            if (IsWallPiece(g))
                _dynWalls.Add((x, y, z));
        foreach (var (g, x, y, z, _) in items)
            if (UseTwins && g > 1 && TwinOf(g) != null)
                _dynTwins[(x, y, z)] = g;
        var partSerials = new Dictionary<(int, int, int, ushort), uint>();
        foreach (var (g, x, y, z, serial) in items)
            if (g > 1 && !_tiles.NoDraw(g))
            {
                if (Composite.IsPart(_tiles, g))
                {
                    if (!_staticImg.TryGetValue(g, out var pimg)) _staticImg[g] = pimg = UoImages.Static(_art, g);
                    if (pimg != null)
                    {
                        int prio = _tiles.Has(g, TileFlag.Background) || _tiles.Has(g, TileFlag.Surface) ? 0 : 1;
                        parts.Add(new Piece(x, y, z, g, pimg, prio));
                        partSerials[(x, y, z, g)] = serial;
                        continue;
                    }
                }
                _pickSerial = serial;
                _pickImage = _staticImg.TryGetValue(g, out var im) ? im : null;
                PlaceStatic(g, x, y, z, null);
                _pickSerial = 0;
            }
        foreach (var cluster in Composite.Clusters(parts))
        {
            // clicks anywhere on the picture hit its first piece
            _pickSerial = partSerials[(cluster[0].X, cluster[0].Y, cluster[0].Z, cluster[0].Id)];
            _pickImage = null;
            AddComposite(ComposeCached(cluster), cluster);
            _pickSerial = 0;
            foreach (var p in cluster.Skip(1))
                if (partSerials.TryGetValue((p.X, p.Y, p.Z, p.Id), out var s) && _pickQuads.TryGetValue(
                        partSerials[(cluster[0].X, cluster[0].Y, cluster[0].Z, cluster[0].Id)], out var q))
                    _pickQuads[s] = q;
        }
        _target = _sprites;
        _shadowTarget = _shadows;
    }

    bool FadedKeysMatch(IReadOnlyList<(ushort Graphic, int X, int Y, int Z, byte Alpha)>? faded)
    {
        if (faded == null || faded.Count == 0) return _fadedKeys.Count == 0;
        var now = new HashSet<(int, int, int, ushort)>();
        foreach (var (g, x, y, z, _) in faded)
            now.Add((x, y, z, g));
        return now.SetEquals(_fadedKeys);
    }

    /// <summary>The faded objects of this frame, with their alpha (premultiplied for AlphaBlend).</summary>
    void BuildFaded(IReadOnlyList<(ushort Graphic, int X, int Y, int Z, byte Alpha)>? faded)
    {
        _faded.Clear();
        if (faded == null || faded.Count == 0) return;
        var tmp = new Dictionary<Texture2D, List<VertexPositionTexture>>();
        var saveTarget = _target;
        var saveShadow = _castsShadow;
        _castsShadow = null;
        foreach (var (g, x, y, z, alpha) in faded)
        {
            if (alpha == 0 || g <= 1 || _tiles.NoDraw(g)) continue;
            tmp.Clear();
            _target = tmp;
            PlaceStatic(g, x, y, z, null);
            float a = alpha / 255f;
            var col = new Color(a, a, a, a);
            foreach (var (tex, verts) in tmp)
            {
                if (!_faded.TryGetValue(tex, out var list)) _faded[tex] = list = new List<VertexPositionColorTexture>();
                foreach (var v in verts) list.Add(new VertexPositionColorTexture(v.Position, col, v.TextureCoordinate));
            }
        }
        _target = saveTarget;
        _castsShadow = saveShadow;
    }

    /// <summary>A camera-facing sprite standing on the tile's front corner (shifted by dx /
    /// up), with depth rising like a vertical plane.</summary>
    /// <summary>Odd quarter turns (nearest to the shown angle): billboards are drawn mirrored.</summary>
    bool Mirrored => ((int)MathF.Round(Angle) % 2 + 2) % 2 == 1;

    /// <summary>A composed multi-piece object (ship, ankh, shrine mosaic) as one picture that keeps the
    /// classic look: never mirrored, and lying flat (no depth rise) when all its pieces are flat.</summary>
    void AddComposite((CompositeImage, Texture2D) ci, List<Piece> cluster)
    {
        var (comp, tex) = ci;
        bool flat = cluster.All(p => _tiles.StaticHeight[p.Id] == 0);
        AddBillboard(tex, comp.AnchorX, comp.AnchorY, comp.AnchorZ,
            comp.Left + tex.Width / 2f, comp.BottomUp, tex.Width, tex.Height, flat ? 0.01f : 0.03f,
            mirrorable: false, flat: flat);
    }

    (CompositeImage, Texture2D) ComposeCached(List<Piece> cluster)
    {
        var key = string.Join(";", cluster.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ThenBy(p => p.Id)
            .Select(p => $"{p.X},{p.Y},{p.Z},{p.Id}"));
        if (!_composites.TryGetValue(key, out var ci))
        {
            var c = Composite.Compose(cluster);
            _composites[key] = ci = (c, ToTexture(c.Img)!);
        }
        return ci;
    }

    void AddBillboard(Texture2D tex, int x, int y, int z, float dx, float up, int w, int h, float bias,
        bool mirrorable = true, bool flat = false)
    {
        Vector3 bottom = Project(x, y, z);
        foreach (var p in new[] { Project(x + 1, y, z), Project(x + 1, y + 1, z), Project(x, y + 1, z) })
            if (p.Y < bottom.Y) bottom = p;
        // From the south-west / north-east the camera sees an object's other front diagonal: the
        // classic sprite mirrored (most objects are left-right symmetric; backs do not exist).
        bool mirror = mirrorable && Mirrored;
        if (mirror) dx = -dx;
        float cxs = Project(x + 0.5f, y + 0.5f, z).X + dx;
        float l = cxs - w / 2f, r = cxs + w / 2f, b = bottom.Y + up, t = b + h;
        float depth = bottom.Z + bias, topDepth = flat ? depth : depth + 11f * h / 4f;
        float u0 = mirror ? 1 : 0, u1 = 1 - u0;
        var v0 = new VertexPositionTexture(new Vector3(l, t, topDepth), new Vector2(u0, 0));
        var v1 = new VertexPositionTexture(new Vector3(r, t, topDepth), new Vector2(u1, 0));
        var v2 = new VertexPositionTexture(new Vector3(r, b, depth), new Vector2(u1, 1));
        var v3 = new VertexPositionTexture(new Vector3(l, b, depth), new Vector2(u0, 1));
        Add(_target, tex, v0, v1, v2, v0, v2, v3);
    }

    readonly Dictionary<int, int> _facing = new();
    /// <summary>Side views draw the twin graphic of wall pieces instead of a mirror image.</summary>
    const bool UseTwins = true;
    readonly Dictionary<ushort, ushort?> _counterparts = new();

    /// <summary>Wall pieces with a twin, by tile: map statics and server items.</summary>
    readonly Dictionary<(int, int, int), ushort> _staticTwins = new(), _dynTwins = new();

    /// <summary>A row of same-named pieces with twins along a wall, on x or y (ovens, fireplaces:
    /// the main piece and a side piece next to it, to the west or north).</summary>
    readonly record struct Row(bool AlongX, int Lo, int Hi, bool Reversed)
    {
        /// <summary>Front of the row as <see cref="Shapes.FacingOf"/> gives it (south +1, east -1):
        /// away from the wall it runs along; null for a single piece.</summary>
        public int? Facing => Lo == Hi ? null : AlongX ? 1 : -1;
    }

    Row TwinRun(ushort id, int x, int y, int z)
    {
        string name = _tiles.StaticName[id];
        bool Same(int tx, int ty) =>
            (_staticTwins.TryGetValue((tx, ty, z), out var o) || _dynTwins.TryGetValue((tx, ty, z), out o))
            && _tiles.StaticName[o] == name;
        int xl = x, xh = x, yl = y, yh = y;
        while (Same(xl - 1, y)) xl--;
        while (Same(xh + 1, y)) xh++;
        while (Same(x, yl - 1)) yl--;
        while (Same(x, yh + 1)) yh++;
        if (xl == xh && yl == yh) return new Row(true, x, x, false);
        bool alongX = xh - xl >= yh - yl;
        // the classic pieces run from the main piece towards -x or -y; a quarter turn sends a row
        // along y onto +x (and a row along x, after three, onto +y): the twins' order reversed
        int q = ((int)MathF.Round(Angle) % 4 + 4) % 4;
        return alongX ? new Row(true, xl, xh, q == 3) : new Row(false, yl, yh, q == 1);
    }

    int FacingOfId(ushort id)
    {
        if (_facing.TryGetValue(id, out int f)) return f;
        if (!_staticImg.TryGetValue(id, out var img)) _staticImg[id] = img = UoImages.Static(_art, id);
        return _facing[id] = img == null ? 0 : Shapes.FacingOf(img);
    }

    /// <summary>The same piece facing the other way (south / east twin): same name and height, a
    /// nearby graphic id, and its art close to the original's mirror image (and further from the
    /// original itself - animation frames of the same piece look identical). Only for things that
    /// stand or hang against a wall.</summary>
    /// <summary>The twin to draw in the side views: not for furniture with a 3D block (bookcases),
    /// those turn as they are.</summary>
    ushort? TwinOf(ushort id) =>
        Counterpart(id) is { } t && !(FurnitureBlocks.Any(_tiles.StaticName[id].ToLowerInvariant().Contains)
            && _staticImg.TryGetValue(id, out var img) && img != null && ShapeOf(id, img) != null) ? t : null;

    ushort? Counterpart(ushort id)
    {
        if (_counterparts.TryGetValue(id, out var c)) return c;
        ushort? best = null;
        string name = _tiles.StaticName[id];
        if (HideBehindWall.Any(name.ToLowerInvariant().Contains)
            && (_staticImg.TryGetValue(id, out var orig) ? orig : _staticImg[id] = UoImages.Static(_art, id)) is { } img)
        {
            double bestDiff = 25;
            for (int cand = Math.Max(2, id - 16); cand <= Math.Min(_tiles.StaticName.Length - 1, id + 16); cand++)
            {
                var cid = (ushort)cand;
                if (cid == id || _tiles.StaticName[cid] != name || _tiles.StaticHeight[cid] != _tiles.StaticHeight[id]) continue;
                if (!_staticImg.TryGetValue(cid, out var other)) _staticImg[cid] = other = UoImages.Static(_art, cid);
                if (other == null || Math.Abs(other.Width - img.Width) > 4 || Math.Abs(other.Height - img.Height) > 4) continue;
                double mirrored = ArtDifference(img, other, true), same = ArtDifference(img, other, false);
                if (mirrored < same - 1.0 && mirrored < bestDiff) { bestDiff = mirrored; best = cid; }
            }
        }
        return _counterparts[id] = best;
    }

    /// <summary>How different two sprites look, bottom-centre aligned, optionally with
    /// <paramref name="a"/> mirrored: mean colour difference (5-bit channels summed) over the pixels
    /// both cover, plus a penalty for pixels only one covers.</summary>
    static double ArtDifference(Image a, Image b, bool mirrorA)
    {
        int w = Math.Max(a.Width, b.Width), h = Math.Max(a.Height, b.Height);
        double sum = 0;
        int both = 0, one = 0;
        uint Px(Image im, int px, int py, bool mirror)
        {
            int ix = px - (w - im.Width) / 2, iy = py - (h - im.Height);
            if (ix < 0 || iy < 0 || ix >= im.Width || iy >= im.Height) return 0;
            if (mirror) ix = im.Width - 1 - ix;
            return im.Pixels[iy * im.Width + ix];
        }
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            uint pa = Px(a, x, y, mirrorA), pb = Px(b, x, y, false);
            bool oa = (pa >> 24) != 0, ob = (pb >> 24) != 0;
            if (oa && ob)
            {
                for (int sh = 0; sh < 24; sh += 8)
                    sum += Math.Abs((int)((pa >> sh) & 0xFF) - (int)((pb >> sh) & 0xFF)) * 31 / 255.0;
                both++;
            }
            else if (oa || ob) one++;
        }
        return (both == 0 ? 99 : sum / both) + one * 30.0 / (both + one + 1);
    }

    /// <summary>Whether the camera sees the back of a wall-standing furniture sprite.</summary>
    readonly HashSet<(int, int, int)> _staticWalls = new(), _dynWalls = new();

    bool IsWallPiece(ushort id) => _tiles.Has(id, TileFlag.Wall) || _tiles.Has(id, TileFlag.Window);

    /// <summary>A wall on tile (x, y) within the furniture's height range.</summary>
    bool WallAt(int x, int y, int z)
    {
        // hangings sit higher on the wall than the wall's base
        for (int dz = -20; dz <= 20; dz++)
            if (_staticWalls.Contains((x, y, z + dz)) || _dynWalls.Contains((x, y, z + dz)))
                return true;
        return false;
    }

    bool ShowsBack(ushort id, Image img, int x, int y, int z, int? rowFacing = null)
    {
        if (Angle == 0 || !HideBehindWall.Any(_tiles.StaticName[id].ToLowerInvariant().Contains)) return false;
        if (!_facing.TryGetValue(id, out int facing)) _facing[id] = facing = Shapes.FacingOf(img);
        // the pieces of a row (a narrow side piece has no clear foot line) face the way the row does
        if (rowFacing is { } rf) facing = rf;
        if (facing == 0) return false;
        // direction to the viewer on the map (classic view: south-east, (1, 1))
        float a = Angle * MathF.PI / 2f, c = MathF.Cos(a), s = MathF.Sin(a);
        var viewer = new Vector2(c + s, c - s);
        var front = facing > 0 ? new Vector2(0, 1) : new Vector2(1, 0);
        if (Vector2.Dot(viewer, front) >= -0.2f) return false;
        // only against a wall that hides it anyway: on its tile or the tile behind it
        int bx = facing > 0 ? x : x - 1, by = facing > 0 ? y - 1 : y;
        return WallAt(x, y, z) || WallAt(bx, by, z);
    }

    /// <summary>Sprites that stand or hang against a wall: hidden while the camera looks at their back.</summary>
    static readonly string[] HideBehindWall =
        { "bookcase", "bookshelf", "shelf", "armoire", "dresser", "drawers", "cabinet", "wardrobe",
          "curtain", "drape", "tapestr", "fireplace", "oven", "hearth" };

    static readonly string[] FurnitureBlocks = { "bookcase", "bookshelf", "shelf", "armoire", "dresser", "drawers", "cabinet", "wardrobe" };

    /// <summary>3D planes of a static (relative to its tile), or null for a billboard.</summary>
    List<Quad>? ShapeOf(ushort id, Image img)
    {
        if (_shapes.TryGetValue(id, out var q)) return q;
        string name = _tiles.StaticName[id].ToLowerInvariant();
        if (_tiles.Has(id, TileFlag.Wet) && _tiles.StaticHeight[id] == 0 || name.Contains("lilly") || name.Contains("lily"))
            q = new List<Quad> { Shapes.Flat(0, 0, 0, img) };
        // tall furniture against a wall (bookcases, some chests of drawers): a block like a
        // wall piece; the ones that do not fit cleanly stay billboards
        // stair steps: blocks (a sloped plane over the tile loses the riser)
        else if (name.Contains("stairs") && _tiles.Has(id, TileFlag.Surface) && Shapes.Block(0, 0, 0, img) is { } block)
            q = block;
        else if (FurnitureBlocks.Any(name.Contains))
            // only the ones with a clean foot line (bookcases); carved cabinets look better as sprites
            q = Shapes.Wall(0, 0, 0, img, _tiles.StaticHeight[id]);
        else if (name.Contains("banister") || name.Contains("railing"))
            q = Shapes.Wall(0, 0, 0, img, _tiles.StaticHeight[id]);
        else if (_tiles.Has(id, TileFlag.Wall) || _tiles.Has(id, TileFlag.Door) || _tiles.Has(id, TileFlag.Window))
            q = Shapes.Wall(0, 0, 0, img, _tiles.StaticHeight[id]);
        // flat ground pieces (height 0): the whole sprite on the ground; shrine mosaics and other
        // art larger than its tile stays in one piece
        else if (_tiles.StaticHeight[id] == 0 && !_tiles.Has(id, TileFlag.Roof)
            && (_tiles.Has(id, TileFlag.Surface) || _tiles.Has(id, TileFlag.Background))
            && (img.Width > 44 || img.Height > 44))
            q = new List<Quad> { Shapes.FlatSprite(0, 0, 0, img, 0.1f) };
        else if (_tiles.Has(id, TileFlag.Roof) || _tiles.Has(id, TileFlag.Surface))
        {
            q = Shapes.Plane(0, 0, 0, _tiles.StaticHeight[id], img);
            if (_tiles.Has(id, TileFlag.Roof) && (q == null || Shapes.LastFit < 0.85))
                q = Shapes.Wall(0, 0, 0, img, _tiles.StaticHeight[id]) ?? q;
        }
        return _shapes[id] = q;
    }
}
