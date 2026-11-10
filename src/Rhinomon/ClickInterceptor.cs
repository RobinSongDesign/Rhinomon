using System;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.UI;

namespace Rhinomon
{
    /// <summary>
    /// Conditional left-click interception (PRD F3): the click is eaten and turned
    /// into a "petted" reaction only when no command is running AND the click
    /// lands inside the pet's last drawn screen rectangle. While a command runs,
    /// every handler returns immediately - zero logic, zero interference with
    /// picking. Mouse move feeds the idle clock; button state feeds the
    /// governor's pause-while-dragging rule.
    /// </summary>
    internal sealed class ClickInterceptor : MouseCallback
    {
        public ActivityMonitor Monitor;
        public PetConduit Conduit;
        public IPetEngine Engine;
        public PerfGovernor Governor;

        private int _failDown;
        private int _failUp;
        private int _failMove;
        private int _failOcclusion;

        protected override void OnMouseDown(MouseCallbackEventArgs e)
        {
            try
            {
                var monitor = Monitor;
                if (monitor == null || monitor.CommandRunning)
                    return; // pass straight through during commands

                if (e.MouseButton != MouseButton.Left)
                    return;

                Governor?.NotifyLeftButton(true);
                monitor.TouchActivity();

                var conduit = Conduit;
                var view = e.View;
                if (conduit == null || view == null)
                    return;
                if (conduit.LastPetViewportId != view.ActiveViewportID)
                    return;
                if (!conduit.LastPetRect.Contains(e.ViewportPoint))
                    return;
                // World mode: a pet hidden behind geometry must not steal the
                // click meant for the object in front of it.
                if (IsPetOccluded(view, e.ViewportPoint))
                    return;

                // Hit: eat the click so Rhino never starts a selection, and pet the pet.
                e.Cancel = true;
                Engine?.OnPetted();
                PetSystem.RedrawActiveView();
                _failDown = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failDown, "ClickInterceptor.OnMouseDown", ex);
            }
        }

        /// <summary>
        /// World mode only: true when a pickable object lies in front of the pet
        /// under the cursor. Runs Rhino's own point pick once, and only for a
        /// click that already landed inside the pet rectangle, so it costs what
        /// the click would have cost anyway. Any doubt (no pick point, pick
        /// failure) keeps the previous behavior: the pet takes the click.
        /// </summary>
        private bool IsPetOccluded(RhinoView view, System.Drawing.Point clientPoint)
        {
            if (_failOcclusion >= Guard.MaxConsecutiveFailures)
                return false;

            var engine = Engine;
            if (engine == null ||
                !engine.TryGetWorldDrawInfo(out _, out Point3d petCenter, out float worldSize))
                return false; // Screen mode: the pet is a HUD sprite on top of everything

            try
            {
                var doc = view.Document;
                var vp = view.ActiveViewport;
                if (doc == null || vp == null)
                    return false;
                if (!vp.GetFrustumLine(clientPoint.X, clientPoint.Y, out Line pickLine))
                    return false;

                Vector3d viewDir = vp.CameraDirection;
                if (!viewDir.Unitize())
                    return false;
                Point3d camera = vp.CameraLocation;
                double petDepth = (petCenter - camera) * viewDir;

                bool occluded = false;
                using (var pick = new PickContext())
                {
                    pick.View = view;
                    pick.PickStyle = PickStyle.PointPick;
                    // Match what the user sees: surfaces only hide the sprite
                    // (and only catch clicks) in shaded display modes.
                    var mode = vp.DisplayMode;
                    pick.PickMode = mode != null && mode.SupportsShading
                        ? PickMode.Shaded
                        : PickMode.Wireframe;
                    pick.SetPickTransform(vp.GetPickTransform(clientPoint));
                    pick.PickLine = pickLine;
                    pick.UpdateClippingPlanes();

                    ObjRef[] hits = doc.Objects.PickObjects(pick);
                    if (hits != null)
                    {
                        for (int i = 0; i < hits.Length; i++)
                        {
                            ObjRef hit = hits[i];
                            if (hit == null)
                                continue;
                            Point3d p = hit.SelectionPoint();
                            // Half a body of slack: the object the pet stands on
                            // or climbs along is not "in front" of it.
                            if (p.IsValid && (p - camera) * viewDir < petDepth - worldSize * 0.5)
                                occluded = true;
                            hit.Dispose();
                        }
                    }
                }
                _failOcclusion = 0;
                return occluded;
            }
            catch (Exception)
            {
                // Not worth disabling the pet over: after repeated failures the
                // test is simply skipped for the rest of the session.
                _failOcclusion++;
                return false;
            }
        }

        protected override void OnMouseUp(MouseCallbackEventArgs e)
        {
            try
            {
                // Always clear the drag flag, even during commands: releasing a
                // stale pause must never depend on command state.
                if (e.MouseButton == MouseButton.Left)
                    Governor?.NotifyLeftButton(false);

                var monitor = Monitor;
                if (monitor == null || monitor.CommandRunning)
                    return;
                monitor.TouchActivity();
                _failUp = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failUp, "ClickInterceptor.OnMouseUp", ex);
            }
        }

        protected override void OnMouseMove(MouseCallbackEventArgs e)
        {
            try
            {
                var monitor = Monitor;
                if (monitor == null || monitor.CommandRunning)
                    return;
                monitor.TouchActivity(); // mouse motion inside a viewport = not idle
                _failMove = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failMove, "ClickInterceptor.OnMouseMove", ex);
            }
        }
    }
}
