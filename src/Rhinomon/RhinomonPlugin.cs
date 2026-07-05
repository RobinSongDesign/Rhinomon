using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Display;
using Rhino.PlugIns;

// Plug-in GUID. Fixed forever once published (Rhino identifies the plug-in by it).
[assembly: Guid("7A5FAD47-9E13-4E58-8C3A-2B6E90D1F3B4")]

namespace Rhinomon
{
    public enum PetKind
    {
        Clawd = 0,
        Crab = 1,
        Nova = 2,
    }

    public enum ActivityLevel
    {
        Lively = 0,
        Normal = 1,
        Chill = 2,
    }

    /// <summary>
    /// User configuration persisted through PlugIn.Settings.
    /// </summary>
    internal sealed class PetSettings
    {
        public PetKind Pet = PetKind.Clawd;
        public int Scale = 2;
        public ActivityLevel Activity = ActivityLevel.Lively;
        public bool Enabled;
        public bool Hidden;

        public void Load(PersistentSettings s)
        {
            Pet = s.GetEnumValue("Pet", PetKind.Clawd);
            Scale = Math.Clamp(s.GetInteger("Scale", 2), 1, 3);
            Activity = s.GetEnumValue("Activity", ActivityLevel.Lively);
            Enabled = s.GetBool("Enabled", false);
            Hidden = s.GetBool("Hidden", false);
        }

        public void Save(PersistentSettings s)
        {
            s.SetEnumValue("Pet", Pet);
            s.SetInteger("Scale", Scale);
            s.SetEnumValue("Activity", Activity);
            s.SetBool("Enabled", Enabled);
            s.SetBool("Hidden", Hidden);
        }
    }

    /// <summary>
    /// Exception containment for every conduit/event callback. Each call site owns
    /// an int counter field; three consecutive failures at the same site disable
    /// the pet entirely so a broken callback can never take Rhino down with it.
    /// </summary>
    internal static class Guard
    {
        public const int MaxConsecutiveFailures = 3;

        public static void Fail(ref int counter, string site, Exception ex)
        {
            counter++;
            if (counter == MaxConsecutiveFailures)
            {
                RhinoApp.WriteLine(
                    "Rhinomon: repeated errors in {0} ({1}: {2}). Pet disabled for this session.",
                    site, ex.GetType().Name, ex.Message);
                PetSystem.RequestDisable();
            }
        }
    }

    /// <summary>
    /// Owns and wires all pet subsystems. Everything runs on the Rhino UI thread,
    /// so no locking anywhere.
    /// </summary>
    internal static class PetSystem
    {
        public static bool Active { get; private set; }
        public static PetSettings CurrentSettings { get; private set; }
        public static SpriteAtlas Atlas { get; private set; }
        public static PetEngine Engine { get; private set; }
        public static ActivityMonitor Monitor { get; private set; }
        public static PetConduit Conduit { get; private set; }
        public static ClickInterceptor Interceptor { get; private set; }
        public static PerfGovernor Governor { get; private set; }

        private static Guid _activeViewportId;
        private static int _dpiMultiplier = 1;
        private static bool _disableQueued;
        private static bool _atlasRebuildQueued;

        public static void Enable(PetSettings settings)
        {
            if (Active)
                return;

            CurrentSettings = settings;
            Atlas = new SpriteAtlas(settings.Pet, EffectiveScale());

            Engine = new PetEngine();
            Monitor = new ActivityMonitor();
            Conduit = new PetConduit();
            Interceptor = new ClickInterceptor();
            Governor = new PerfGovernor();

            Engine.Monitor = Monitor;
            Engine.Scanner = new PerchScanner();
            Monitor.Engine = Engine;
            Monitor.Governor = Governor;
            Conduit.Engine = Engine;
            Conduit.Governor = Governor;
            Interceptor.Monitor = Monitor;
            Interceptor.Conduit = Conduit;
            Interceptor.Engine = Engine;
            Interceptor.Governor = Governor;
            Governor.Engine = Engine;
            Governor.Monitor = Monitor;

            var view = RhinoDoc.ActiveDoc?.Views.ActiveView;
            _activeViewportId = view != null ? view.ActiveViewportID : Guid.Empty;
            Engine.ResetToHome();

            Monitor.Start();
            Conduit.Enabled = true;
            Interceptor.Enabled = true;
            Governor.Start();

            Active = true;
            RedrawActiveView();
        }

        public static void Disable()
        {
            if (!Active)
                return;
            Active = false;

            Governor?.Dispose();
            if (Interceptor != null)
                Interceptor.Enabled = false;
            if (Conduit != null)
                Conduit.Enabled = false;
            Monitor?.Stop();
            Atlas?.Dispose();

            Governor = null;
            Interceptor = null;
            Conduit = null;
            Monitor = null;
            Engine = null;
            Atlas = null;

            // Erase the last drawn frame. No command can be running here in the
            // usual paths (command UI / idle handler), and RedrawActiveView is
            // guarded anyway.
            RedrawActiveView();
        }

        /// <summary>Rebuilds the sprite atlas after a Pet/Scale settings change.</summary>
        public static void ApplyVisualSettings()
        {
            if (!Active)
                return;
            var old = Atlas;
            Atlas = new SpriteAtlas(CurrentSettings.Pet, EffectiveScale());
            old?.Dispose();
            RedrawActiveView();
        }

        /// <summary>
        /// The only place the plug-in ever asks for a redraw. Guarded so that the
        /// plug-in never generates a redraw while a command is running (PRD P6).
        /// </summary>
        public static void RedrawActiveView()
        {
            if (Monitor != null && Monitor.CommandRunning)
                return;
            RhinoDoc.ActiveDoc?.Views.ActiveView?.Redraw();
        }

        /// <summary>
        /// Deferred kill used by Guard: tearing subsystems down from inside their
        /// own callbacks (display pipeline, mouse hook) is unsafe, so the actual
        /// disable happens on the next application idle.
        /// </summary>
        public static void RequestDisable()
        {
            if (_disableQueued)
                return;
            _disableQueued = true;
            Governor?.Stop();
            RhinoApp.Idle += DisableOnIdle;
        }

        private static void DisableOnIdle(object sender, EventArgs e)
        {
            RhinoApp.Idle -= DisableOnIdle;
            _disableQueued = false;
            Disable();
        }

        public static void SetActiveViewportId(Guid id)
        {
            _activeViewportId = id;
        }

        public static bool IsActiveViewport(RhinoViewport viewport)
        {
            return viewport != null && viewport.Id == _activeViewportId;
        }

        /// <summary>
        /// Called from the conduit with the pipeline DPI scale. Sprites are cached
        /// pre-scaled with nearest-neighbor, so a DPI change requires an atlas
        /// rebuild; that is deferred to idle time (never inside a draw callback).
        /// </summary>
        public static void NotifyDpiScale(float dpiScale)
        {
            int mult = Math.Clamp((int)Math.Round(dpiScale), 1, 3);
            if (mult == _dpiMultiplier || _atlasRebuildQueued)
                return;
            _dpiMultiplier = mult;
            _atlasRebuildQueued = true;
            RhinoApp.Idle += RebuildAtlasOnIdle;
        }

        private static void RebuildAtlasOnIdle(object sender, EventArgs e)
        {
            RhinoApp.Idle -= RebuildAtlasOnIdle;
            _atlasRebuildQueued = false;
            ApplyVisualSettings();
        }

        private static int EffectiveScale()
        {
            return Math.Clamp(CurrentSettings.Scale * _dpiMultiplier, 1, 6);
        }
    }

    public sealed class RhinomonPlugin : PlugIn
    {
        public static RhinomonPlugin Instance { get; private set; }

        internal PetSettings Config { get; } = new PetSettings();

        public RhinomonPlugin()
        {
            Instance = this;
        }

        // Zero impact on Rhino startup: the plug-in loads the first time the
        // Rhinomon command is used (PRD P5).
        public override PlugInLoadTime LoadTime => PlugInLoadTime.WhenNeeded;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Config.Load(Settings);
            if (Config.Enabled && !Config.Hidden)
                PetSystem.Enable(Config);
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            PetSystem.Disable();
            SaveConfig();
            base.OnShutdown();
        }

        internal void SaveConfig()
        {
            Config.Save(Settings);
            SaveSettings();
        }
    }
}
