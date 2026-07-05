using System;

namespace Rhinomon
{
    /// <summary>
    /// Drives the idle animation with a WinForms timer (UI thread) at 4-6 fps and
    /// enforces the avoidance and degradation rules:
    ///  - timer paused while a command runs, while the view is being dynamically
    ///    manipulated, or while the left mouse button is dragging (PRD F2/P6);
    ///  - viewport redraws sampled continuously; average cost &gt; 25 ms drops the
    ///    animation to 1-2 fps, &gt; 60 ms stops the timer entirely and the pet
    ///    becomes a static sprite drawn only on user-triggered redraws (P3/P4).
    /// </summary>
    internal sealed class PerfGovernor : IDisposable
    {
        private const int NormalIntervalCapMs = 160;      // never faster than ~6 fps
        private const int DegradedIntervalMs = 650;       // level 1: ~1.5 fps
        private const int DynamicDisplayHoldMs = 400;     // treat view as "in drag" this long after a dynamic frame
        private const double DegradeAtMs = 25.0;          // level 0 -> 1
        private const double StopAtMs = 60.0;             // -> level 2
        private const double RecoverLevel1BelowMs = 18.0; // level 1 -> 0 (hysteresis)
        private const double RecoverLevel2BelowMs = 48.0; // level 2 -> 1
        private const int SampleWindow = 8;

        public PetEngine Engine;
        public ActivityMonitor Monitor;

        private System.Windows.Forms.Timer _timer;
        private bool _running;
        private bool _commandRunning;
        private bool _leftButtonDown;
        private long _lastDynamicFrameMs;
        private long _lastTickMs;
        private int _level; // 0 = normal, 1 = degraded, 2 = stopped

        private readonly double[] _samples = new double[SampleWindow];
        private int _sampleIndex;
        private int _sampleCount;

        private int _failTick;

        public void Start()
        {
            if (_timer != null)
                return;
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 200; // 5 fps baseline
            _timer.Tick += OnTick;
            _lastTickMs = Environment.TickCount64;
            _running = true;
            UpdateTimerState();
        }

        public void Stop()
        {
            _running = false;
            _timer?.Stop();
        }

        public void Dispose()
        {
            _running = false;
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer.Dispose();
                _timer = null;
            }
        }

        public void SetCommandRunning(bool running)
        {
            _commandRunning = running;
            UpdateTimerState();
        }

        public void NotifyLeftButton(bool down)
        {
            _leftButtonDown = down;
            UpdateTimerState();
        }

        /// <summary>
        /// Called by the conduit after every drawn frame of the active viewport.
        /// pipelineMs &lt;= 0 means the cost could not be measured for this frame.
        /// </summary>
        public void ReportFrame(double pipelineMs, bool dynamicDisplay)
        {
            if (dynamicDisplay)
                _lastDynamicFrameMs = Environment.TickCount64;

            if (pipelineMs > 0)
            {
                _samples[_sampleIndex] = pipelineMs;
                _sampleIndex = (_sampleIndex + 1) % SampleWindow;
                if (_sampleCount < SampleWindow)
                    _sampleCount++;
                UpdateDegradationLevel();
            }

            // A non-dynamic user frame is also the recovery signal after a view
            // drag ended without any further mouse events.
            if (!dynamicDisplay)
                UpdateTimerState();
        }

        // ---- internals -------------------------------------------------------

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                long now = Environment.TickCount64;
                double dt = now - _lastTickMs;
                _lastTickMs = now;

                if (!_running || _commandRunning || _leftButtonDown)
                    return; // belt and suspenders; the timer should already be stopped
                if (now - _lastDynamicFrameMs < DynamicDisplayHoldMs)
                    return; // view drag in progress: piggyback drawing only

                var engine = Engine;
                if (engine == null)
                    return;

                if (dt < 1)
                    dt = 1;
                else if (dt > 2000)
                    dt = 2000;

                bool changed = engine.Tick(dt);

                // Follow the engine's desired pace (sleep runs at 1 fps).
                int interval = _level == 1
                    ? DegradedIntervalMs
                    : Math.Max(1000 / Math.Clamp(engine.DesiredFps, 1, 6), NormalIntervalCapMs);
                if (_timer != null && _timer.Interval != interval)
                    _timer.Interval = interval;

                if (changed)
                    PetSystem.RedrawActiveView();
                _failTick = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failTick, "PerfGovernor.OnTick", ex);
            }
        }

        private void UpdateDegradationLevel()
        {
            if (_sampleCount < SampleWindow / 2)
                return;

            double sum = 0;
            for (int i = 0; i < _sampleCount; i++)
                sum += _samples[i];
            double avg = sum / _sampleCount;

            int level = _level;
            switch (level)
            {
                case 0:
                    if (avg > StopAtMs) level = 2;
                    else if (avg > DegradeAtMs) level = 1;
                    break;
                case 1:
                    if (avg > StopAtMs) level = 2;
                    else if (avg < RecoverLevel1BelowMs) level = 0;
                    break;
                case 2:
                    if (avg < RecoverLevel2BelowMs) level = 1;
                    break;
            }

            if (level != _level)
            {
                _level = level;
                UpdateTimerState();
            }
        }

        private void UpdateTimerState()
        {
            if (_timer == null)
                return;

            bool shouldRun = _running && _level < 2 && !_commandRunning && !_leftButtonDown;
            if (shouldRun)
            {
                if (!_timer.Enabled)
                {
                    _lastTickMs = Environment.TickCount64;
                    _timer.Interval = _level == 1 ? DegradedIntervalMs : 200;
                    _timer.Start();
                }
            }
            else if (_timer.Enabled)
            {
                _timer.Stop();
            }
        }
    }
}
