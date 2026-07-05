using System;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Rhinomon
{
    /// <summary>
    /// The single "Rhinomon" command (PRD F8):
    ///   Enter        - toggle the pet on/off
    ///   Pet          - Clawd / Crab / Nova
    ///   Scale        - 1 / 2 / 3
    ///   Activity     - Lively / Normal / Chill
    ///   Mode         - Screen / World
    ///   WorldSize    - model-unit size for World mode; 0 means automatic
    ///   Hide         - kill switch with confirmation; running Rhinomon again restores
    /// All settings persist via PlugIn.Settings.
    /// </summary>
    public sealed class RhinomonCommand : Command
    {
        private static readonly string[] PetNames = { "Clawd", "Crab", "Nova" };
        // Rhino's command-line parser only accepts option tokens that start with
        // a letter; purely numeric list values ("1"/"2"/"3") are dead on click
        // and on typing. Bare numbers are still accepted via AcceptNumber below.
        private static readonly string[] ScaleNames = { "x1", "x2", "x3" };
        private static readonly string[] ActivityNames = { "Lively", "Normal", "Chill" };
        private static readonly string[] ModeNames = { "Screen", "World" };

        public override string EnglishName => "Rhinomon";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = RhinomonPlugin.Instance;
            if (plugin == null)
                return Result.Failure;
            PetSettings config = plugin.Config;

            // Kill-switch recovery: running the command while hidden restores the pet.
            if (config.Hidden)
            {
                config.Hidden = false;
                config.Enabled = true;
                plugin.SaveConfig();
                PetSystem.Enable(config);
                RhinoApp.WriteLine("Rhinomon: welcome back! Your pet has returned.");
                return Result.Success;
            }

            bool optionChanged = false;
            while (true)
            {
                // Rebuilt each pass so the option lists show the current values.
                var go = new GetOption();
                go.SetCommandPrompt(string.Format(
                    "Rhinomon is {0}. Press Enter to {1}",
                    PetSystem.Active ? "on" : "off",
                    optionChanged ? "finish" : (PetSystem.Active ? "hide your pet" : "summon your pet")));
                go.AcceptNothing(true);
                go.AcceptNumber(true, false); // typing 1/2/3 sets the scale directly
                int optPet = go.AddOptionList("Pet", PetNames, (int)config.Pet);
                int optScale = go.AddOptionList("Scale", ScaleNames, config.Scale - 1);
                int optActivity = go.AddOptionList("Activity", ActivityNames, (int)config.Activity);
                int optMode = go.AddOptionList("Mode", ModeNames, (int)config.Mode);
                int optWorldSize = -1;
                var worldSizeOption = new OptionDouble(Math.Max(0.0, config.WorldSize), 0.0, 1.0e9);
                if (config.Mode == PetDisplayMode.World)
                    optWorldSize = go.AddOptionDouble("WorldSize", ref worldSizeOption);
                int optPanel = go.AddOption("Panel");
                int optHide = go.AddOption("Hide");

                GetResult res = go.Get();

                if (res == GetResult.Nothing)
                {
                    // Bare Enter toggles; Enter after changing options just finishes,
                    // so tweaking the scale never accidentally dismisses the pet.
                    if (!optionChanged)
                        Toggle(plugin, config);
                    return Result.Success;
                }

                if (res == GetResult.Number)
                {
                    int scale = (int)Math.Round(go.Number());
                    if (scale >= 1 && scale <= 3)
                    {
                        ApplyScale(plugin, config, scale);
                        optionChanged = true;
                    }
                    else
                    {
                        RhinoApp.WriteLine("Rhinomon: scale must be 1, 2 or 3.");
                    }
                    continue;
                }

                if (res != GetResult.Option)
                    return res == GetResult.Cancel ? Result.Cancel : Result.Success;

                CommandLineOption option = go.Option();
                if (option == null)
                    continue;
                int index = option.Index;

                if (index == optPet)
                {
                    var pet = (PetKind)Math.Clamp(option.CurrentListOptionIndex, 0, PetNames.Length - 1);
                    if (pet != config.Pet)
                    {
                        config.Pet = pet;
                        plugin.SaveConfig();
                        PetSystem.ApplyVisualSettings();
                    }
                    optionChanged = true;
                }
                else if (index == optScale)
                {
                    ApplyScale(plugin, config, option.CurrentListOptionIndex + 1);
                    optionChanged = true;
                }
                else if (index == optActivity)
                {
                    config.Activity = (ActivityLevel)Math.Clamp(
                        option.CurrentListOptionIndex, 0, ActivityNames.Length - 1);
                    plugin.SaveConfig();
                    optionChanged = true;
                }
                else if (index == optMode)
                {
                    var displayMode = (PetDisplayMode)Math.Clamp(
                        option.CurrentListOptionIndex, 0, ModeNames.Length - 1);
                    if (displayMode != config.Mode)
                    {
                        config.Mode = displayMode;
                        plugin.SaveConfig();
                        RestartIfActive(config);
                    }
                    optionChanged = true;
                }
                else if (index == optWorldSize)
                {
                    ApplyWorldSize(plugin, config, worldSizeOption.CurrentValue);
                    optionChanged = true;
                }
                else if (index == optPanel)
                {
                    Rhino.UI.Panels.OpenPanel(typeof(RhinomonPanel).GUID);
                    return Result.Success;
                }
                else if (index == optHide)
                {
                    bool confirmed = false;
                    Result confirmRes = RhinoGet.GetBool(
                        "Hide Rhinomon permanently? (Run Rhinomon again to bring it back)",
                        true, "No", "Yes", ref confirmed);
                    if (confirmRes == Result.Success && confirmed)
                    {
                        config.Hidden = true;
                        config.Enabled = false;
                        plugin.SaveConfig();
                        PetSystem.Disable();
                        RhinoApp.WriteLine("Rhinomon: pet hidden. Run Rhinomon again to bring it back.");
                        return Result.Success;
                    }
                    // Declined: fall through and keep showing the options.
                }
            }
        }

        private static void ApplyScale(RhinomonPlugin plugin, PetSettings config, int scale)
        {
            scale = Math.Clamp(scale, 1, 3);
            if (scale == config.Scale)
                return;
            config.Scale = scale;
            plugin.SaveConfig();
            PetSystem.ApplyVisualSettings();
        }

        private static void ApplyWorldSize(RhinomonPlugin plugin, PetSettings config, double worldSize)
        {
            worldSize = Math.Max(0.0, worldSize);
            if (Math.Abs(worldSize - config.WorldSize) < 1e-9)
                return;
            config.WorldSize = worldSize;
            plugin.SaveConfig();
            RestartIfActive(config);
        }

        private static void RestartIfActive(PetSettings config)
        {
            if (!PetSystem.Active)
                return;
            PetSystem.Disable();
            PetSystem.Enable(config);
        }

        private static void Toggle(RhinomonPlugin plugin, PetSettings config)
        {
            if (PetSystem.Active)
            {
                PetSystem.Disable();
                config.Enabled = false;
                RhinoApp.WriteLine("Rhinomon: pet dismissed. Run Rhinomon to summon it again.");
            }
            else
            {
                PetSystem.Enable(config);
                config.Enabled = true;
                if (PetSystem.Atlas != null && PetSystem.Atlas.UsingPlaceholder)
                    RhinoApp.WriteLine("Rhinomon: pet enabled (placeholder art - sprite assets missing).");
                else
                    RhinoApp.WriteLine("Rhinomon: pet enabled.");
            }
            plugin.SaveConfig();
        }
    }
}
