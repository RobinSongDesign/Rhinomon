using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace Rhinomon
{
    /// <summary>
    /// Dockable Eto settings panel (PRD F9). Mirrors the Rhinomon command
    /// options; every change applies instantly and persists. Also hosts custom
    /// pet import (PRD F10). Everything runs on the UI thread.
    /// </summary>
    [System.Runtime.InteropServices.Guid("3C9E5A72-1F46-4B8D-B0E3-8D25C7A94F61")]
    public sealed class RhinomonPanel : Panel
    {
        private static readonly string[] BuiltInNames = { "Clawd", "Crab", "Nova" };

        private readonly DropDown _petDrop = new DropDown();
        private readonly Button _importButton = new Button { Text = "Import PNG…" };
        private readonly Button _removeButton = new Button { Text = "Remove" };
        private readonly DropDown _scaleDrop = new DropDown();
        private readonly DropDown _activityDrop = new DropDown();
        private readonly CheckBox _showCheck = new CheckBox { Text = "Show pet" };
        private readonly Button _hideButton = new Button { Text = "Hide permanently (kill switch)" };

        private List<string> _customPets = new List<string>();
        private bool _updating;

        public RhinomonPanel()
        {
            foreach (string s in new[] { "1x", "2x", "3x" })
                _scaleDrop.Items.Add(s);
            foreach (string s in new[] { "Lively", "Normal", "Chill" })
                _activityDrop.Items.Add(s);

            _petDrop.SelectedIndexChanged += (s, e) => ApplyPetSelection();
            _scaleDrop.SelectedIndexChanged += (s, e) => ApplyScale();
            _activityDrop.SelectedIndexChanged += (s, e) => ApplyActivity();
            _showCheck.CheckedChanged += (s, e) => ApplyShow();
            _importButton.Click += (s, e) => ImportPet();
            _removeButton.Click += (s, e) => RemovePet();
            _hideButton.Click += (s, e) => HidePermanently();

            Content = new TableLayout
            {
                Padding = new Padding(10),
                Spacing = new Size(8, 8),
                Rows =
                {
                    new TableRow(new Label { Text = "Pet" }, _petDrop),
                    new TableRow(null, new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Items = { _importButton, _removeButton },
                    }),
                    new TableRow(null, new Label
                    {
                        Text = "Custom sheets: 256×224 PNG, 8×7 grid of 32 px tiles.",
                        TextColor = Colors.Gray,
                        Font = SystemFonts.Default(8),
                    }),
                    new TableRow(new Label { Text = "Scale" }, _scaleDrop),
                    new TableRow(new Label { Text = "Activity" }, _activityDrop),
                    new TableRow(_showCheck, null),
                    new TableRow(_hideButton, null),
                    new TableRow { ScaleHeight = true },
                },
            };

            PetSystem.ConfigChanged += RefreshFromConfig;
            RefreshFromConfig();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                PetSystem.ConfigChanged -= RefreshFromConfig;
            base.Dispose(disposing);
        }

        // ---- config -> UI ----------------------------------------------------

        private void RefreshFromConfig()
        {
            var config = RhinomonPlugin.Instance?.Config;
            if (config == null)
                return;

            _updating = true;
            try
            {
                _customPets = PetLibrary.ListCustomPets();
                _petDrop.Items.Clear();
                foreach (string name in BuiltInNames)
                    _petDrop.Items.Add(name);
                foreach (string name in _customPets)
                    _petDrop.Items.Add(name + " (custom)");

                int petIndex;
                if (!string.IsNullOrEmpty(config.CustomPet))
                {
                    int custom = _customPets.IndexOf(config.CustomPet);
                    petIndex = custom >= 0 ? BuiltInNames.Length + custom : (int)config.Pet;
                }
                else
                {
                    petIndex = (int)config.Pet;
                }
                _petDrop.SelectedIndex = Math.Clamp(petIndex, 0, _petDrop.Items.Count - 1);

                _scaleDrop.SelectedIndex = Math.Clamp(config.Scale - 1, 0, 2);
                _activityDrop.SelectedIndex = Math.Clamp((int)config.Activity, 0, 2);
                _showCheck.Checked = PetSystem.Active;
                _removeButton.Enabled = _petDrop.SelectedIndex >= BuiltInNames.Length;
            }
            finally
            {
                _updating = false;
            }
        }

        // ---- UI -> config ----------------------------------------------------

        private void ApplyPetSelection()
        {
            if (_updating)
                return;
            var plugin = RhinomonPlugin.Instance;
            int index = _petDrop.SelectedIndex;
            if (plugin == null || index < 0)
                return;

            if (index < BuiltInNames.Length)
            {
                plugin.Config.Pet = (PetKind)index;
                plugin.Config.CustomPet = "";
            }
            else
            {
                int custom = index - BuiltInNames.Length;
                if (custom >= _customPets.Count)
                    return;
                plugin.Config.CustomPet = _customPets[custom];
            }
            _removeButton.Enabled = index >= BuiltInNames.Length;
            plugin.SaveConfig();
            PetSystem.ApplyVisualSettings();
        }

        private void ApplyScale()
        {
            if (_updating)
                return;
            var plugin = RhinomonPlugin.Instance;
            if (plugin == null || _scaleDrop.SelectedIndex < 0)
                return;
            plugin.Config.Scale = _scaleDrop.SelectedIndex + 1;
            plugin.SaveConfig();
            PetSystem.ApplyVisualSettings();
        }

        private void ApplyActivity()
        {
            if (_updating)
                return;
            var plugin = RhinomonPlugin.Instance;
            if (plugin == null || _activityDrop.SelectedIndex < 0)
                return;
            plugin.Config.Activity = (ActivityLevel)_activityDrop.SelectedIndex;
            plugin.SaveConfig();
        }

        private void ApplyShow()
        {
            if (_updating)
                return;
            var plugin = RhinomonPlugin.Instance;
            if (plugin == null)
                return;

            bool show = _showCheck.Checked == true;
            if (show && !PetSystem.Active)
            {
                plugin.Config.Hidden = false;
                plugin.Config.Enabled = true;
                PetSystem.Enable(plugin.Config);
            }
            else if (!show && PetSystem.Active)
            {
                plugin.Config.Enabled = false;
                PetSystem.Disable();
            }
            plugin.SaveConfig();
        }

        private void ImportPet()
        {
            var plugin = RhinomonPlugin.Instance;
            if (plugin == null)
                return;

            var dialog = new OpenFileDialog
            {
                Title = "Import Rhinomon sprite sheet",
                Filters = { new FileFilter("PNG sprite sheet (256×224)", ".png") },
            };
            if (dialog.ShowDialog(this) != DialogResult.Ok)
                return;

            if (!PetLibrary.TryImport(dialog.FileName, out string petName, out string error))
            {
                MessageBox.Show(this, error, "Rhinomon", MessageBoxType.Warning);
                return;
            }

            plugin.Config.CustomPet = petName;
            plugin.SaveConfig(); // triggers RefreshFromConfig via ConfigChanged
            PetSystem.ApplyVisualSettings();
            RhinoApp.WriteLine("Rhinomon: imported custom pet \"{0}\".", petName);
        }

        private void RemovePet()
        {
            var plugin = RhinomonPlugin.Instance;
            int index = _petDrop.SelectedIndex - BuiltInNames.Length;
            if (plugin == null || index < 0 || index >= _customPets.Count)
                return;
            string name = _customPets[index];

            if (MessageBox.Show(this,
                    string.Format("Remove custom pet \"{0}\"?", name),
                    "Rhinomon", MessageBoxButtons.YesNo, MessageBoxType.Question) != DialogResult.Yes)
                return;

            if (!PetLibrary.TryDelete(name, out string error))
            {
                MessageBox.Show(this, error, "Rhinomon", MessageBoxType.Warning);
                return;
            }
            plugin.Config.CustomPet = "";
            plugin.SaveConfig();
            PetSystem.ApplyVisualSettings();
        }

        private void HidePermanently()
        {
            var plugin = RhinomonPlugin.Instance;
            if (plugin == null)
                return;
            if (MessageBox.Show(this,
                    "Hide Rhinomon permanently? Run the Rhinomon command to bring it back.",
                    "Rhinomon", MessageBoxButtons.YesNo, MessageBoxType.Question) != DialogResult.Yes)
                return;

            plugin.Config.Hidden = true;
            plugin.Config.Enabled = false;
            PetSystem.Disable();
            plugin.SaveConfig();
        }
    }
}
