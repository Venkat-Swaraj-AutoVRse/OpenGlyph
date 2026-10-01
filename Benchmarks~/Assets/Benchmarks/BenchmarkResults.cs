// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpenGlyph.Benchmarks
{
    /// <summary>One measured phase: median and p95 in milliseconds plus raw samples.</summary>
    [Serializable]
    public struct PhaseStat
    {
        public double medianMs;
        public double p95Ms;
        public double minMs;
        public double maxMs;
        public double[] samplesMs;

        public static PhaseStat From(List<double> samples)
        {
            var copy = samples.ToArray();
            Array.Sort(copy);
            return new PhaseStat
            {
                samplesMs = copy,
                medianMs = Percentile(copy, 50),
                p95Ms = Percentile(copy, 95),
                minMs = copy.Length > 0 ? copy[0] : 0,
                maxMs = copy.Length > 0 ? copy[copy.Length - 1] : 0
            };
        }

        // copy MUST already be sorted ascending.
        public static double Percentile(double[] sorted, double p)
        {
            if (sorted == null || sorted.Length == 0) return 0;
            if (sorted.Length == 1) return sorted[0];
            double rank = (p / 100.0) * (sorted.Length - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            if (lo == hi) return sorted[lo];
            double frac = rank - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }
    }

    [Serializable]
    public class SystemTextResult
    {
        public string system;        // OpenGlyph-ParallelOff / OpenGlyph-ParallelOn / TMP / UIToolkit
        public string textSet;       // Latin / Arabic / Hebrew / Mixed
        public int objectCount;
        public int iterations;
        public int warmups;

        public PhaseStat objectCreation;  // instantiate 100 objects until first mesh ready
        public PhaseStat fullRebuild;     // change text on all 100 + force regenerate
        public PhaseStat layout;          // re-layout only (width change)
        public PhaseStat meshRebuild;     // color/size change, mesh regen only

        public int gcCollectionsDuringCreation;   // GC.CollectionCount(0) delta
        public double allocatedMBDuringCreation;   // bytes delta / 1MB
        public double kbPerFullRebuildOp;          // alloc per op, KB
        public bool shapingFairForThisSystem;      // false for TMP on Arabic/Hebrew/Mixed
        public string note;
    }

    [Serializable]
    public class GlyphRasterResult
    {
        public string engine;        // FreeType (OpenGlyph) / UnityFontEngine (TMP dynamic)
        public string font;
        public int glyphSize;
        public int glyphCount;
        public double totalMs;
        public double msPerGlyph;
        public string note;
    }

    [Serializable]
    public class BuildSizeResult
    {
        public string system;
        public string font;
        public long fontBytes;
        public double fontMB;
        public string note;
    }

    [Serializable]
    public class EnvironmentInfo
    {
        public string unityVersion;
        public string scriptingBackend;   // Mono / IL2CPP
        public string buildType;           // Editor / StandaloneWindows64-IL2CPP-Release / Android-IL2CPP-Release
        public string os;
        public string cpu;
        public int cpuCount;
        public int systemMemoryMB;
        public string graphicsDevice;
        public string timestampUtc;
        public bool representative;        // false for editor runs
    }

    [Serializable]
    public class BenchmarkReport
    {
        public EnvironmentInfo environment = new EnvironmentInfo();
        public int objectCount = 100;
        public int iterations = 10;
        public int warmups = 3;
        public List<SystemTextResult> perSystemText = new List<SystemTextResult>();
        public List<GlyphRasterResult> glyphRaster = new List<GlyphRasterResult>();
        public List<BuildSizeResult> buildSize = new List<BuildSizeResult>();

        public string ToJson() => JsonUtility.ToJson(this, true);
    }
}
