using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Rhinomon
{
    /// <summary>
    /// Store for user-imported pet sprite sheets (PRD F10). Imported PNGs are
    /// normalized into the internal 256x224 sheet layout before they are stored,
    /// so the atlas loader never sees a malformed sheet.
    /// </summary>
    internal static class PetLibrary
    {
        public const int SheetWidth = SpriteAtlas.SheetColumns * SpriteAtlas.TileSize;    // 256
        public const int SheetHeight = SpriteAtlas.AnimationCount * SpriteAtlas.TileSize; // 224
        private const int MaxImportSide = 4096;
        private const long MaxImportPixels = 16_777_216; // 4096 x 4096

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
                using Bitmap sheet = TryLoadNormalizedSheet(sourcePath, out error);
                if (sheet == null)
                    return false;

                string baseName = SanitizeName(Path.GetFileNameWithoutExtension(sourcePath));
                Directory.CreateDirectory(StoreDirectory);

                // Never overwrite an existing pet: suffix until free.
                string name = baseName;
                int suffix = 2;
                while (File.Exists(PathFor(name)))
                    name = baseName + "-" + suffix++;

                sheet.Save(PathFor(name), ImageFormat.Png);
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

        private static Bitmap TryLoadNormalizedSheet(string path, out string error)
        {
            error = null;
            try
            {
                using var bmp = new Bitmap(path);
                if (bmp.Width <= 0 || bmp.Height <= 0 ||
                    bmp.Width > MaxImportSide || bmp.Height > MaxImportSide ||
                    (long)bmp.Width * bmp.Height > MaxImportPixels)
                {
                    error = string.Format(
                        "PNG must be no larger than {0}x{0} px / {1} megapixels; this file is {2}x{3}.",
                        MaxImportSide, MaxImportPixels / 1_000_000, bmp.Width, bmp.Height);
                    return null;
                }

                if (bmp.Width == SheetWidth && bmp.Height == SheetHeight)
                    return new Bitmap(bmp);

                if (IsAllowedScaledSheet(bmp.Width, bmp.Height))
                    return ResizeSheet(bmp);

                return BuildSheetFromSingleImage(bmp);
            }
            catch (Exception)
            {
                error = "Not a readable PNG image.";
                return null;
            }
        }

        private static bool IsAllowedScaledSheet(int width, int height)
        {
            return (width == SheetWidth * 2 && height == SheetHeight * 2) ||
                   (width == SheetWidth * 4 && height == SheetHeight * 4);
        }

        private static Bitmap ResizeSheet(Bitmap source)
        {
            var result = new Bitmap(SheetWidth, SheetHeight, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(result);
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceOver;
            g.DrawImage(source, new Rectangle(0, 0, SheetWidth, SheetHeight));
            return result;
        }

        private static Bitmap BuildSheetFromSingleImage(Bitmap source)
        {
            using Bitmap tile = BuildTile(source);
            var sheet = new Bitmap(SheetWidth, SheetHeight, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(sheet);
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceOver;

            for (int row = 0; row < SpriteAtlas.AnimationCount; row++)
            {
                int frames = SpriteAtlas.FrameCounts[row];
                for (int col = 0; col < frames; col++)
                {
                    FrameOffset(row, col, out int dx, out int dy);
                    g.DrawImageUnscaled(tile, col * SpriteAtlas.TileSize + dx, row * SpriteAtlas.TileSize + dy);
                }
            }
            return sheet;
        }

        private static Bitmap BuildTile(Bitmap source)
        {
            Rectangle content = FindContentBounds(source);
            int maxDst = SpriteAtlas.TileSize - 4;
            double scale = Math.Min((double)maxDst / content.Width, (double)maxDst / content.Height);
            int dstW = Math.Max(1, (int)Math.Round(content.Width * scale));
            int dstH = Math.Max(1, (int)Math.Round(content.Height * scale));
            int dstX = (SpriteAtlas.TileSize - dstW) / 2;
            int dstY = SpriteAtlas.TileSize - dstH - 2;

            var tile = new Bitmap(SpriteAtlas.TileSize, SpriteAtlas.TileSize, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(tile);
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceOver;
            g.DrawImage(source, new Rectangle(dstX, dstY, dstW, dstH), content, GraphicsUnit.Pixel);
            return tile;
        }

        private static Rectangle FindContentBounds(Bitmap source)
        {
            bool hasAlpha = (source.Flags & (int)ImageFlags.HasAlpha) != 0;
            if (!hasAlpha)
                return new Rectangle(0, 0, source.Width, source.Height);

            int minX = source.Width, minY = source.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    if (source.GetPixel(x, y).A <= 8)
                        continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY)
                return new Rectangle(0, 0, source.Width, source.Height);
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        private static void FrameOffset(int row, int col, out int dx, out int dy)
        {
            dx = 0;
            dy = 0;
            switch ((PetAnim)row)
            {
                case PetAnim.Idle:
                    dy = col == 1 ? -1 : 0;
                    break;
                case PetAnim.Walk:
                    dx = col == 0 ? -1 : (col == 2 ? 1 : 0);
                    dy = col == 1 || col == 3 ? -1 : 0;
                    break;
                case PetAnim.Climb:
                    dy = col % 2 == 0 ? 1 : -1;
                    break;
                case PetAnim.Sleep:
                    dy = col == 1 ? 1 : 0;
                    break;
                case PetAnim.Petted:
                case PetAnim.Happy:
                    dy = col == 1 || col == 2 ? -1 : 0;
                    break;
                case PetAnim.Surprised:
                    dy = col == 1 ? -2 : 0;
                    break;
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
