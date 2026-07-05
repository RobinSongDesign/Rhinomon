using System;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace Rhinomon
{
    internal enum PetState
    {
        Idle,
        Walk,        // wandering along the viewport bottom edge
        WalkToPerch, // walking toward the screen x of a chosen perch
        Climb,       // climbing up to the perch anchor
        Perched,     // idling on top of an object, glued to a world anchor
        Sleep,
        Petted,      // one-shot
        Surprised,   // one-shot
        Happy,       // one-shot
        Fall,        // dropping back to the ground, then walking home
    }

    internal enum PetReaction
    {
        Petted,
        NewObjects,
        MassDelete,
        Undo,
    }

    /// <summary>Session-only mood (PRD F7). Never persisted.</summary>
    internal enum PetMood
    {
        Calm,
        Happy,
        Bored,
    }

    /// <summary>
    /// The pet state machine: idle ladder timing, wandering, perching, sleeping,
    /// one-shot reactions and animation frame scheduling. Runs on the UI thread,
    /// driven by PerfGovernor ticks; PetConduit only reads the current frame.
    /// </summary>
    internal sealed class PetEngine : IPetEngine
    {
        // Idle ladder thresholds in ms, indexed by ActivityLevel:
        // { start wandering, try to climb, fall asleep } (PRD F5).
        private static readonly long[,] IdleThresholdsMs =
        {
            { 10_000, 30_000, 120_000 },     // Lively
            { 30_000, 90_000, 300_000 },     // Normal
            { 120_000, 600_000, 1_800_000 }, // Chill
        };

        private const double WalkSpeedBodiesPerSec = 1.6;  // horizontal, in body heights
        private const double ClimbSpeedBodiesPerSec = 1.1;
        private const double FallSpeedBodiesPerSec = 14.0;
        private const int ReactionEmoteMs = 2500;
        private const int PettedRepeatGuardMs = 800;
        private const int OffscreenMarginPx = 4;
        private const long MoodHappyWindowMs = 60_000;
        private const long MoodBoredAfterMs = 600_000;

        public ActivityMonitor Monitor;
        public PerchScanner Scanner;

        private readonly Random _rng = new Random();

        private PetState _state = PetState.Idle;
        private int _frame;
        private double _frameAccumMs;
        private bool _facingLeft;

        // Feet position (bottom-center of the sprite) in viewport client pixels.
        private double _x;
        private double _y;
        private bool _needsHomePlacement = true;

        private double _wanderTargetX;
        private long _walkPauseUntilMs;

        // Perch anchor: a world point on top of a document object.
        private bool _hasPerch;
        private bool _elevated;
        private Point3d _anchor;
        private double _perchScreenX;
        private double _perchScreenY;
        private bool _perchTriedThisEpisode;
        private long _idleEpisodeStamp;

        private EmoteKind _emote = EmoteKind.None;
        private long _emoteUntilMs; // long.MaxValue = sticky while the state lasts

        private PetMood _mood = PetMood.Calm;
        private long _lastInteractionMs;
        private long _lastPettedMs;

        public PetMood Mood => _mood;

        /// <summary>Target animation rate; the governor may cap it further.</summary>
        public int DesiredFps => _state == PetState.Sleep ? 1 : 5;

        public void ResetToHome()
        {
            _needsHomePlacement = true;
            AbandonPerchTracking();
            SetState(PetState.Idle);
            _emote = EmoteKind.None;
        }

        /// <summary>
        /// A command started or the document changed: wake up immediately; if the
        /// pet is above the ground it drops and walks home (PRD F5). Pure field
        /// mutation - never triggers a redraw (PRD P6).
        /// </summary>
        public void OnStrongInterrupt()
        {
            if (_elevated || _state == PetState.Climb)
            {
                StartFall();
                return;
            }
            AbandonPerchTracking();
            if (_state != PetState.Idle)
                SetState(PetState.Idle);
            if (_emote == EmoteKind.Zzz)
                ClearEmote();
        }

        /// <summary>The active viewport changed: the pet moves house.</summary>
        public void OnViewportChanged()
        {
            ResetToHome();
        }

        public void OnPetted()
        {
            React(PetReaction.Petted);
        }

        public void React(PetReaction reaction)
        {
            long now = Environment.TickCount64;
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
                    // No dedicated "confused" animation row; the question emote
                    // alone carries the reaction.
                    SetEmote(EmoteKind.Question, now + ReactionEmoteMs);
                    break;
            }
        }

        /// <summary>
        /// Advances the simulation by dtMs. Returns true when the visible result
        /// changed and a redraw is worthwhile. Called only by the governor timer,
        /// which is paused during commands / view drags / left-button drags.
        /// </summary>
        public bool Tick(double dtMs)
        {
            var doc = RhinoDoc.ActiveDoc;
            var view = doc?.Views.ActiveView;
            var vp = view?.ActiveViewport;
            if (vp == null)
                return false;

            // Keep the conduit's notion of "active viewport" self-healing.
            PetSystem.SetActiveViewportId(vp.Id);

            Rectangle bounds = vp.Bounds;
            int w = bounds.Width;
            int h = bounds.Height;
            if (w < 16 || h < 16)
                return false;

            long now = Environment.TickCount64;
            bool changed = false;
            int size = SpriteSize();
            double groundY = h - 1;
            double homeX = HomeX(w);

            if (_needsHomePlacement)
            {
                _needsHomePlacement = false;
                _x = homeX;
                _y = groundY;
                changed = true;
            }

            UpdateMood(now);
            if (UpdateEmoteExpiry(now))
                changed = true;

            // New idle episode (any user activity) re-arms the one-per-episode
            // perch scan (PRD F6).
            long stamp = Monitor.LastActivityStamp;
            if (stamp != _idleEpisodeStamp)
            {
                _idleEpisodeStamp = stamp;
                _perchTriedThisEpisode = false;
            }

            if (FollowAnchor(vp, w, h, groundY, ref changed))
                changed = true;

            long idleMs = Monitor.IdleMilliseconds;
            long walkAfter = Threshold(0);
            long climbAfter = Threshold(1);
            long sleepAfter = Threshold(2);

            switch (_state)
            {
                case PetState.Idle:
                    _y = groundY;
                    if (idleMs >= sleepAfter)
                    {
                        SetState(PetState.Sleep);
                    }
                    else if (idleMs >= climbAfter && !_elevated && !_perchTriedThisEpisode)
                    {
                        _perchTriedThisEpisode = true;
                        TryStartPerch(doc, vp, size, groundY);
                    }
                    else if (idleMs >= walkAfter && now >= _walkPauseUntilMs)
                    {
                        _wanderTargetX = PickWanderX(w, size);
                        SetState(PetState.Walk);
                    }
                    break;

                case PetState.Walk:
                    _y = groundY;
                    if (idleMs < walkAfter || idleMs >= climbAfter)
                    {
                        // User became active again, or the ladder escalated:
                        // let Idle decide the next move.
                        SetState(PetState.Idle);
                        break;
                    }
                    if (MoveHorizontal(_wanderTargetX, dtMs, ref changed))
                    {
                        _walkPauseUntilMs = now + 1500 + _rng.Next(3500);
                        SetState(PetState.Idle);
                    }
                    break;

                case PetState.WalkToPerch:
                    _y = groundY;
                    if (!_hasPerch)
                    {
                        SetState(PetState.Idle);
                        break;
                    }
                    if (MoveHorizontal(_perchScreenX, dtMs, ref changed))
                        SetState(PetState.Climb);
                    break;

                case PetState.Climb:
                    if (!_hasPerch)
                        break; // FollowAnchor already routed to Fall/Idle.
                    MoveHorizontal(_perchScreenX, dtMs * 0.4, ref changed);
                    double climbStep = ClimbSpeedBodiesPerSec * size * dtMs / 1000.0;
                    if (_y - climbStep <= _perchScreenY)
                    {
                        _y = _perchScreenY;
                        _elevated = true;
                        SetState(PetState.Perched);
                    }
                    else
                    {
                        _y -= climbStep;
                    }
                    changed = true;
                    break;

                case PetState.Perched:
                    // Position follows the anchor (FollowAnchor).
                    if (idleMs >= sleepAfter)
                        SetState(PetState.Sleep);
                    break;

                case PetState.Sleep:
                    if (idleMs < 1000)
                    {
                        // Woken by mouse/view activity.
                        ClearEmote();
                        SetState(_elevated ? PetState.Perched : PetState.Idle);
                    }
                    break;

                case PetState.Fall:
                    double fallStep = FallSpeedBodiesPerSec * size * dtMs / 1000.0;
                    if (_y + fallStep >= groundY)
                    {
                        _y = groundY;
                        _wanderTargetX = homeX; // walk back home (bottom-right)
                        SetState(PetState.Walk);
                    }
                    else
                    {
                        _y += fallStep;
                    }
                    changed = true;
                    break;

                case PetState.Petted:
                case PetState.Surprised:
                case PetState.Happy:
                    // One-shots: frame advance below ends them.
                    break;
            }

            // Clamp inside the viewport (window may have been resized).
            double half = size * 0.5;
            double clampedX = Math.Clamp(_x, half, Math.Max(half, w - half));
            if (Math.Abs(clampedX - _x) > 0.01)
            {
                _x = clampedX;
                changed = true;
            }
            if (!_elevated && _state != PetState.Climb && _state != PetState.Fall && _y > groundY)
            {
                _y = groundY;
                changed = true;
            }

            if (AdvanceFrame(dtMs))
                changed = true;

            return changed;
        }

        /// <summary>
        /// Fills in everything the conduit needs for the current frame. Zero heap
        /// allocation. When perched, re-projects the world anchor so the pet
        /// tracks the view even on piggyback redraws while the timer is paused.
        /// </summary>
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

            if (_needsHomePlacement)
            {
                Rectangle b = vp.Bounds;
                if (b.Width < 16 || b.Height < 16)
                    return false;
                _needsHomePlacement = false;
                _x = HomeX(b.Width);
                _y = b.Height - 1;
            }

            if (_hasPerch && _elevated)
            {
                Point2d p = vp.WorldToClient(_anchor);
                _x = p.X;
                _y = p.Y;
            }

            int size = atlas.SpritePixels;
            int left = (int)Math.Round(_x - size * 0.5);
            int top = (int)Math.Round(_y) - size;
            sprite = atlas.GetFrame(CurrentAnimRow(), _frame, _facingLeft);
            petRect = new Rectangle(left, top, size, size);

            if (_emote != EmoteKind.None)
            {
                emote = atlas.GetEmote((int)_emote);
                if (emote != null)
                {
                    int es = atlas.EmotePixels;
                    emoteRect = new Rectangle(
                        (int)Math.Round(_x - es * 0.5),
                        top - es - 2,
                        es, es);
                }
            }
            return true;
        }

        public bool TryGetWorldDrawInfo(out DisplayBitmap sprite, out Point3d position, out float worldSize)
        {
            sprite = null;
            position = Point3d.Origin;
            worldSize = 0;
            return false;
        }

        // ---- internals -----------------------------------------------------

        private int SpriteSize()
        {
            var atlas = PetSystem.Atlas;
            return atlas != null ? atlas.SpritePixels : SpriteAtlas.TileSize * 2;
        }

        private static double HomeX(int viewportWidth)
        {
            // Home is the bottom-right corner (PRD F5), one body width from the edge.
            return Math.Max(viewportWidth - SpriteAtlas.TileSize * 2.0, viewportWidth * 0.5);
        }

        private long Threshold(int step)
        {
            var settings = PetSystem.CurrentSettings;
            int level = settings != null ? (int)settings.Activity : 0;
            return IdleThresholdsMs[Math.Clamp(level, 0, 2), step];
        }

        private double PickWanderX(int viewportWidth, int size)
        {
            double half = size * 0.5;
            double min = half;
            double max = Math.Max(half + 1, viewportWidth - half);
            return min + _rng.NextDouble() * (max - min);
        }

        /// <summary>Moves toward targetX; returns true on arrival.</summary>
        private bool MoveHorizontal(double targetX, double dtMs, ref bool changed)
        {
            double dx = targetX - _x;
            if (Math.Abs(dx) < 2.0)
                return true;
            double step = WalkSpeedBodiesPerSec * SpriteSize() * dtMs / 1000.0;
            if (step >= Math.Abs(dx))
                _x = targetX;
            else
                _x += Math.Sign(dx) * step;
            _facingLeft = dx < 0;
            changed = true;
            return Math.Abs(targetX - _x) < 2.0;
        }

        private void TryStartPerch(RhinoDoc doc, RhinoViewport vp, int size, double groundY)
        {
            if (doc == null || Scanner == null)
                return;
            if (!Scanner.TryFindPerch(doc, vp, size, _x, groundY, out Guid objectId, out Point3d anchor, out Point2d anchorScreen))
                return;
            _hasPerch = true;
            _anchor = anchor;
            _perchScreenX = anchorScreen.X;
            _perchScreenY = anchorScreen.Y;
            Monitor.WatchObject(objectId);
            SetState(PetState.WalkToPerch);
        }

        /// <summary>
        /// Tracks the perch anchor: one point projection per tick (PRD F6). Fires
        /// the fall/return-home path when the anchor object was deleted or its
        /// projection left the screen.
        /// </summary>
        private bool FollowAnchor(RhinoViewport vp, int w, int h, double groundY, ref bool changed)
        {
            if (!_hasPerch)
                return false;

            if (Monitor.ConsumeAnchorDeleted())
            {
                AbandonPerch(groundY);
                return true;
            }

            Point2d p = vp.WorldToClient(_anchor);
            if (p.X < -OffscreenMarginPx || p.X > w + OffscreenMarginPx ||
                p.Y < -OffscreenMarginPx || p.Y > h + OffscreenMarginPx)
            {
                AbandonPerch(groundY);
                return true;
            }

            _perchScreenX = p.X;
            _perchScreenY = p.Y;
            if (_elevated)
            {
                if (Math.Abs(p.X - _x) > 0.25 || Math.Abs(p.Y - _y) > 0.25)
                {
                    _x = p.X;
                    _y = p.Y;
                    changed = true;
                }
            }
            return false;
        }

        private void AbandonPerch(double groundY)
        {
            bool wasAirborne = _elevated || (_state == PetState.Climb && _y < groundY - 2);
            AbandonPerchTracking();
            if (wasAirborne)
                StartFall();
            else if (_state == PetState.WalkToPerch || _state == PetState.Climb)
                SetState(PetState.Idle);
        }

        private void AbandonPerchTracking()
        {
            _hasPerch = false;
            _elevated = false;
            Monitor?.ClearWatch();
        }

        private void StartFall()
        {
            AbandonPerchTracking();
            if (_state == PetState.Fall)
                return;
            SetState(PetState.Fall);
            SetEmote(EmoteKind.Exclaim, Environment.TickCount64 + 1200);
        }

        private void StartOneShot(PetState oneShot)
        {
            // Reactions do not interrupt airborne movement; the emote still shows.
            if (_state == PetState.Fall || _state == PetState.Climb)
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
                case PetState.Climb:
                    return (int)PetAnim.Climb;
                case PetState.Sleep:
                    return (int)PetAnim.Sleep;
                case PetState.Petted:
                    return (int)PetAnim.Petted;
                case PetState.Surprised:
                case PetState.Fall: // no dedicated fall row; surprised reads well
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
                fps *= 0.5; // bored pets blink in slow motion (PRD F7)
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
