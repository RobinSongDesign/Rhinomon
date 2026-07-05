using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace Rhinomon
{
    internal interface IPetEngine
    {
        int DesiredFps { get; }
        void ResetToHome();
        void OnStrongInterrupt();
        void OnViewportChanged();
        void OnPetted();
        void React(PetReaction reaction);
        bool Tick(double dtMs);

        // Screen-space payload: real sprite in Screen mode; emote-only
        // (sprite=null, petRect still filled for click hit-testing) in World mode.
        bool TryGetScreenDrawInfo(
            RhinoViewport vp,
            out DisplayBitmap sprite,
            out Rectangle petRect,
            out DisplayBitmap emote,
            out Rectangle emoteRect);

        // World-space payload: false in Screen mode.
        bool TryGetWorldDrawInfo(
            out DisplayBitmap sprite,
            out Point3d position,
            out float worldSize);
    }
}
