#nullable enable
// A small compass in the corner of the world view: the red needle points to map north,
// an "N" sits at its tip. Drawn straight into the world render target.

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Uo3d;

public sealed class Compass
{
    readonly GraphicsDevice _gd;
    readonly BasicEffect _fx;

    public Compass(GraphicsDevice gd)
    {
        _gd = gd;
        _fx = new BasicEffect(gd) { VertexColorEnabled = true, TextureEnabled = false };
    }

    /// <summary>Draws at <paramref name="center"/> (render target px) with radius <paramref name="r"/>.</summary>
    public void Draw(int rtW, int rtH, Vector2 center, float r)
    {
        var tris = new System.Collections.Generic.List<VertexPositionColor>();
        void Tri(Vector2 a, Vector2 b, Vector2 c, Color col)
        {
            tris.Add(new(new Vector3(a, 0), col));
            tris.Add(new(new Vector3(b, 0), col));
            tris.Add(new(new Vector3(c, 0), col));
        }
        void Line(Vector2 a, Vector2 b, float w, Color col)
        {
            var d = b - a;
            if (d.LengthSquared() < 1e-4f) return;
            d.Normalize();
            var n = new Vector2(-d.Y, d.X) * (w / 2);
            Tri(a - n, a + n, b + n, col);
            Tri(a - n, b + n, b - n, col);
        }

        // disc and rim
        const int seg = 32;
        var bg = new Color(15, 18, 24, 255);
        var rim = new Color(150, 150, 150, 255);
        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
            var p0 = center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r;
            var p1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r;
            Tri(center, p0, p1, bg);
            Line(p0, p1, Math.Max(1.5f, r / 14f), rim);
        }

        var north = WorldRotation.NorthOnScreen();
        var side = new Vector2(-north.Y, north.X);
        float len = r * 0.62f, half = r * 0.17f;
        Tri(center + north * len, center + side * half, center - side * half, new Color(210, 40, 40, 255));
        Tri(center - north * len, center - side * half, center + side * half, new Color(225, 225, 225, 255));

        // "N" outside the needle tip, upright on screen
        var at = center + north * (r + r * 0.42f);
        float h = r * 0.42f, w = r * 0.3f, lw = Math.Max(1.5f, r / 10f);
        var white = new Color(235, 235, 235, 255);
        var bl = at + new Vector2(-w / 2, h / 2);
        var tl = at + new Vector2(-w / 2, -h / 2);
        var br = at + new Vector2(w / 2, h / 2);
        var tr = at + new Vector2(w / 2, -h / 2);
        Line(bl, tl, lw, white);
        Line(tl, br, lw, white);
        Line(br, tr, lw, white);

        _gd.DepthStencilState = DepthStencilState.None;
        _gd.BlendState = BlendState.Opaque;
        _gd.RasterizerState = RasterizerState.CullNone;
        _fx.Projection = Matrix.CreateOrthographicOffCenter(0, rtW, rtH, 0, 0, 1);
        _fx.View = _fx.World = Matrix.Identity;
        _fx.CurrentTechnique.Passes[0].Apply();
        var arr = tris.ToArray();
        _gd.DrawUserPrimitives(PrimitiveType.TriangleList, arr, 0, arr.Length / 3);
    }
}
