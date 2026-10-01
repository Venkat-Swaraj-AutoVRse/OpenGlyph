using System;
using UnityEngine;

namespace LightSide
{
    /// <summary>
    /// Result of analysing a font for pixel-font characteristics (Phase 1c).
    /// </summary>
    /// <remarks>
    /// A "pixel font" is one whose outlines are drawn on a coarse regular grid so that at an
    /// integer multiple of its <see cref="NativePixelsPerEm"/> every edge lands exactly on a device
    /// pixel boundary. Alternatively a font may ship embedded bitmap strikes (EBDT/CBDT/sbix); those
    /// are inherently pixel-perfect at their fixed sizes. This struct carries both facts so the
    /// renderer can choose an integer sampling size and point-filter the atlas.
    /// </remarks>
    [Serializable]
    public struct PixelFontInfo : IEquatable<PixelFontInfo>
    {
        /// <summary>True when a regular outline grid was detected (see <see cref="NativePixelsPerEm"/>).</summary>
        public bool isPixelGrid;

        /// <summary>
        /// The font's native pixel size, expressed as pixels-per-em: at this ppem (or an integer
        /// multiple) outline edges land on integer device pixels. 0 when <see cref="isPixelGrid"/> is false.
        /// </summary>
        public int nativePixelsPerEm;

        /// <summary>
        /// Fraction (0..1) of sampled outline points that lay on the detected grid. Diagnostic; the
        /// detector accepts a grid at or above <see cref="PixelFontDetection.OnGridThreshold"/>.
        /// </summary>
        public float onGridFraction;

        /// <summary>True when the font contains embedded bitmap strikes (EBDT/CBDT/sbix).</summary>
        public bool hasBitmapStrikes;

        /// <summary>Available embedded bitmap strike sizes in pixels-per-em (empty when none).</summary>
        public int[] bitmapStrikeSizes;

        /// <summary>
        /// True when the analysis could actually read outline data. When false the outline grid was
        /// NOT probed (e.g. the native outline export is unavailable and no managed fallback was
        /// supplied); <see cref="isPixelGrid"/> is then false by absence of evidence, not by proof.
        /// </summary>
        public bool outlineDataAvailable;

        /// <summary>A font is treated as pixel-perfect-capable if it has an outline grid OR bitmap strikes.</summary>
        public bool IsPixelFont => isPixelGrid || hasBitmapStrikes;

        public bool Equals(PixelFontInfo other) =>
            isPixelGrid == other.isPixelGrid &&
            nativePixelsPerEm == other.nativePixelsPerEm &&
            hasBitmapStrikes == other.hasBitmapStrikes &&
            outlineDataAvailable == other.outlineDataAvailable;

        public override bool Equals(object obj) => obj is PixelFontInfo o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(isPixelGrid, nativePixelsPerEm, hasBitmapStrikes, outlineDataAvailable);

        public override string ToString() =>
            $"PixelFontInfo(grid={isPixelGrid} ppem={nativePixelsPerEm} onGrid={onGridFraction:0.###} " +
            $"strikes={hasBitmapStrikes} outlineData={outlineDataAvailable})";
    }

    /// <summary>
    /// Provides an outline in integer design-unit coordinates for grid analysis, decoupled from the
    /// native FreeType export so tests can feed a managed TrueType reader. Coordinates must be in the
    /// same units as <see cref="UnitsPerEm"/> (raw font units, i.e. LOAD_NO_SCALE space).
    /// </summary>
    public interface IPixelGridOutlineProvider
    {
        /// <summary>Font design units per em.</summary>
        int UnitsPerEm { get; }

        /// <summary>
        /// Fills <paramref name="coords"/> with alternating x,y integer design-unit coordinates for a
        /// number of representative glyphs. Returns the number of coordinate VALUES written (2 per
        /// point). Implementations should append across several glyphs to get a stable sample.
        /// </summary>
        /// <param name="coords">Growable buffer the provider appends design-unit coords into.</param>
        /// <param name="maxGlyphs">Soft cap on how many glyphs to sample.</param>
        /// <returns>True if any outline data was produced.</returns>
        bool TryCollectOutlineCoords(System.Collections.Generic.List<int> coords, int maxGlyphs);
    }

    /// <summary>
    /// Pixel-font detection (Phase 1c). Detects whether a font's outlines lie on a regular grid and,
    /// if so, the coarsest grid (its native pixels-per-em). Also reports embedded bitmap strikes.
    /// </summary>
    /// <remarks>
    /// <para><b>Grid detection.</b> For each candidate pixels-per-em N in
    /// [<see cref="MinPpem"/>, <see cref="MaxPpem"/>], the cell size is <c>unitsPerEm / N</c>. We
    /// measure the fraction of sampled outline coordinates that sit within
    /// <see cref="GridTolerance"/> of an integer multiple of that cell. The SMALLEST N whose fraction
    /// reaches <see cref="OnGridThreshold"/> is the native grid: harmonics (2N, 3N, …) also pass but
    /// describe the same lattice at finer resolution, so the coarsest is canonical.</para>
    /// <para>Pixel fonts commonly carry a few off-grid points (rounded terminals, hint anchors), so a
    /// strict GCD test fails; the fractional threshold tolerates that while a real vector font (e.g.
    /// NotoSans) never exceeds ~50% at any candidate and is rejected.</para>
    /// <para>The detector is renderer-agnostic and never mutates the font; existing render modes are
    /// unaffected. When no outline provider is available it reports
    /// <see cref="PixelFontInfo.outlineDataAvailable"/> = false and leaves grid detection off.</para>
    /// </remarks>
    public static class PixelFontDetection
    {
        /// <summary>Smallest candidate pixels-per-em considered.</summary>
        public const int MinPpem = 4;

        /// <summary>Largest candidate pixels-per-em considered. Bitmap pixel fonts rarely exceed this em.</summary>
        public const int MaxPpem = 64;

        /// <summary>A coordinate counts as on-grid when within this fraction of a cell of a lattice line.</summary>
        public const float GridTolerance = 0.02f;

        /// <summary>Minimum on-grid fraction for a candidate grid to be accepted.</summary>
        public const float OnGridThreshold = 0.90f;

        /// <summary>Minimum number of sampled coordinate values before grid detection is trusted.</summary>
        public const int MinSampleCoords = 64;

        /// <summary>
        /// Runs grid detection over an already-collected set of integer design-unit coordinates.
        /// Pure and allocation-light; unit-testable without any font machinery.
        /// </summary>
        /// <param name="coords">Alternating (or flat) integer design-unit coordinates.</param>
        /// <param name="count">Number of valid entries in <paramref name="coords"/>.</param>
        /// <param name="unitsPerEm">Font units per em (must be &gt; 0).</param>
        /// <param name="nativePpem">Out: detected native pixels-per-em, or 0.</param>
        /// <param name="onGridFraction">Out: on-grid fraction at the detected (or best) candidate.</param>
        /// <returns>True if a grid at or above the threshold was found.</returns>
        public static bool DetectGrid(int[] coords, int count, int unitsPerEm,
            out int nativePpem, out float onGridFraction)
        {
            nativePpem = 0;
            onGridFraction = 0f;
            if (coords == null || count < MinSampleCoords || unitsPerEm <= 0)
                return false;

            float bestFrac = 0f;
            for (int n = MinPpem; n <= MaxPpem; n++)
            {
                double cell = (double)unitsPerEm / n;
                if (cell <= 0) continue;

                int on = 0;
                for (int i = 0; i < count; i++)
                {
                    double q = coords[i] / cell;
                    double frac = q - Math.Round(q);
                    if (frac < 0) frac = -frac;
                    if (frac <= GridTolerance) on++;
                }

                float f = (float)on / count;
                if (f > bestFrac) bestFrac = f;

                if (f >= OnGridThreshold)
                {
                    // Smallest passing N is the canonical (coarsest) grid; harmonics also pass.
                    nativePpem = n;
                    onGridFraction = f;
                    return true;
                }
            }

            onGridFraction = bestFrac;
            return false;
        }

        /// <summary>
        /// Full analysis from an outline provider plus a precomputed bitmap-strike list.
        /// </summary>
        /// <param name="outlineProvider">Source of outline coords; may be null (grid probing skipped).</param>
        /// <param name="bitmapStrikeSizes">Embedded strike sizes in ppem (null/empty when none).</param>
        /// <param name="maxGlyphs">Soft cap on glyphs sampled for the grid probe.</param>
        public static PixelFontInfo Analyze(IPixelGridOutlineProvider outlineProvider,
            int[] bitmapStrikeSizes, int maxGlyphs = 64)
        {
            var info = new PixelFontInfo
            {
                hasBitmapStrikes = bitmapStrikeSizes != null && bitmapStrikeSizes.Length > 0,
                bitmapStrikeSizes = bitmapStrikeSizes ?? Array.Empty<int>(),
                outlineDataAvailable = false,
            };

            if (outlineProvider == null || outlineProvider.UnitsPerEm <= 0)
                return info;

            var list = _scratch ??= new System.Collections.Generic.List<int>(4096);
            list.Clear();
            if (!outlineProvider.TryCollectOutlineCoords(list, maxGlyphs) || list.Count < MinSampleCoords)
                return info;

            info.outlineDataAvailable = true;

            // Copy to array for the pure detector (List indexer is fine but array keeps it hot/simple).
            var arr = list.ToArray();
            if (DetectGrid(arr, arr.Length, outlineProvider.UnitsPerEm,
                    out int ppem, out float frac))
            {
                info.isPixelGrid = true;
                info.nativePixelsPerEm = ppem;
            }
            info.onGridFraction = frac;
            return info;
        }

        [ThreadStatic] private static System.Collections.Generic.List<int> _scratch;
    }
}
