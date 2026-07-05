using System;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Rhinomon
{
    /// <summary>
    /// Finds something for the pet to climb on (PRD F6): walks the visible
    /// document objects, reads their cached bounding boxes (accurate=false, never
    /// forces a recompute), projects the corners to the screen and picks a
    /// suitable candidate. The anchor is the world-space center of the bbox top
    /// face; the engine re-projects that single point every frame to follow the
    /// view. Runs at most once per idle episode and never on huge documents.
    /// </summary>
    internal sealed class PerchScanner
    {
        private const int MaxDocumentObjects = 5000; // above this, skip climbing entirely
        private const int MaxExaminedObjects = 4096;
        private const int TopMarginPx = 24; // room for the pet + emote above the anchor

        private readonly Random _rng = new Random();

        public bool TryFindPerch(
            RhinoDoc doc,
            RhinoViewport viewport,
            int petSizePx,
            double petFeetX,
            double groundY,
            out Guid objectId,
            out Point3d anchor,
            out Point2d anchorScreen)
        {
            objectId = Guid.Empty;
            anchor = Point3d.Origin;
            anchorScreen = new Point2d(0, 0);

            if (doc == null || viewport == null)
                return false;
            if (doc.Objects.Count > MaxDocumentObjects)
                return false;

            Rectangle bounds = viewport.Bounds;
            int w = bounds.Width;
            int h = bounds.Height;
            if (w < 16 || h < 16)
                return false;

            // Keep two finalists and pick randomly between them so the pet does
            // not always run to the same object ("random/nearest" per PRD F6).
            Guid bestId = Guid.Empty, secondId = Guid.Empty;
            Point3d bestAnchor = Point3d.Origin, secondAnchor = Point3d.Origin;
            Point2d bestScreen = new Point2d(0, 0), secondScreen = new Point2d(0, 0);
            double bestDist = double.MaxValue, secondDist = double.MaxValue;

            int examined = 0;
            foreach (RhinoObject obj in doc.Objects)
            {
                if (++examined > MaxExaminedObjects)
                    break;
                if (obj == null || !obj.Visible)
                    continue;

                GeometryBase geo = obj.Geometry;
                if (geo == null)
                    continue;

                BoundingBox bbox = geo.GetBoundingBox(false); // cached, cheap
                if (!bbox.IsValid)
                    continue;

                // Project all 8 corners; track the screen-space extents.
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                bool degenerate = false;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Point3d(
                        (i & 1) == 0 ? bbox.Min.X : bbox.Max.X,
                        (i & 2) == 0 ? bbox.Min.Y : bbox.Max.Y,
                        (i & 4) == 0 ? bbox.Min.Z : bbox.Max.Z);
                    Point2d s = viewport.WorldToClient(corner);
                    // Corners near/behind the camera project to absurd values.
                    if (double.IsNaN(s.X) || double.IsNaN(s.Y) ||
                        Math.Abs(s.X) > w * 8.0 || Math.Abs(s.Y) > h * 8.0)
                    {
                        degenerate = true;
                        break;
                    }
                    if (s.X < minX) minX = s.X;
                    if (s.X > maxX) maxX = s.X;
                    if (s.Y < minY) minY = s.Y;
                    if (s.Y > maxY) maxY = s.Y;
                }
                if (degenerate)
                    continue;

                // Candidate filter: on screen, projected height at least the
                // pet's height, and not a sliver.
                if (maxY - minY < petSizePx || maxX - minX < petSizePx * 0.5)
                    continue;
                if (maxX < 0 || minX > w || maxY < 0 || minY > h)
                    continue;

                // Anchor: world center of the bbox top face.
                var candidate = new Point3d(
                    0.5 * (bbox.Min.X + bbox.Max.X),
                    0.5 * (bbox.Min.Y + bbox.Max.Y),
                    bbox.Max.Z);
                Point2d screen = viewport.WorldToClient(candidate);

                // The pet must fit on screen while standing on the anchor and the
                // anchor must be meaningfully above the ground walk line.
                if (screen.X < petSizePx * 0.5 || screen.X > w - petSizePx * 0.5)
                    continue;
                if (screen.Y < TopMarginPx + petSizePx || screen.Y > groundY - petSizePx)
                    continue;

                double dist = Math.Abs(screen.X - petFeetX);
                if (dist < bestDist)
                {
                    secondId = bestId; secondAnchor = bestAnchor; secondScreen = bestScreen; secondDist = bestDist;
                    bestId = obj.Id; bestAnchor = candidate; bestScreen = screen; bestDist = dist;
                }
                else if (dist < secondDist)
                {
                    secondId = obj.Id; secondAnchor = candidate; secondScreen = screen; secondDist = dist;
                }
            }

            if (bestId == Guid.Empty)
                return false;

            bool takeSecond = secondId != Guid.Empty && _rng.Next(3) == 0;
            objectId = takeSecond ? secondId : bestId;
            anchor = takeSecond ? secondAnchor : bestAnchor;
            anchorScreen = takeSecond ? secondScreen : bestScreen;
            return true;
        }
    }
}
