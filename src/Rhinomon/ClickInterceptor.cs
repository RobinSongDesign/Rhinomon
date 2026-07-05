using System;
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
