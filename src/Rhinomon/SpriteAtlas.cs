using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using Rhino.Display;

namespace Rhinomon
{
    /// <summary>
    /// Animation rows of the pet sprite sheet. Values are row indices in the
    /// sheet and must match docs/CONTRACT.md exactly.
    /// </summary>
    internal enum PetAnim
    {
        Idle = 0,
        Walk = 1,
        Climb = 2,
        Sleep = 3,
        Petted = 4,
        Surprised = 5,
        Happy = 6,
    }

    /// <summary>
    /// Emote icons, in the fixed column order of assets/emotes.png
    /// (see docs/CONTRACT.md).
    /// </summary>
    internal enum EmoteKind
    {
        None = -1,
        Heart = 0,
        Exclaim = 1,
        Question = 2,
        Zzz = 3,
        Sparkle = 4,
    }

    /// <summary>
    /// Loads the embedded sprite sheets, slices them into frames, pre-scales each
    /// frame with nearest-neighbor (runtime linear filtering would blur the
    /// pixels) and caches everything as DisplayBitmaps, including a horizontally
    /// flipped set for left-facing movement. Falls back to magenta placeholder
    /// tiles when assets are missing, so the plug-in works without any assets.
    /// </summary>
    internal sealed class SpriteAtlas : IDisposable
    {
        // Sheet layout per docs/CONTRACT.md: 32 px grid, 8 columns, 7 rows,
        // frames left-aligned per row.
        public const int TileSize = 32;
        public const int SheetColumns = 8;
        public const int AnimationCount = 7;
        public const int EmoteTileSize = 16;
        public const int EmoteCount = 5;

        // Frame counts and playback rates per animation row (docs/CONTRACT.md).
        public static readonly int[] FrameCounts = { 4, 4, 4, 2, 4, 2, 4 };
        public static readonly int[] FrameRates = { 4, 6, 6, 1, 6, 6, 6 };

        private static readonly string[] SheetNames = { "clawd.png", "crab.png", "nova.png" };

        private readonly DisplayBitmap[][] _framesRight = new DisplayBitmap[AnimationCount][];
        private readonly DisplayBitmap[][] _framesLeft = new DisplayBitmap[AnimationCount][];
        private readonly DisplayBitmap[] _emotes = new DisplayBitmap[EmoteCount];

        // DisplayBitmap does not document whether it copies the source pixels, so
        // the GDI bitmaps are kept alive for the atlas lifetime and disposed with it.
        private readonly List<Bitmap> _ownedBitmaps = new List<Bitmap>();
        private readonly List<DisplayBitmap> _ownedDisplayBitmaps = new List<DisplayBitmap>();

        private bool _disposed;
        private int _spritePixels;

        public int Scale { get; }
        public bool UsingPlaceholder { get; private set; }

        public int SpritePixels => _spritePixels;
        public int EmotePixels => EmoteTileSize * Scale;

        public SpriteAtlas(PetKind pet, int scale)
        {
            Scale = Math.Clamp(scale, 1, 6);
            SliceSheet(LoadEmbeddedBitmap(SheetNames[Math.Clamp((int)pet, 0, SheetNames.Length - 1)]), TileSize);
            LoadEmoteSheet();
        }

        /// <summary>Atlas for a user-imported sheet (PRD F10). Emotes stay built in.</summary>
        public SpriteAtlas(string customSheetPath, int scale)
        {
            Scale = Math.Clamp(scale, 1, 6);
            Bitmap sheet = LoadFileBitmap(customSheetPath);
            SliceSheet(sheet, DetectTileSize(sheet));
            LoadEmoteSheet();
        }

        public DisplayBitmap GetFrame(int anim, int frame, bool facingLeft)
        {
            if (anim < 0 || anim >= AnimationCount)
                anim = 0;
            var set = facingLeft ? _framesLeft[anim] : _framesRight[anim];
            if (frame < 0 || frame >= set.Length)
                frame = 0;
            return set[frame];
        }

        public DisplayBitmap GetEmote(int emote)
        {
            if (emote < 0 || emote >= EmoteCount)
                return null;
            return _emotes[emote];
        }

        private void SliceSheet(Bitmap sheet, int sourceTileSize)
        {
            if (sheet == null)
            {
                UsingPlaceholder = true;
                sourceTileSize = TileSize;
                sheet = MakePlaceholderSheet(SheetColumns * TileSize, AnimationCount * TileSize, TileSize);
            }

            sourceTileSize = Math.Clamp(sourceTileSize, TileSize, TileSize * 4);
            _spritePixels = Math.Max(TileSize * Scale, sourceTileSize);

            using (sheet)
            {
                for (int row = 0; row < AnimationCount; row++)
                {
                    int frames = FrameCounts[row];
                    _framesRight[row] = new DisplayBitmap[frames];
                    _framesLeft[row] = new DisplayBitmap[frames];
                    for (int col = 0; col < frames; col++)
                    {
                        Bitmap right = ExtractScaledTile(
                            sheet,
                            col * sourceTileSize,
                            row * sourceTileSize,
                            sourceTileSize,
                            _spritePixels);
                        var left = (Bitmap)right.Clone();
                        left.RotateFlip(RotateFlipType.RotateNoneFlipX);
                        _framesRight[row][col] = Wrap(right);
                        _framesLeft[row][col] = Wrap(left);
                    }
                }
            }
        }

        private void LoadEmoteSheet()
        {
            Bitmap sheet = LoadEmbeddedBitmap("emotes.png");
            if (sheet == null)
            {
                UsingPlaceholder = true;
                sheet = MakePlaceholderSheet(EmoteCount * EmoteTileSize, EmoteTileSize, EmoteTileSize);
            }

            using (sheet)
            {
                for (int col = 0; col < EmoteCount; col++)
                    _emotes[col] = Wrap(ExtractScaledTile(
                        sheet,
                        col * EmoteTileSize,
                        0,
                        EmoteTileSize,
                        EmotePixels));
            }
        }

        private DisplayBitmap Wrap(Bitmap bitmap)
        {
            _ownedBitmaps.Add(bitmap);
            var db = new DisplayBitmap(bitmap);
            _ownedDisplayBitmaps.Add(db);
            return db;
        }

        /// <summary>
        /// Cuts one tile out of a sheet and scales it in a single nearest-neighbor
        /// draw. Out-of-range source rectangles (undersized sheet) yield the
        /// transparent parts of a fresh bitmap, which is safe.
        /// </summary>
        private Bitmap ExtractScaledTile(Bitmap sheet, int srcX, int srcY, int sourceTile, int outputTile)
        {
            var result = new Bitmap(outputTile, outputTile, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(
                    sheet,
                    new Rectangle(0, 0, outputTile, outputTile),
                    new Rectangle(srcX, srcY, sourceTile, sourceTile),
                    GraphicsUnit.Pixel);
            }
            return result;
        }

        /// <summary>
        /// First idle frame of a built-in pet at native 32 px, for UI icons.
        /// Caller owns the bitmap. Null when the asset is missing.
        /// </summary>
        internal static Bitmap LoadBuiltInTile(PetKind pet)
        {
            using Bitmap sheet = LoadEmbeddedBitmap(SheetNames[Math.Clamp((int)pet, 0, SheetNames.Length - 1)]);
            if (sheet == null || sheet.Width < TileSize || sheet.Height < TileSize)
                return null;
            return sheet.Clone(new Rectangle(0, 0, TileSize, TileSize), PixelFormat.Format32bppArgb);
        }

        /// <summary>File-backed sheet for custom pets; null on any failure.</summary>
        private static Bitmap LoadFileBitmap(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;
                // Copy so the file handle is not kept open for the atlas lifetime.
                using var fromFile = new Bitmap(path);
                int tile = DetectTileSize(fromFile);
                if (tile == 0)
                    return null; // malformed sheet: placeholder is safer than misaligned frames
                return new Bitmap(fromFile);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int DetectTileSize(Bitmap sheet)
        {
            if (sheet == null)
                return 0;

            for (int scale = 1; scale <= 4; scale *= 2)
            {
                int tile = TileSize * scale;
                if (sheet.Width == SheetColumns * tile &&
                    sheet.Height == AnimationCount * tile)
                {
                    return tile;
                }
            }
            return 0;
        }

        private static Bitmap LoadEmbeddedBitmap(string fileName)
        {
            try
            {
                Assembly asm = typeof(SpriteAtlas).Assembly;
                string match = null;
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = name;
                        break;
                    }
                }
                if (match == null)
                    return null;

                using Stream stream = asm.GetManifestResourceStream(match);
                if (stream == null)
                    return null;
                // Copy the decoded image so the bitmap does not depend on the
                // resource stream staying open.
                using var fromStream = new Bitmap(stream);
                return new Bitmap(fromStream);
            }
            catch (Exception)
            {
                return null; // Corrupt asset: fall through to the placeholder.
            }
        }

        private static Bitmap MakePlaceholderSheet(int width, int height, int tile)
        {
            var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(sheet);
            using var fill = new SolidBrush(Color.Magenta);
            using var border = new Pen(Color.FromArgb(140, 0, 110));
            for (int y = 0; y < height; y += tile)
            {
                for (int x = 0; x < width; x += tile)
                {
                    g.FillRectangle(fill, x + 1, y + 1, tile - 2, tile - 2);
                    g.DrawRectangle(border, x + 1, y + 1, tile - 3, tile - 3);
                }
            }
            return sheet;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var db in _ownedDisplayBitmaps)
                db.Dispose();
            _ownedDisplayBitmaps.Clear();
            foreach (var bmp in _ownedBitmaps)
                bmp.Dispose();
            _ownedBitmaps.Clear();
        }
    }
}
