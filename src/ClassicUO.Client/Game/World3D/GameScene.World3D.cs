// SPDX-License-Identifier: BSD-2-Clause
// The rotatable 3D view (Game/World3D) inside the game scene: turning keys, drawing the world
// through the 3D renderer while turned, and what the scene's object lists need for it.

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Managers.Hotkeys;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace ClassicUO.Game.Scenes
{
    public partial class GameScene
    {
        private Uo3d.WorldRenderer3D _world3D;
        private long _world3DLastTick;

        /// <summary>Advances the turn the hotkeys (Hotkeys &gt; 3D View) asked for and sets the pivot
        /// (the player) before the object screen positions are computed, so overheads and picking
        /// follow the turned view.</summary>
        private void UpdateWorld3DState()
        {
            if (ProfileManager.CurrentProfile?.Enable3DRotation != true)
            {
                // feature off: back to the classic view at once, keys do nothing
                if (Uo3d.WorldRotation.Turned)
                {
                    Uo3d.WorldRotation.Reset();
                    UpdateDrawPosition = true;
                }
                return;
            }
            Uo3d.WorldRotation.Hold = (HotKeys.IsPressed(HotKeyRegistrar.World3DHoldClockwiseId) ? 1 : 0)
                - (HotKeys.IsPressed(HotKeyRegistrar.World3DHoldCounterClockwiseId) ? 1 : 0);
            float before = Uo3d.WorldRotation.Angle;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float seconds = _world3DLastTick == 0 ? 0f : (float)((now - _world3DLastTick) / (double)System.Diagnostics.Stopwatch.Frequency);
            _world3DLastTick = now;
            Uo3d.WorldRotation.Step(Math.Min(seconds, 0.1f));
            if (Uo3d.WorldRotation.Angle != before)
                UpdateDrawPosition = true;
            var player = _world.Player;
            if (player != null && Uo3d.WorldRotation.Turned)
            {
                float ofx = player.Offset.X, ofy = player.Offset.Y;
                Uo3d.WorldRotation.PivotX = player.X + 0.5f + (ofx + ofy) / 44f;
                Uo3d.WorldRotation.PivotY = player.Y + 0.5f + (ofy - ofx) / 44f;
                // every object's screen position depends on the moving pivot
                UpdateDrawPosition = true;
            }
        }

        /// <summary>When the view is turned, the 3D renderer draws the world into the bound world
        /// target and the 2D draw is skipped.</summary>
        private bool DrawWorld3D(UltimaBatcher2D batcher, ref Matrix matrix)
        {
            GraphicsDevice gd = batcher.GraphicsDevice;
            if (!Uo3d.WorldRotation.Turned || _world.Player == null)
                return false;
            _world3D ??= new Uo3d.WorldRenderer3D(
                gd,
                Settings.GlobalSettings.ResolvedUltimaOnlineDirectory,
                i => (Client.Game.UO.FileManager.Maps.MapsDefaultSize[i, 0], Client.Game.UO.FileManager.Maps.MapsDefaultSize[i, 1]));
            _world3D.Angle = Uo3d.WorldRotation.Angle;

            var player = _world.Player;
            var items = new List<(ushort, int, int, int, uint)>();
            var faded = new List<(ushort, int, int, int, byte)>();
            foreach (var list in new[] { _renderListStatics, _renderListAnimations, _renderListTransparentObjects })
                foreach (GameObject obj in list)
                {
                    bool in3D = obj is Static || IsDrawnIn3D(obj);
                    if (!in3D) continue;
                    // circle of transparency / translucent tiles: TazUO's own per-object alpha
                    if (obj.AlphaHue < 255)
                        faded.Add((obj.Graphic, obj.X, obj.Y, obj.Z, obj.AlphaHue));
                    else if (obj is not Static)
                        items.Add((obj.Graphic, obj.X, obj.Y, obj.Z, obj is Entity ent ? ent.Serial : 0u));
                }
            // left out by TazUO altogether (fully faded, hidden roofs): out of the 3D map geometry too
            foreach (GameObject obj in _hidden3D)
                if (obj is Static || IsDrawnIn3D(obj))
                    faded.Add((obj.Graphic, obj.X, obj.Y, obj.Z, 0));
            var frame = new Uo3d.WorldRenderer3D.Frame(
                _world.MapIndex, player.X, player.Y, player.Z, player.Offset, _offset, new Vector2(matrix.M41, matrix.M42), items,
                _maxZ, _maxGroundZ,
                ProfileManager.CurrentProfile is { ShadowsEnabled: true, ShadowsStatics: true } ? CastsShadow3D : null,
                faded);
            AddLights3D();
            _world3D.Draw(frame, _worldRenderTarget.Width, _worldRenderTarget.Height);
            DrawDynamicsOver3D(batcher, ref matrix);
            DrawCompass(gd);
            return true;
        }

        private Uo3d.Compass _compass;

        /// <summary>The compass in the top-right corner of the visible part of the world target.</summary>
        private void DrawCompass(GraphicsDevice gd)
        {
            if (_worldRenderTarget == null || _worldSrcRect.Width <= 0 || Camera.Bounds.Width <= 0
                || ProfileManager.CurrentProfile?.Enable3DRotation != true)
                return;
            _compass ??= new Uo3d.Compass(gd);
            float scale = _worldSrcRect.Width / (float)Camera.Bounds.Width; // render target px per screen px
            float r = 22f * scale;
            var center = new Vector2(_worldSrcRect.Right - 48f * scale, _worldSrcRect.Top + 48f * scale);
            _compass.Draw(_worldRenderTarget.Width, _worldRenderTarget.Height, center, r);
        }

        /// <summary>Statics TazUO draws with a shadow: trees, foliage, rocks.</summary>
        private static bool CastsShadow3D(ushort graphic) =>
            Client.Game.UO.FileManager.TileData.StaticData[graphic].IsFoliage
            || StaticFilters.IsTree(graphic, out _)
            || StaticFilters.IsRock(graphic);

        /// <summary>TazUO registers lights while drawing an object's 2D sprite; statics, multis and
        /// world items are not drawn in 2D while turned, so their lights are registered here, at
        /// the turned screen positions.</summary>
        private void AddLights3D()
        {
            foreach (var list in new[] { _renderListStatics, _renderListAnimations, _renderListTransparentObjects })
                foreach (GameObject obj in list)
                {
                    bool light = obj switch
                    {
                        Static st => st.ItemData.IsLight,
                        Multi mu => mu.ItemData.IsLight,
                        Item it when IsDrawnIn3D(it) => it.ItemData.IsLight,
                        _ => false,
                    };
                    if (light && obj.Z < _maxZ)
                        AddLight(obj, obj, obj.RealScreenPosition.X + 22, obj.RealScreenPosition.Y + 22);
                }
        }

        /// <summary>Server items lying in the world and multi (house) parts go through the 3D renderer
        /// like statics; corpses and items in containers or on mobiles stay 2D.</summary>
        private static bool IsDrawnIn3D(GameObject obj) =>
            obj is Multi || obj is Item it && !it.IsCorpse && !it.IsMulti && it.OnGround;

        /// <summary>Server items, multis, mobiles and effects from TazUO's own render lists, at their
        /// turned screen positions, over the 3D world (own depth, not mixed with the 3D one).</summary>
        private void DrawDynamicsOver3D(UltimaBatcher2D batcher, ref Matrix matrix)
        {
            // Tested against the 3D world's depth: roofs and walls in front hide a mobile. A sprite
            // gets the depth of a vertical plane: the transform adds depth for every pixel up
            // (z += -y * DepthPerPixelUp) and each object's depth is anchored at its feet, so a
            // wall right behind the mobile stays behind it and one in front of it covers it.
            Matrix vertical = matrix;
            vertical.M23 = -Uo3d.WorldRenderer3D.DepthPerPixelUp;
            batcher.Begin(null, vertical);
            batcher.SetSampler(SamplerState.PointClamp);
            // test the 3D world's depth but write none: a mobile's body and clothing layers lie at the
            // same depth and would fight; they keep TazUO's draw order instead
            batcher.SetStencil(DepthStencilState.DepthRead);
            batcher.SetProjectionDepthRange(Uo3d.WorldRenderer3D.BatcherNear, Uo3d.WorldRenderer3D.BatcherFar);
            foreach (var list in new[] { _renderListStatics, _renderListAnimations, _renderListEffects, _renderListTransparentObjects })
            {
                foreach (GameObject obj in list)
                {
                    if (obj is Land || obj is Static || IsDrawnIn3D(obj) || obj.Z > _maxGroundZ)
                        continue;
                    // feet: tile centre plus walking offset (classic-view offset -> map fraction)
                    float ofx = obj.Offset.X, ofy = obj.Offset.Y;
                    float fx = obj.X + 0.5f + (ofx + ofy) / 44f, fy = obj.Y + 0.5f + (ofy - ofx) / 44f;
                    float footY = obj.RealScreenPosition.Y + 22
                        + Uo3d.WorldRotation.OffY(obj.Offset) - obj.Offset.Z;
                    float depth = _world3D.DepthAt(fx, fy, obj.Z) + footY * Uo3d.WorldRenderer3D.DepthPerPixelUp;
                    obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
                }
            }
            batcher.SetStencil(null);
            batcher.ResetProjectionDepthRange();
            batcher.SetSampler(null);
            batcher.End();
        }

        /// <summary>Objects TazUO leaves out entirely this frame (fully faded, hidden roofs); the
        /// turned 3D view must leave them out of its map geometry too.</summary>
        private readonly List<GameObject> _hidden3D = new();

        /// <summary>Which of two objects under the mouse is in front: TazUO's depth, or in the turned
        /// view the turned one (TazUO's depth follows the classic diagonal).</summary>
        private static float PickDepth(GameObject obj) =>
            Uo3d.WorldRotation.Turned
                ? Uo3d.WorldRotation.PickDepth(obj.X, obj.Y, obj.Z, obj is Mobile)
                : obj.CalculateDepthZ();

        /// <summary>The mouse test: for server items the turned 3D view draws itself, against what
        /// it drew (doors are wall planes, not the sprite at the tile centre).</summary>
        private bool CheckMouseSelection3D(GameObject obj)
        {
            if (Uo3d.WorldRotation.Turned && _world3D != null && obj is Item item && IsDrawnIn3D(item))
                return _world3D.HitTest(item.Serial, SelectedObject.TranslatedMousePositionByViewport);
            return obj.CheckMouseSelection();
        }

        private void RecordHidden3D(GameObject obj)
        {
            if (Uo3d.WorldRotation.Turned)
                _hidden3D.Add(obj);
        }

        private bool ProcessAlpha3D(
            GameObject obj,
            ref StaticTiles itemData,
            bool useCoT,
            ref Vector2 playerPos,
            int cotZ,
            out bool allowSelection
        )
        {
            bool keep = ProcessAlpha(obj, ref itemData, useCoT, ref playerPos, cotZ, out allowSelection);
            if (!keep)
                RecordHidden3D(obj);
            return keep;
        }
    }
}
