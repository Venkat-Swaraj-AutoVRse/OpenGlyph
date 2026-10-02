# LSE BenchmarkWorkshop — OpenGlyph vs TMP / UI Toolkit / UniText

**Status: BLOCKED — not completed.** The upstream measurement harness cannot be
compiled or run because its UniText runtime source is in a **private / deleted
repository** that is no longer publicly accessible. This document records exactly
what was found, the one deliverable that *was* recoverable (the upstream author's
own published numbers), and what would be needed to finish.

Date: 2026-10-02. Host: Windows (`PlatformTeamSRE`). Measurement harness kept
entirely under `D:\OpenGlyphWork\scratch\lse-bench` — nothing from the unlicensed
LSE repo was copied into this (OpenGlyph) repository.

---

## 1. Branch chosen, and why

Repo: `https://github.com/LightSideKittens/LightSideEcosystem` (no LICENSE — used
locally only, never copied or pushed).

Candidate branches all carry the benchmark scene
`Assets/UniText.Test/BenchmarkWorkshop/UniText_BenchmarkTest.unity`:
`2.0.0`, `2.0.0-dev`, `2.0.8`, `v2` (default `v3.x.x` does **not** have it).

**Chosen: `2.0.0`.** Reasoning:

- The task is to pick the branch whose bundled UniText is **closest to UniText
  1.0** (OpenGlyph's clean-room fork point). Branch labels map to UniText
  versions, so the earliest release line, `2.0.0`, is nominally closest; `2.0.0-dev`
  is its pre-release, `2.0.8` is eight patches later, and `v2` is a rolling head.
- **Caveat established from the source itself:** even `2.0.0` bundles UniText
  **2.0.0**, not 1.0. The bundled UniText is a git submodule; its public manifest
  (`Assets/UniText-Public/package.json`) reads `"version": "2.0.0"`,
  `"unity": "2021.3"`, and its `CHANGELOG.md` has a **single** header,
  `## [2.0.0] - Unreleased`, describing a large SDF/MSDF rewrite, a new font-family
  architecture, variable fonts, and SE-Asian word segmentation. There is **no 1.0
  entry** on this line — this UniText package starts at 2.0.0. So "closest to 1.0"
  can only be satisfied nominally; no branch here actually ships a 1.0 UniText.

Clone (branch-pinned, single-branch):
`git clone --branch 2.0.0 --single-branch https://github.com/LightSideKittens/LightSideEcosystem.git D:\OpenGlyphWork\scratch\lse-bench\repo` — succeeded.

**Project's required editor:** `ProjectSettings/ProjectVersion.txt` =
**`6000.3.11f1`**. Installed locally: `6000.3.19f1` (and `6000.2.0b12`). Per the
brief, the installed `6000.3.19f1` would have been used (same `6000.3` patch line);
reported here as the only deviation from the requested editor. The editor was
never launched because the project cannot compile (below).

---

## 2. What the scene measures (from the harness source, read not run)

The BenchmarkWorkshop scripts under
`Assets/UniText.Test/BenchmarkWorkshop/` are:

- `BenchmarkRunner.cs`, `TextBenchmarkBase.cs` — the driver + shared base.
- Per-system benchmarks: `UniTextBenchmark.cs`, `TMPBenchmark.cs`,
  `UIToolkitBenchmark.cs`, plus comparative/interactive/perf variants
  (`UniTextComparativeBenchmark.cs`, `UniTextInteractiveTest.cs`,
  `UniTextPerformanceTest.cs`) and glyph-rasterization benchmarks
  (`UniText_GlyphRasterizationBenchmark.cs`, `TMP_GlyphRasterizationBenchmark.cs`).
- `BenchmarkJsonSerializer.cs` — writes results to JSON.

The committed report `Assets/UniText.Test/BenchmarkReport.html` shows the harness
reports **four timed phases** per system plus allocation/GC cards. The timed work
toggles `UniText.UseParallel` and marks dirty via `UniText.SetDirty(DirtyFlags.All
| .Text | .Layout)` — i.e. object creation, a text/layout dirty rebuild, and a
mesh rebuild. The three explicitly titled non-time cards are **GC Cycles
(creation)**, **Managed Allocation (creation)**, and **Runtime Allocations (per
operation)**.

**Validation in the harness:** the glyph-rasterization benchmarks and the
comparative test exist, but whether the runner asserts glyph/vertex counts could
not be confirmed, because the types they call live in the missing submodule and
the assembly does not compile. This is why the brief's own Step 3 ("check every
system really renders before trusting any number") is mandatory — and here, none
of the systems could be rendered at all.

---

## 3. Why it could not be built — the blocker

`Assets/UniText` and `Assets/UniText-Public` are **git submodules**, not plain
folders. Their recorded URLs (from the clone's `.gitmodules`):

| Submodule | URL | Pinned commit | Fetchable? |
|---|---|---|---|
| `Assets/UniText` (the **runtime** source) | `github.com/LightSideMeowshop/UniText-Dev.git` | `d423c422` | **NO — `remote: Repository not found`** |
| `Assets/UniText-Public` (metadata only) | `github.com/LightSideMeowshop/unitext.git` | `5b767265` | yes, but ships no runtime code |

- `UniText-Dev` (the repo that contains the actual `UniText` MonoBehaviour,
  `UseParallel`, `DirtyFlags`, the SDF/MSDF pipeline, etc.) returns
  **`Repository not found`** — it is private or deleted, and no credentials for it
  exist or were used.
- `unitext` (public) fetched at its pinned commit but contains **only 9 files**:
  `CHANGELOG.md`, `LICENSE.md`, `package.json`, `README.md`, `Third-Party
  Notices.txt`, and `Documentation/GettingStarted.md`. **No `.cs` runtime.**

The Test assembly `UniText.Test.asmdef` references two asmdef GUIDs
(`6055be8e…`, `6572381e…`) that **resolve to no asmdef in the clone** — they are
defined inside the missing `UniText-Dev` submodule. Therefore the project **cannot
compile the UniText arm of the benchmark**, which blocks Steps 1–5 as specified
(open & run the scene; add OpenGlyph *alongside* UniText; build A=UniText vs
B=OpenGlyph; render-validate all four systems; Quest 3S and Windows IL2CPP runs).

This is branch-independent: all four candidate branches reference the same two
submodule repos, so the private one blocks every one of them.

---

## 4. The one recoverable deliverable: upstream's published numbers

The repo commits a pre-rendered `BenchmarkReport.html` containing the author's own
measured results (platform/editor unspecified in the file). Transcribed here (I
did **not** copy the file into this repo — these are my own transcription of the
displayed values):

**Timed phases (median ms, lower is better). Phase labels are inferred from the
report's bar order + the harness's dirty-flag usage; exact per-phase titles are
not plain-text in the HTML.**

| Phase (report order) | UniText | TMP | UI Toolkit |
|---|---:|---:|---:|
| Phase 1 (object creation) | 217 ms | 542 ms | 725 ms |
| Phase 2 (text/layout rebuild) | 75 ms | 275 ms | 333 ms |
| Phase 3 (layout) | 58 ms | 666 ms | 142 ms |
| Phase 4 (mesh rebuild) | 33 ms | 167 ms | 50 ms |

**Allocation / GC cards (as titled in the report):**

| Metric | UniText | TMP | UI Toolkit |
|---|---:|---:|---:|
| GC Cycles (creation) | 2 | 38 | 2 |
| Managed Allocation (creation) | ~500 B | ~6 KB | ~89 KB |
| Runtime Allocations (per operation) | (shown: 10 / 1712 / 3) | | |

> These are **upstream's own numbers for upstream UniText 2.0.0**, not for
> OpenGlyph, and not independently reproduced here. They are included only to
> satisfy the "comparison with the author's published numbers" item; with no
> runnable harness there is nothing of ours to compare them against yet.

---

## 5. Tables requested — per phase / system / platform

**Empty by necessity.** No measurement was produced on any platform (Quest 3S or
Windows IL2CPP), because the scene does not compile. Every cell below is
`BLOCKED`.

| Phase | System | Quest 3S (median) | Windows IL2CPP (median) | Validity |
|---|---|---|---|---|
| all | OpenGlyph | BLOCKED | BLOCKED | n/a — never built |
| all | upstream UniText | BLOCKED | BLOCKED | n/a — UniText runtime missing |
| all | TMP | BLOCKED | BLOCKED | n/a — project won't compile |
| all | UI Toolkit | BLOCKED | BLOCKED | n/a — project won't compile |

---

## 6. Evidence

- Clone + submodule logs: `D:\OpenGlyphWork\scratch\lse-bench\logs\clone.log`,
  `submodule.log`, `submodule_public.log` (show the `Repository not found` for
  `UniText-Dev`).
- No per-system frame PNGs under `…\evidence\` — nothing rendered to capture.

---

## 7. Open issues / what would unblock this

1. **The UniText runtime is inaccessible.** Finishing as specified requires the
   `LightSideMeowshop/UniText-Dev` submodule at `d423c422` (private/deleted).
   Options: (a) obtain access / a mirror of that commit from the project owner;
   (b) the owner provides a UniText 2.0.0 `.unitypackage` or UPM tarball;
   (c) re-scope the benchmark to **OpenGlyph vs TMP vs UI Toolkit only** (drop the
   upstream-UniText arm) using OpenGlyph's own benchmark harness
   (branch `openglyph/benchmarks` exists in this repo), which does not depend on
   the LSE submodules.
2. **Editor version** `6000.3.11f1` requested by the project vs `6000.3.19f1`
   installed — resolvable (same patch line) once there is something to open.
3. The upstream published numbers' **platform/editor are undocumented** in the
   committed report, so even a successful local run would not be strictly
   comparable without that context.

## Recommendation

Decide between (1a/1b) getting the private UniText runtime from the owner, or (1c)
re-scoping to a three-way benchmark driven by OpenGlyph's own harness. Option (1c)
is fully executable in this environment today; the four-way comparison with
upstream UniText is not, through no recoverable fault of the setup.
