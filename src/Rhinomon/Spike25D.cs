using System;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;

namespace Rhinomon
{
    /// <summary>
    /// Test conduit for the two rendering assumptions the 2.5D world mode rests
    /// on (PRD §12.3 spike items): the world-space DrawSprite overload (absolute
    /// world size, scales with camera distance) and depth-tested drawing in the
    /// PostDrawObjects channel (sprite occluded by geometry in front of it).
    /// </summary>
    internal sealed class Spike25DConduit : DisplayConduit
    {
        public Point3d Location;
        public double WorldSize;
        public SpriteAtlas Atlas;

        private int _failWorld;
        private int _failRef;

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            try
            {
                var atlas = Atlas;
                if (atlas == null)
                    return;
                DisplayBitmap frame = atlas.GetFrame((int)PetAnim.Idle, 0, false);
                // Depth-tested channel: geometry between camera and sprite should
                // occlude it. If it does not, the world mode needs a different
                // channel or explicit depth state - that is what this spike tells us.
                e.Display.DrawSprite(frame, Location, (float)WorldSize, true);
                _failWorld = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failWorld, "Spike25D.PostDrawObjects", ex);
            }
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            try
            {
                var atlas = Atlas;
                if (atlas == null)
                    return;
                // Screen-space reference: constant size, always on top. Compare
                // crispness and occlusion against the world sprite.
                DisplayBitmap frame = atlas.GetFrame((int)PetAnim.Idle, 0, false);
                e.Display.DrawBitmap(frame, 12, 40);
                e.Display.Draw2dText(
                    "Rhinomon 2.5D spike - world sprite at picked point; this corner one is the screen-space reference",
                    System.Drawing.Color.OrangeRed, new Point2d(12, 28), false, 12);
                _failRef = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failRef, "Spike25D.DrawForeground", ex);
            }
        }
    }

    /// <summary>
    /// Hidden dev command: first run places the test sprite, second run removes
    /// it. Not part of the public v2 surface; deleted once the world mode ships.
    /// </summary>
    [CommandStyle(Style.Hidden)]
    public sealed class RhinomonTest25DCommand : Command
    {
        private static Spike25DConduit _conduit;
        private static SpriteAtlas _atlas;

        public override string EnglishName => "RhinomonTest25D";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (_conduit != null)
            {
                _conduit.Enabled = false;
                _conduit = null;
                _atlas?.Dispose();
                _atlas = null;
                doc.Views.Redraw();
                RhinoApp.WriteLine("Rhinomon spike: test sprite removed.");
                return Result.Success;
            }

            Result res = RhinoGet.GetPoint("Anchor point for the world-space test sprite", false, out Point3d point);
            if (res != Result.Success)
                return res;

            double size = 20.0;
            res = RhinoGet.GetNumber("Sprite world size (model units)", true, ref size, 0.01, 1e6);
            if (res != Result.Success && res != Result.Nothing)
                return res;

            _atlas = new SpriteAtlas(PetKind.Clawd, 4); // 4x pre-upscale: filtering-blur check
            _conduit = new Spike25DConduit
            {
                Location = point,
                WorldSize = size,
                Atlas = _atlas,
                Enabled = true,
            };
            doc.Views.Redraw();

            RhinoApp.WriteLine("Rhinomon spike checklist:");
            RhinoApp.WriteLine(" 1. Zoom in/out - the sprite must grow/shrink with camera distance.");
            RhinoApp.WriteLine(" 2. Put a box between camera and sprite - the sprite must be hidden by it.");
            RhinoApp.WriteLine(" 3. Compare pixel crispness with the reference sprite in the corner.");
            RhinoApp.WriteLine(" 4. Orbit - the sprite must stay camera-facing (billboard).");
            RhinoApp.WriteLine("Run RhinomonTest25D again to remove the test sprite.");
            return Result.Success;
        }
    }
}
