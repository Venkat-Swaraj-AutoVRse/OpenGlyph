# Quest 3S VR benchmark - main 2c71ef6 (PR #30, unified renderer default)

Device: Meta Quest 3S; OpenXR single-pass instanced, Vulkan, IL2CPP ARM64. Workload: 100 objects x 2,405 chars (Latin+Arabic+Hebrew+mixed), 10 iterations after 3 warm-ups; value = median of the 3 per-run medians. Raw: `ua_91a39f0_vr_run{1,2,3}.json` (91a39f0 is the PR head, identical package content to main 2c71ef6). UI Toolkit validity is not verifiable by the harness (*).

| Metric (ms) | OpenGlyph parallel | OpenGlyph 1-thread | TMP | UI Toolkit* | x faster than TMP (parallel) |
|---|--:|--:|--:|--:|--:|
| Object creation | 168.2 | 307.4 | 610.0 | 952.1 | 3.63x |
| Full rebuild | 142.9 | 247.6 | 447.8 | 893.7 | 3.13x |
| Layout (wrap + auto-size) | 139.5 | 147.9 | 1289.3 | 270.6 | 9.24x |
| Layout (wrap) | 79.2 | 85.1 | 518.5 | 165.9 | 6.55x |
| Mesh rebuild (colour) | 111.6 | 118.7 | 534.8 | 164.0 | 4.79x |
| Destruction | 31.9 | 31.9 | 29.5 | 2.1 | 0.93x |

| Creation memory | OpenGlyph parallel | OpenGlyph 1-thread | TMP | UI Toolkit* |
|---|--:|--:|--:|--:|
| GC collections during creation | 1 | 2 | 30 | 0 |
| Allocated during creation (MB) | 427 | 427 | 552 | 67 |
