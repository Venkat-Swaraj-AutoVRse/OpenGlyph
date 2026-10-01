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

        public int gcCollectionsDuringCreation;   // GC.CollectionCount(0) delta (legacy)
        public double allocatedMBDuringCreation;   // ProfilerRecorder GC Allocated In Frame, summed (MB)
        public double kbPerFullRebuildOp;          // alloc per op, KB (ProfilerRecorder)
        public bool shapingFairForThisSystem;      // false for TMP on Arabic/Hebrew/Mixed
        public string note;

        // ---- VALIDITY GATE ----
        // A phase is VALID only if every object actually produced output in the
        // timed window. If not, the timing is reported but flagged INVALID.
        public bool creationValid;
        public bool fullRebuildValid;
        public bool layoutValid;
        public bool meshRebuildValid;
        public int expectedChars;        // per-object expected character count
        public int observedCharsMin;     // min observed across objects (0 => something empty)
        public long observedVerticesMin; // min mesh/vertex/glyph count across objects
        public string validityNote;      // why invalid, if so

        // ---- allocation detail (ProfilerRecorder, whole-phase) ----
        public double gcAllocCreationKB;     // GC Allocated In Frame summed over creation
        public double gcReservedCreationKB;  // GC Reserved Memory (end-of-phase)
        public int gcGen0Delta, gcGen1Delta, gcGen2Delta;
        public double totalMemoryDeltaKB;    // GC.GetTotalMemory delta
    }

    // OpenGlyph-only: where the time goes (Stopwatch splits around pipeline stages),
    // averaged per object over the creation phase, so the rendering team knows what to fix.
    [Serializable]
    public class OpenGlyphProfileSplit
    {
        public string textSet;
        public bool parallel;
        public double shapeMsPerObj;    // HarfBuzz shaping (TextProcessor)
        public double layoutMsPerObj;   // line breaking + positioning
        public double rasterMsPerObj;   // FreeType/SDF glyph add to atlas
        public double meshMsPerObj;     // mesh generation
        public double totalMsPerObj;
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
        public bool incrementalGCEnabled;  // Application.incrementalGCTimeSlice-ish; recorded
        public int randomSeed;             // fixed seed for reproducibility
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
        public List<OpenGlyphProfileSplit> openGlyphSplits = new List<OpenGlyphProfileSplit>();

        public string ToJson() => JsonUtility.ToJson(this, true);
    }
}
