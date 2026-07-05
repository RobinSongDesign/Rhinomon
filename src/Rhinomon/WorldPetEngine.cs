using System;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Rhinomon
{
    /// <summary>
    /// World-space 2.5D pet engine. The pet owns a model-space feet point on the
    /// XY ground plane; the conduit draws its sprite in PostDrawObjects and asks
    /// this engine for a projected screen rectangle only for clicks and emotes.
    /// </summary>
    internal sealed class WorldPetEngine : IPetEngine
    {
        private static readonly long[,] IdleThresholdsMs =
        {
            { 10_000, 30_000, 120_000 },
            { 30_000, 90_000, 300_000 },
            { 120_000, 600_000, 1_800_000 },
        };

        private const int MaxWorldSizeObjects = 4096;
        private const double WalkSpeedBodiesPerSec = 1.6;
        private const double ClimbSpeedBodiesPerSec = 1.1;
        private const double FallSpeedBodiesPerSec = 14.0;
        private const int ReactionEmoteMs = 2500;
        private const int PettedRepeatGuardMs = 800;
        private const long MoodHappyWindowMs = 60_000;
        private const long MoodBoredAfterMs = 600_000;

        public ActivityMonitor Monitor;
        public PerchScanner Scanner;

        private readonly Random _rng = new Random();

        private PetState _state = PetState.Idle;
        private int _frame;
        private double _frameAccumMs;
        private bool _facingLeft;

        // Feet position in model space. In the ground-only slice, Z stays 0.
        private Point3d _pos;
        private Point3d _wanderTarget;
        private double _worldSize;
        private bool _needsSpawn = true;
        private long _walkPauseUntilMs;
        private long _idleEpisodeStamp;

        private bool _hasPerch;
        private bool _elevated;
        private bool _perchTriedThisEpisode;
        private BoundingBox _perchBox;
        private Point3d _perchApproach;
        private Point3d _perchEntry;
        private Point3d _perchTarget;
        private long _perchPauseUntilMs;
        private bool _perchWalking;

        private Vector3d _cameraRight = Vector3d.XAxis;

        private EmoteKind _emote = EmoteKind.None;
        private long _emoteUntilMs;

        private PetMood _mood = PetMood.Calm;
        private long _lastInteractionMs;
        private long _lastPettedMs;

        public PetMood Mood => _mood;

        public int DesiredFps => _state == PetState.Sleep ? 1 : 5;

        public void ResetToHome()
        {
            _needsSpawn = true;
            AbandonPerchTracking();
            SetState(PetState.Idle);
            _emote = EmoteKind.None;
            SpawnFromActiveView();
        }

        public void OnStrongInterrupt()
        {
            if (_state == PetState.Climb)
            {
                StartFall();
                return;
            }

            if (_state == PetState.WalkToPerch)
            {
                AbandonPerchTracking();
                SetState(PetState.Idle);
            }
            else if (_state == PetState.Sleep || _state == PetState.Perched)
            {
                _perchWalking = false;
                _perchPauseUntilMs = System.Environment.TickCount64 + 1200;
                SetState(_elevated ? PetState.Perched : PetState.Idle);
            }

            if (_emote == EmoteKind.Zzz)
                ClearEmote();
        }

        public void OnViewportChanged()
        {
            // World mode is view independent: switching views must not move the pet.
            if (_needsSpawn)
                SpawnFromActiveView();
        }

        public void OnPetted()
        {
            React(PetReaction.Petted);
        }

        public void React(PetReaction reaction)
        {
            long now = System.Environment.TickCount64;
            _lastInteractionMs = now;
            _mood = PetMood.Happy;

            switch (reaction)
            {
                case PetReaction.Petted:
                    if (now - _lastPettedMs < PettedRepeatGuardMs)
                        return;
                    _lastPettedMs = now;
                    StartOneShot(PetState.Petted);
                    SetEmote(EmoteKind.Heart, now + ReactionEmoteMs);
                    break;
                case PetReaction.NewObjects:
                    StartOneShot(PetState.Happy);
                    SetEmote(EmoteKind.Sparkle, now + ReactionEmoteMs);
                    break;
                case PetReaction.MassDelete:
                    StartOneShot(PetState.Surprised);
                    SetEmote(EmoteKind.Exclaim, now + ReactionEmoteMs);
                    break;
                case PetReaction.Undo:
                    SetEmote(EmoteKind.Question, now + ReactionEmoteMs);
                    break;
            }
        }

        public bool Tick(double dtMs)
        {
            var doc = RhinoDoc.ActiveDoc;
            var view = doc?.Views.ActiveView;
            var vp = view?.ActiveViewport;
            if (vp == null)
                return false;

            PetSystem.SetActiveViewportId(vp.Id);
            UpdateCameraRight(vp);

            bool changed = false;
            if (_needsSpawn)
            {
                Spawn(doc, vp);
                changed = true;
            }

            long now = System.Environment.TickCount64;
            UpdateMood(now);
            if (UpdateEmoteExpiry(now))
                changed = true;

            long stamp = Monitor != null ? Monitor.LastActivityStamp : 0;
            if (stamp != _idleEpisodeStamp)
            {
                _idleEpisodeStamp = stamp;
                _perchTriedThisEpisode = false;
            }

            if (_hasPerch && Monitor != null && Monitor.ConsumeAnchorDeleted())
            {
                StartFall();
                changed = true;
            }

            long idleMs = Monitor != null ? Monitor.IdleMilliseconds : 0;
            long walkAfter = Threshold(0);
            long climbAfter = Threshold(1);
            long sleepAfter = Threshold(2);

            switch (_state)
            {
                case PetState.Idle:
                    if (!_elevated)
                        _pos.Z = 0;
                    if (idleMs >= sleepAfter)
                    {
                        SetState(PetState.Sleep);
                    }
                    else if (!_elevated && idleMs >= climbAfter && !_perchTriedThisEpisode)
                    {
                        _perchTriedThisEpisode = true;
                        TryStartPerch(doc);
                    }
                    else if (!_elevated && idleMs >= walkAfter && now >= _walkPauseUntilMs)
                    {
                        _wanderTarget = PickWanderTarget(vp);
                        SetState(PetState.Walk);
                    }
                    break;

                case PetState.Walk:
                    _pos.Z = 0;
                    if (idleMs < walkAfter || idleMs >= climbAfter)
                    {
                        SetState(PetState.Idle);
                        break;
                    }
                    if (MoveToward(_wanderTarget, WalkSpeedBodiesPerSec, dtMs, ref changed))
                    {
                        _walkPauseUntilMs = now + 1500 + _rng.Next(3500);
                        SetState(PetState.Idle);
                    }
                    break;

                case PetState.WalkToPerch:
                    _pos.Z = 0;
                    if (!_hasPerch)
                    {
                        SetState(PetState.Idle);
                        break;
                    }
                    if (MoveToward(_perchApproach, WalkSpeedBodiesPerSec, dtMs, ref changed))
                    {
                        _pos = _perchApproach;
                        SetState(PetState.Climb);
                    }
                    break;

                case PetState.Climb:
                    if (!_hasPerch)
                    {
                        StartFall();
                        changed = true;
                        break;
                    }
                    double climbStep = ClimbSpeedBodiesPerSec * _worldSize * dtMs / 1000.0;
                    double topZ = _perchBox.Max.Z;
                    if (_pos.Z + climbStep >= topZ)
                    {
                        _pos = _perchEntry;
                        _elevated = true;
                        _perchWalking = false;
                        _perchPauseUntilMs = now + 1200 + _rng.Next(2600);
                        SetState(PetState.Perched);
                    }
                    else
                    {
                        _pos = new Point3d(_perchApproach.X, _perchApproach.Y, _pos.Z + climbStep);
                    }
                    changed = true;
                    break;

                case PetState.Perched:
                    if (!_hasPerch)
                    {
                        StartFall();
                        changed = true;
                        break;
                    }
                    _pos.Z = _perchBox.Max.Z;
                    if (idleMs >= sleepAfter)
                    {
                        _perchWalking = false;
                        SetState(PetState.Sleep);
                    }
                    else
                    {
                        UpdatePerchedWalk(now, dtMs, ref changed);
                    }
                    break;

                case PetState.Sleep:
                    if (idleMs < 1000)
                    {
                        ClearEmote();
                        SetState(_elevated ? PetState.Perched : PetState.Idle);
                    }
                    break;

                case PetState.Fall:
                    double fallStep = FallSpeedBodiesPerSec * _worldSize * dtMs / 1000.0;
                    if (_pos.Z - fallStep <= 0)
                    {
                        _pos.Z = 0;
                        _elevated = false;
                        SetState(PetState.Idle);
                    }
                    else
                    {
                        _pos.Z -= fallStep;
                    }
                    changed = true;
                    break;

                case PetState.Petted:
                case PetState.Surprised:
                case PetState.Happy:
                    break;
            }

            if (AdvanceFrame(dtMs))
                changed = true;

            return changed;
        }

        public bool TryGetScreenDrawInfo(
            RhinoViewport vp,
            out DisplayBitmap sprite,
            out Rectangle petRect,
            out DisplayBitmap emote,
            out Rectangle emoteRect)
        {
            sprite = null;
            petRect = Rectangle.Empty;
            emote = null;
            emoteRect = Rectangle.Empty;

            var atlas = PetSystem.Atlas;
            if (atlas == null || vp == null)
                return false;

            if (_needsSpawn)
                Spawn(RhinoDoc.ActiveDoc, vp);

            if (!TryProjectPet(vp, out petRect))
                return false;

            if (_emote != EmoteKind.None)
            {
                emote = atlas.GetEmote((int)_emote);
                if (emote != null)
                {
                    int es = atlas.EmotePixels;
                    emoteRect = new Rectangle(
                        petRect.X + (petRect.Width - es) / 2,
                        petRect.Y - es - 2,
                        es,
                        es);
                }
            }
            return true;
        }

        public bool TryGetWorldDrawInfo(out DisplayBitmap sprite, out Point3d position, out float worldSize)
        {
            sprite = null;
            position = Point3d.Origin;
            worldSize = 0;

            var atlas = PetSystem.Atlas;
            if (atlas == null || _needsSpawn || _worldSize <= 0)
                return false;

            sprite = atlas.GetFrame(CurrentAnimRow(), _frame, _facingLeft);
            position = _pos + Vector3d.ZAxis * (_worldSize * 0.5);
            worldSize = (float)_worldSize;
            return true;
        }

        // ---- internals -----------------------------------------------------

        private void SpawnFromActiveView()
        {
            var doc = RhinoDoc.ActiveDoc;
            var vp = doc?.Views.ActiveView?.ActiveViewport;
            if (vp != null)
                Spawn(doc, vp);
        }

        private void Spawn(RhinoDoc doc, RhinoViewport vp)
        {
            if (vp == null)
                return;

            _worldSize = ResolveWorldSize(doc, vp);
            var target = vp.CameraTarget;
            _pos = new Point3d(target.X, target.Y, 0);
            _wanderTarget = _pos;
            UpdateCameraRight(vp);
            _needsSpawn = false;
        }

        private double ResolveWorldSize(RhinoDoc doc, RhinoViewport vp)
        {
            var settings = PetSystem.CurrentSettings;
            if (settings != null && settings.WorldSize > 0)
                return Math.Clamp(settings.WorldSize, 0.5, 1000.0);

            BoundingBox bbox = BoundingBox.Empty;
            int examined = 0;
            if (doc != null)
            {
                foreach (RhinoObject obj in doc.Objects)
                {
                    if (++examined > MaxWorldSizeObjects)
                        break;
                    if (obj == null || !obj.Visible || obj.Geometry == null)
                        continue;
                    BoundingBox candidate = obj.Geometry.GetBoundingBox(false);
                    if (candidate.IsValid)
                        bbox.Union(candidate);
                }
            }

            double autoSize = 0;
            if (bbox.IsValid)
                autoSize = bbox.Diagonal.Length * 0.02;
            if (autoSize <= 0 && vp != null)
                autoSize = vp.CameraLocation.DistanceTo(vp.CameraTarget) * 0.05;
            if (autoSize <= 0)
                autoSize = 1.0;
            return Math.Clamp(autoSize, 0.5, 1000.0);
        }

        private void UpdateCameraRight(RhinoViewport vp)
        {
            if (vp == null)
                return;
            Vector3d right = vp.CameraX;
            if (!right.IsValid || right.IsTiny())
                return;
            right.Unitize();
            _cameraRight = right;
        }

        private Point3d PickWanderTarget(RhinoViewport vp)
        {
            var center = vp != null ? vp.CameraTarget : _pos;
            center.Z = 0;
            double radius = Math.Max(_worldSize, _worldSize * 20.0);
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            double distance = Math.Sqrt(_rng.NextDouble()) * radius;
            return new Point3d(
                center.X + Math.Cos(angle) * distance,
                center.Y + Math.Sin(angle) * distance,
                0);
        }

        private void TryStartPerch(RhinoDoc doc)
        {
            if (doc == null || Scanner == null || _worldSize <= 0)
                return;
            if (!Scanner.TryFindWorldPerch(doc, _pos, _worldSize, out Guid objectId, out BoundingBox bbox))
                return;

            _perchBox = bbox;
            ConfigurePerchPath(bbox);
            _hasPerch = true;
            _elevated = false;
            _perchWalking = false;
            Monitor?.WatchObject(objectId);
            SetState(PetState.WalkToPerch);
        }

        private void ConfigurePerchPath(BoundingBox bbox)
        {
            FindNearestPerchSide(bbox, out Point3d sidePoint, out Vector3d normal);

            double half = _worldSize * 0.5;
            _perchApproach = sidePoint + normal * half;
            _perchApproach.Z = 0;

            Point3d entry = sidePoint - normal * half;
            _perchEntry = ClampToTop(entry, bbox);
            _perchTarget = _perchEntry;
        }

        private void FindNearestPerchSide(BoundingBox bbox, out Point3d sidePoint, out Vector3d normal)
        {
            Point3d bestPoint = new Point3d(bbox.Min.X, Math.Clamp(_pos.Y, bbox.Min.Y, bbox.Max.Y), 0);
            Vector3d bestNormal = new Vector3d(-1, 0, 0);
            double best = HorizontalDistance(_pos, bestPoint);

            Consider(new Point3d(bbox.Max.X, Math.Clamp(_pos.Y, bbox.Min.Y, bbox.Max.Y), 0), new Vector3d(1, 0, 0));
            Consider(new Point3d(Math.Clamp(_pos.X, bbox.Min.X, bbox.Max.X), bbox.Min.Y, 0), new Vector3d(0, -1, 0));
            Consider(new Point3d(Math.Clamp(_pos.X, bbox.Min.X, bbox.Max.X), bbox.Max.Y, 0), new Vector3d(0, 1, 0));

            void Consider(Point3d candidate, Vector3d candidateNormal)
            {
                double dist = HorizontalDistance(_pos, candidate);
                if (dist >= best)
                    return;
                best = dist;
                bestPoint = candidate;
                bestNormal = candidateNormal;
            }

            sidePoint = bestPoint;
            normal = bestNormal;
        }

        private void UpdatePerchedWalk(long now, double dtMs, ref bool changed)
        {
            if (!_perchWalking)
            {
                if (now >= _perchPauseUntilMs)
                {
                    _perchTarget = PickPerchTarget();
                    _perchWalking = true;
                }
                return;
            }

            if (MoveToward(_perchTarget, WalkSpeedBodiesPerSec * 0.75, dtMs, ref changed))
            {
                _perchWalking = false;
                _perchPauseUntilMs = now + 1500 + _rng.Next(4500);
            }
        }

        private Point3d PickPerchTarget()
        {
            GetInsetRange(_perchBox.Min.X, _perchBox.Max.X, out double minX, out double maxX);
            GetInsetRange(_perchBox.Min.Y, _perchBox.Max.Y, out double minY, out double maxY);
            return new Point3d(
                minX + _rng.NextDouble() * (maxX - minX),
                minY + _rng.NextDouble() * (maxY - minY),
                _perchBox.Max.Z);
        }

        private Point3d ClampToTop(Point3d point, BoundingBox bbox)
        {
            GetInsetRange(bbox.Min.X, bbox.Max.X, out double minX, out double maxX);
            GetInsetRange(bbox.Min.Y, bbox.Max.Y, out double minY, out double maxY);
            return new Point3d(
                Math.Clamp(point.X, minX, maxX),
                Math.Clamp(point.Y, minY, maxY),
                bbox.Max.Z);
        }

        private void GetInsetRange(double min, double max, out double insetMin, out double insetMax)
        {
            double inset = Math.Min(_worldSize * 0.5, Math.Max(0, (max - min) * 0.5));
            insetMin = min + inset;
            insetMax = max - inset;
            if (insetMin > insetMax)
                insetMin = insetMax = 0.5 * (min + max);
        }

        private static double HorizontalDistance(Point3d a, Point3d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void StartFall()
        {
            AbandonPerchTracking();
            if (_state == PetState.Fall)
                return;
            SetState(PetState.Fall);
            SetEmote(EmoteKind.Exclaim, System.Environment.TickCount64 + 1200);
        }

        private void AbandonPerchTracking()
        {
            _hasPerch = false;
            _elevated = false;
            _perchWalking = false;
            Monitor?.ClearWatch();
        }

        private bool MoveToward(Point3d target, double bodiesPerSec, double dtMs, ref bool changed)
        {
            target.Z = _pos.Z;
            Vector3d delta = target - _pos;
            double distance = delta.Length;
            double arrive = Math.Max(0.01, _worldSize * 0.05);
            if (distance <= arrive)
                return true;

            _facingLeft = delta * _cameraRight < 0;
            double step = bodiesPerSec * _worldSize * dtMs / 1000.0;
            if (step >= distance)
            {
                _pos = target;
            }
            else
            {
                delta.Unitize();
                _pos += delta * step;
            }
            changed = true;
            return _pos.DistanceTo(target) <= arrive;
        }

        private bool TryProjectPet(RhinoViewport vp, out Rectangle petRect)
        {
            petRect = Rectangle.Empty;
            if (vp == null || _worldSize <= 0)
                return false;

            Point2d feet = vp.WorldToClient(_pos);
            Point2d head = vp.WorldToClient(_pos + Vector3d.ZAxis * _worldSize);
            if (!IsReasonable(feet) || !IsReasonable(head))
                return false;

            double pixelHeight = feet.DistanceTo(head);
            if (pixelHeight < 1.0)
                pixelHeight = 1.0;
            if (pixelHeight > 100000.0)
                return false;

            int size = Math.Max(1, (int)Math.Round(pixelHeight));
            int left = (int)Math.Round(feet.X - size * 0.5);
            int top = (int)Math.Round(feet.Y) - size;
            petRect = new Rectangle(left, top, size, size);
            return true;
        }

        private static bool IsReasonable(Point2d p)
        {
            return !double.IsNaN(p.X) && !double.IsNaN(p.Y) &&
                   !double.IsInfinity(p.X) && !double.IsInfinity(p.Y) &&
                   Math.Abs(p.X) < 1.0e7 && Math.Abs(p.Y) < 1.0e7;
        }

        private long Threshold(int step)
        {
            var settings = PetSystem.CurrentSettings;
            int level = settings != null ? (int)settings.Activity : 0;
            return IdleThresholdsMs[Math.Clamp(level, 0, 2), step];
        }

        private void StartOneShot(PetState oneShot)
        {
            if (_state == PetState.Fall || _state == PetState.Climb || _state == PetState.WalkToPerch)
                return;
            SetState(oneShot);
        }

        private void EndOneShot()
        {
            SetState(_elevated ? PetState.Perched : PetState.Idle);
        }

        private void SetState(PetState state)
        {
            if (_state == state)
                return;
            if (_state == PetState.Sleep && _emote == EmoteKind.Zzz)
                ClearEmote();
            _state = state;
            _frame = 0;
            _frameAccumMs = 0;
            if (state == PetState.Sleep)
                SetEmote(EmoteKind.Zzz, long.MaxValue);
        }

        private void SetEmote(EmoteKind emote, long untilMs)
        {
            _emote = emote;
            _emoteUntilMs = untilMs;
        }

        private void ClearEmote()
        {
            _emote = EmoteKind.None;
            _emoteUntilMs = 0;
        }

        private bool UpdateEmoteExpiry(long now)
        {
            if (_emote == EmoteKind.None || _emoteUntilMs == long.MaxValue || now < _emoteUntilMs)
                return false;
            ClearEmote();
            return true;
        }

        private void UpdateMood(long now)
        {
            long sinceInteraction = now - _lastInteractionMs;
            if (sinceInteraction > MoodBoredAfterMs)
                _mood = PetMood.Bored;
            else if (sinceInteraction > MoodHappyWindowMs && _mood == PetMood.Happy)
                _mood = PetMood.Calm;
        }

        private int CurrentAnimRow()
        {
            switch (_state)
            {
                case PetState.Walk:
                case PetState.WalkToPerch:
                    return (int)PetAnim.Walk;
                case PetState.Perched:
                    return _perchWalking ? (int)PetAnim.Walk : (int)PetAnim.Idle;
                case PetState.Climb:
                    return (int)PetAnim.Climb;
                case PetState.Sleep:
                    return (int)PetAnim.Sleep;
                case PetState.Petted:
                    return (int)PetAnim.Petted;
                case PetState.Surprised:
                case PetState.Fall:
                    return (int)PetAnim.Surprised;
                case PetState.Happy:
                    return (int)PetAnim.Happy;
                default:
                    return (int)PetAnim.Idle;
            }
        }

        private static bool IsOneShot(PetState state)
        {
            return state == PetState.Petted || state == PetState.Surprised || state == PetState.Happy;
        }

        private bool AdvanceFrame(double dtMs)
        {
            int row = CurrentAnimRow();
            int frames = SpriteAtlas.FrameCounts[row];
            double fps = SpriteAtlas.FrameRates[row];
            if (_state == PetState.Idle && _mood == PetMood.Bored)
                fps *= 0.5;
            if (frames <= 1 && !IsOneShot(_state))
                return false;

            bool changed = false;
            double frameMs = 1000.0 / fps;
            _frameAccumMs += dtMs;
            while (_frameAccumMs >= frameMs)
            {
                _frameAccumMs -= frameMs;
                _frame++;
                changed = true;
                if (_frame >= frames)
                {
                    if (IsOneShot(_state))
                    {
                        EndOneShot();
                        break;
                    }
                    _frame = 0;
                }
            }
            return changed;
        }
    }
}
