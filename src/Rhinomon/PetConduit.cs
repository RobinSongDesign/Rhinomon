using System;
using System.Diagnostics;
using System.Drawing;
using Rhino.Display;

namespace Rhinomon
{
    /// <summary>
    /// Draws the current pet frame plus the emote icon in screen space during the
    /// DrawForeground channel, only for the active viewport. Deliberately dumb:
    /// all behavior lives in PetEngine; this class must stay well under 0.3 ms
    /// per frame (PRD P1) and never allocate.
    /// It also doubles as the pipeline profiler: the time from
    /// CalculateBoundingBox to DrawForeground of the same frame approximates the
    /// viewport redraw cost that drives PerfGovernor's degradation levels.
    /// </summary>
    internal sealed class PetConduit : DisplayConduit
    {
        public IPetEngine Engine;
        public PerfGovernor Governor;

        // Last drawn pet rectangle, used by ClickInterceptor for hit testing
        // without any projection work inside the mouse hook.
        public Rectangle LastPetRect { get; private set; } = Rectangle.Empty;
        public Guid LastPetViewportId { get; private set; } = Guid.Empty;

        private long _frameStartTimestamp;
        private int _failBBox;
        private int _failWorld;
        private int _failDraw;

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            try
            {
                // Never touches e.BoundingBox: the pet lives in screen space and
                // must not influence zoom-extents or clipping.
                if (PetSystem.IsActiveViewport(e.Viewport))
                    _frameStartTimestamp = Stopwatch.GetTimestamp();
                _failBBox = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failBBox, "PetConduit.CalculateBoundingBox", ex);
            }
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            try
            {
                var vp = e.Viewport;
                if (vp == null || !PetSystem.IsActiveViewport(vp))
                    return;

                var display = e.Display;
                var engine = Engine;
                if (display == null || engine == null)
                    return;

                if (engine.TryGetWorldDrawInfo(out DisplayBitmap sprite, out var position, out float worldSize) &&
                    sprite != null && worldSize > 0)
                {
                    display.DrawSprite(sprite, position, worldSize, true);
                }
                _failWorld = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failWorld, "PetConduit.PostDrawObjects", ex);
            }
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            try
            {
                var vp = e.Viewport;
                if (vp == null || !PetSystem.IsActiveViewport(vp))
                    return;

                var display = e.Display;
                var engine = Engine;
                if (display == null || engine == null)
                    return;

                bool dynamic = display.IsDynamicDisplay;

                if (engine.TryGetScreenDrawInfo(vp, out DisplayBitmap sprite, out Rectangle petRect,
                                                out DisplayBitmap emote, out Rectangle emoteRect))
                {
                    if (sprite != null)
                        display.DrawBitmap(sprite, petRect.X, petRect.Y);
                    if (emote != null)
                        display.DrawBitmap(emote, emoteRect.X, emoteRect.Y);
                    LastPetRect = petRect;
                    LastPetViewportId = vp.Id;
                }

                PetSystem.NotifyDpiScale(display.DpiScale);

                // Frame cost sample for the governor. Dynamic-display frames may
                // skip the bbox channel, in which case the duration is unknown (0).
                double pipelineMs = 0;
                long start = _frameStartTimestamp;
                if (start != 0)
                {
                    _frameStartTimestamp = 0;
                    pipelineMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                }
                Governor?.ReportFrame(pipelineMs, dynamic);
                _failDraw = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failDraw, "PetConduit.DrawForeground", ex);
            }
        }
    }
}
