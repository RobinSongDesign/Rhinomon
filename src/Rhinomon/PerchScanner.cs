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
    /// forces a recompute) into a snapshot that is rebuilt only when the
    /// document changes, projects the corners to the screen and picks a
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

        public ActivityMonitor Monitor;

        // Snapshot of the visible objects' cached bounding boxes. Enumerating
        // the object table allocates a wrapper per object, so it happens once
        // per document change (ActivityMonitor.DocEpoch), not once per scan;
        // the scans themselves then walk plain struct arrays, allocation-free.
        private BoundingBox[] _boxes = new BoundingBox[64];
        private Guid[] _ids = new Guid[64];
        private int _count;
        private bool _snapshotValid;
        private bool _snapshotTooMany;
        private uint _snapshotDocSerial;
        private int _snapshotEpoch;

        /// <summary>
        /// Brings the snapshot up to date. Returns false when the document is
        /// too large for climbing behavior (PRD F6).
        /// </summary>
        private bool EnsureSnapshot(RhinoDoc doc)
        {
            var monitor = Monitor;
            uint serial = doc.RuntimeSerialNumber;
            int epoch = monitor != null ? monitor.DocEpoch : 0;
            // Without a monitor there is no change signal: always rebuild.
            if (_snapshotValid && monitor != null && serial == _snapshotDocSerial && epoch == _snapshotEpoch)
                return !_snapshotTooMany;

            _snapshotValid = true;
            _snapshotDocSerial = serial;
            _snapshotEpoch = epoch;
            _count = 0;
            _snapshotTooMany = doc.Objects.Count > MaxDocumentObjects;
            if (_snapshotTooMany)
                return false;

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

                if (_count == _boxes.Length)
                {
                    int capacity = Math.Min(_boxes.Length * 2, MaxExaminedObjects);
                    Array.Resize(ref _boxes, capacity);
                    Array.Resize(ref _ids, capacity);
                }
                _boxes[_count] = bbox;
                _ids[_count] = obj.Id;
                _count++;
            }
            return true;
        }

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
            if (!EnsureSnapshot(doc))
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

            for (int n = 0; n < _count; n++)
            {
                BoundingBox bbox = _boxes[n];

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
                    bestId = _ids[n]; bestAnchor = candidate; bestScreen = screen; bestDist = dist;
                }
                else if (dist < secondDist)
                {
                    secondId = _ids[n]; secondAnchor = candidate; secondScreen = screen; secondDist = dist;
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

        public bool TryFindWorldPerch(
            RhinoDoc doc,
            Point3d petPos,
            double worldSize,
            out Guid objectId,
            out BoundingBox bbox)
        {
            objectId = Guid.Empty;
            bbox = BoundingBox.Empty;

            if (doc == null || worldSize <= 0)
                return false;
            if (!EnsureSnapshot(doc))
                return false;

            Guid bestId = Guid.Empty, secondId = Guid.Empty;
            BoundingBox bestBox = BoundingBox.Empty, secondBox = BoundingBox.Empty;
            double bestDist = double.MaxValue, secondDist = double.MaxValue;
            double maxDistance = worldSize * 40.0;
            double reachableBaseZ = worldSize * 0.25;

            for (int n = 0; n < _count; n++)
            {
                BoundingBox candidate = _boxes[n];

                double height = candidate.Max.Z - candidate.Min.Z;
                if (candidate.Min.Z > reachableBaseZ || height < worldSize)
                    continue;

                double dist = HorizontalDistanceToFootprint(petPos, candidate);
                if (dist > maxDistance)
                    continue;

                if (dist < bestDist)
                {
                    secondId = bestId; secondBox = bestBox; secondDist = bestDist;
                    bestId = _ids[n]; bestBox = candidate; bestDist = dist;
                }
                else if (dist < secondDist)
                {
                    secondId = _ids[n]; secondBox = candidate; secondDist = dist;
                }
            }

            if (bestId == Guid.Empty)
                return false;

            bool takeSecond = secondId != Guid.Empty && _rng.Next(3) == 0;
            objectId = takeSecond ? secondId : bestId;
            bbox = takeSecond ? secondBox : bestBox;
            return true;
        }

        public bool TryFindWorldOverlapEscape(
            RhinoDoc doc,
            Point3d petPos,
            double worldSize,
            out Point3d escapeTarget,
            out BoundingBox bbox)
        {
            escapeTarget = Point3d.Origin;
            bbox = BoundingBox.Empty;

            if (doc == null || worldSize <= 0)
                return false;
            if (!EnsureSnapshot(doc))
                return false;

            double bestExitDistance = double.MaxValue;
            Point3d bestTarget = Point3d.Origin;
            BoundingBox bestBox = BoundingBox.Empty;

            for (int n = 0; n < _count; n++)
            {
                BoundingBox candidate = _boxes[n];
                if (!TryComputeOverlapEscape(petPos, worldSize, candidate, out Point3d target, out double exitDistance))
                    continue;

                if (exitDistance < bestExitDistance)
                {
                    bestExitDistance = exitDistance;
                    bestTarget = target;
                    bestBox = candidate;
                }
            }

            if (!bestBox.IsValid)
                return false;

            escapeTarget = bestTarget;
            bbox = bestBox;
            return true;
        }

        private static double HorizontalDistanceToFootprint(Point3d point, BoundingBox bbox)
        {
            double x = Math.Clamp(point.X, bbox.Min.X, bbox.Max.X);
            double y = Math.Clamp(point.Y, bbox.Min.Y, bbox.Max.Y);
            double dx = point.X - x;
            double dy = point.Y - y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool TryComputeOverlapEscape(
            Point3d petPos,
            double worldSize,
            BoundingBox bbox,
            out Point3d target,
            out double exitDistance)
        {
            target = Point3d.Origin;
            exitDistance = 0;

            double epsilon = Math.Max(worldSize * 0.02, 1e-9);
            double petBottom = petPos.Z + epsilon;
            double petTop = petPos.Z + worldSize - epsilon;
            if (bbox.Min.Z >= petTop || bbox.Max.Z <= petBottom)
                return false;
            if (bbox.Max.Z <= petPos.Z + worldSize * 0.25)
                return false;

            if (petPos.X <= bbox.Min.X + epsilon || petPos.X >= bbox.Max.X - epsilon ||
                petPos.Y <= bbox.Min.Y + epsilon || petPos.Y >= bbox.Max.Y - epsilon)
            {
                return false;
            }

            double toMinX = petPos.X - bbox.Min.X;
            double toMaxX = bbox.Max.X - petPos.X;
            double toMinY = petPos.Y - bbox.Min.Y;
            double toMaxY = bbox.Max.Y - petPos.Y;
            double offset = worldSize * 0.75 + epsilon;
            double z = 0;

            exitDistance = toMinX;
            target = new Point3d(bbox.Min.X - offset, petPos.Y, z);

            if (toMaxX < exitDistance)
            {
                exitDistance = toMaxX;
                target = new Point3d(bbox.Max.X + offset, petPos.Y, z);
            }
            if (toMinY < exitDistance)
            {
                exitDistance = toMinY;
                target = new Point3d(petPos.X, bbox.Min.Y - offset, z);
            }
            if (toMaxY < exitDistance)
            {
                exitDistance = toMaxY;
                target = new Point3d(petPos.X, bbox.Max.Y + offset, z);
            }

            return true;
        }
    }
}
