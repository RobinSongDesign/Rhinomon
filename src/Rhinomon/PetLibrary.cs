using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace Rhinomon
{
    /// <summary>
    /// Store for user-imported pet sprite sheets (PRD F10). Imported PNGs are
    /// copied to %AppData%\Rhinomon\pets and must follow docs/CONTRACT.md layout
    /// exactly (256x224, 8x7 grid of 32 px tiles); anything else is rejected at
    /// import time so the atlas loader never sees a malformed sheet.
    /// </summary>
    internal static class PetLibrary
    {
        public const int SheetWidth = SpriteAtlas.SheetColumns * SpriteAtlas.TileSize;    // 256
        public const int SheetHeight = SpriteAtlas.AnimationCount * SpriteAtlas.TileSize; // 224

        public static string StoreDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Rhinomon", "pets");

        /// <summary>Names (without extension) of all stored custom pets, sorted.</summary>
        public static List<string> ListCustomPets()
        {
            var names = new List<string>();
            try
            {
                if (!Directory.Exists(StoreDirectory))
                    return names;
                foreach (string file in Directory.GetFiles(StoreDirectory, "*.png"))
                    names.Add(Path.GetFileNameWithoutExtension(file));
                names.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Unreadable store: behave as if empty.
            }
            return names;
        }

        public static string PathFor(string name)
        {
            return Path.Combine(StoreDirectory, name + ".png");
        }

        public static bool TryImport(string sourcePath, out string petName, out string error)
        {
            petName = null;
            error = null;
            try
            {
                if (!File.Exists(sourcePath))
                {
                    error = "File not found.";
                    return false;
                }
                if (!ValidateSheet(sourcePath, out error))
                    return false;

                string baseName = SanitizeName(Path.GetFileNameWithoutExtension(sourcePath));
                Directory.CreateDirectory(StoreDirectory);

                // Never overwrite an existing pet: suffix until free.
                string name = baseName;
                int suffix = 2;
                while (File.Exists(PathFor(name)))
                    name = baseName + "-" + suffix++;

                File.Copy(sourcePath, PathFor(name));
                petName = name;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryDelete(string name, out string error)
        {
            error = null;
            try
            {
                string path = PathFor(name);
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool ValidateSheet(string path, out string error)
        {
            error = null;
            try
            {
                using var bmp = new Bitmap(path);
                if (bmp.Width != SheetWidth || bmp.Height != SheetHeight)
                {
                    error = string.Format(
                        "Sheet must be exactly {0}x{1} px (8 columns x 7 rows of 32 px tiles, " +
                        "see docs/CONTRACT.md); this file is {2}x{3}.",
                        SheetWidth, SheetHeight, bmp.Width, bmp.Height);
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                error = "Not a readable PNG image.";
                return false;
            }
        }

        private static string SanitizeName(string fileName)
        {
            var chars = new char[fileName.Length];
            int n = 0;
            foreach (char c in fileName)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    chars[n++] = c;
            }
            string name = new string(chars, 0, n);
            return name.Length == 0 ? "custom" : name;
        }
    }
}
