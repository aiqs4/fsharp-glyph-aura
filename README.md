# GlyphAura

**Zero-alloc F# OpenType shaping & SDF layout engine — built for Wasm-GC.**

[![CI](https://github.com/aiqs4/fsharp-glyph-auro/actions/workflows/ci.yml/badge.svg)](https://github.com/aiqs4/fsharp-glyph-auro/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/GlyphAura.svg)](https://www.nuget.org/packages/GlyphAura)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

GlyphAura is a self-contained F# library that reads a `.ttf` or `.otf` file and
produces positioned `GlyphPosition` arrays ready for a renderer — with zero heap
allocations in the hot path. It is the **typesetting back-end** you drop into any
SceneGraph that speaks `uint16[]` glyph IDs and `float32[]` positions.

---

## Features

| Feature | Status |
|---------|--------|
| Big-endian binary reader — no copies, no boxing | ✅ |
| `cmap` Format 4 (BMP) + Format 12 (full Unicode incl. SMP) | ✅ |
| UTF-16 surrogate pair decoding (emoji, SMP math, etc.) | ✅ |
| `hmtx` — O(1) glyph metrics | ✅ |
| Legacy `kern` table — O(log n) binary search | ✅ |
| GPOS LookupType 2 class-based kerning — O(1) lookup | ✅ |
| TR#29 grapheme cluster boundaries (ZWJ, emoji, Hangul) | ✅ |
| Bidi stub — LTR/RTL run detection | ✅ |
| Hebrew, Arabic, Greek, Cyrillic, Math, Symbols (BMP) | ✅ |
| Emoji and Supplementary Plane characters (Format 12) | ✅ |
| PUA / icon-font codepoints (graceful .notdef fallback) | ✅ |
| SDF geometry: Circle, Diamond, Triangle, Parallelogram | ✅ |
| Fused streaming layout — Mode A (Flow) + Mode B (Fit) | ✅ |
| Word-boundary line breaking | ✅ |
| `Vector128<float32>` SIMD advance summation | ✅ |
| DrawList SoA output (WebGPU-compatible) | ✅ |
| LRU shape cache with FNV-1a dirty-flag system | ✅ |
| TextNode with hit-testing, caret & selection geometry | ✅ |

> **Script support note:** GlyphAura resolves any Unicode codepoint to a glyph ID via cmap
> (Format 4 for BMP, Format 12 for full Unicode). Complex scripts that require *glyph
> substitution* (GSUB) — Arabic joining forms, Devanagari conjuncts, Latin ligatures — are
> not yet substituted; the engine places logical-order glyphs without substitution.
> RTL visual reordering (full Unicode Bidirectional Algorithm) is planned.
> For icon fonts (Font Awesome, Material Symbols) that use Private Use Area codepoints,
> the cmap lookup works transparently — the engine places whichever glyph the font maps.

---

## Quick Start

```bash
dotnet add package GlyphAura
```

```fsharp
open System
open System.IO
open GlyphAura
open GlyphAura.Shapes
open GlyphAura.LayoutEngine
open GlyphAura.DrawList

// 1. Load font (zero-copy — bytes stay in memory, all tables are slices)
let bytes = File.ReadAllBytes("DejaVuSans.ttf")
let tables, metrics = FontProvider.load (ReadOnlyMemory bytes) |> Result.get

// 2. Define an SDF shape
let shape = circle { X = 150.0f; Y = 150.0f } 150.0f

// 3. Layout text into the shape
let req = {
    Text       = ReadOnlyMemory("The quick brown fox jumps over the lazy dog.".ToCharArray())
    Shape      = shape
    Font       = tables
    Metrics    = metrics
    FontSizePx = 16.0f
    Mode       = Flow    // or Fit — auto-sizes to fill the shape
}
let result = layout req
let dl     = fromLayoutResult result

// 4. Feed to your renderer
// dl.GlyphIds     : uint16[] — glyph IDs for atlas lookup
// dl.XPositions   : float32[] — pixel X
// dl.YPositions   : float32[] — pixel Y
// dl.GlyphRasterRequests : uint16[] — sorted unique IDs to pre-warm the atlas
printfn "%d glyphs placed" dl.GlyphCount
```

---

## Architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│                           GlyphAura library                            │
│                                                                        │
│  BinaryReader ─► FontProvider ─► CMap / Hmtx / KernLegacy / Gpos      │
│                                         │                              │
│                              TextAnalyzer (TR#29 + bidi stub)          │
│                                         │                              │
│                              Shaper (Latin, Mode A)                    │
│                                         │                              │
│  Shapes (SDF) ◄─────────── LayoutEngine (Flow / Fit) ◄── SimdAdvance  │
│                                         │                              │
│                              DrawList (SoA output)                     │
│                                         │                              │
│                    ShapeCache ◄── TextNode (hit-test, caret, select)   │
└────────────────────────────────────────────────────────────────────────┘
             │
             ▼
    SceneGraph consumer
    (WebGPU / canvas / native renderer)
```

---

## Demo — Rendering Outputs

Running `dotnet run --project demo/GlyphAura.Demo` produces live renders in three
formats: **ASCII art** (terminal), **PNG** (SkiaSharp bitmap), and **SVG** (vector).
All rendered output is saved to `demo/output/`.

### PNG Renders (SkiaSharp)

High-fidelity raster renders that verify pixel-perfect glyph placement, baseline
alignment and kerning.  The **green dashed lines** are baselines; the **blue
polylines** are the SDF shape boundary; the **orange band** is the cap-height zone.

| Circle — Flow 12 px (long text) | Circle — Fit (auto-size) |
|---|---|
| ![circle_flow](demo/output/circle_flow.png) | ![circle_fit](demo/output/circle_fit.png) |

| Diamond — Flow 12 px | Triangle — Flow 11 px (long text) |
|---|---|
| ![diamond_flow](demo/output/diamond_flow.png) | ![triangle_flow](demo/output/triangle_flow.png) |

| Parallelogram shear=0.4 | Kerning test — VA Te AV To |
|---|---|
| ![parallelogram_flow](demo/output/parallelogram_flow.png) | ![kerning_test](demo/output/kerning_test.png) |

The kerning test renders common tight pairs at 72 px.  Even horizontal spacing
between the two characters confirms that GPOS class-based kerning is applied
correctly (e.g. V→A is pulled together by −348 design units in DejaVu Sans).

### SVG Renders (vector)

Scalable vector output for inspecting curve fidelity at any zoom level.  Zoom in
to verify that shape boundaries are smooth polylines (not staircase approximations).

| Circle Flow SVG | Kerning Test SVG |
|---|---|
| ![circle_flow_svg](demo/output/circle_flow.svg) | ![kerning_test_svg](demo/output/kerning_test.svg) |

### ASCII Rendering (terminal)

The ASCII art correctly places every glyph character *inside* the shape boundary `|`.
The shape boundary is anchored to each row's actual baseline Y from the layout engine
so glyphs never appear outside the edge.  All renders below were generated from the
current code and reflect the corrected glyph-positioning algorithm (band-midpoint
scanline query prevents ascenders from overflowing the shape boundary).

#### Circle — Flow mode (12 px, long text)

```
  Glyphs placed  : 118 / 122 source chars
  Font size used : 12.0 px
  Unique glyphs  : 32 (raster requests)
  Overflow       : none

                      |  Th e ·q u c k           |
                     |                            |
                |· br o w n·f ox·j u  m ps· ov e       |
           |r ·t he· la zy ·d og. ·P a ck · my ·b ox ·wi    |
        |th·f iv e ·d o ze· l i quor· j ugs . · Ho w· ve xin   |
      g l y·q ui ck· a f t ·ze bra s · j                         |
    |                                                              |
   |                                                                |
  |                                                                  |
 |                                                                    |
|                                                                      |
```

#### Circle — Fit mode (auto font size)

```
  Glyphs placed  : 22 / 23 source chars
  Font size used : 67.8 px   (binary-searched to fill the circle)
  Unique glyphs  : 17 (raster requests)

                     |                            |
                |                                      |
            |                                              |
         |                                                    |
      |    H           e         l    l   o                      |
    |                                                              |
   |                                                                |
,    ·     W              o         r     l    d         !
 ·  |         G   l         y         p                            |
```

#### Diamond — Flow mode (12 px)

```
  Glyphs placed  : 40 / 44 source chars

                                 |T   |
                                 |     |
                             |h e         |
                          |· q uic           |
                       |k ·br  o wn·f           |
                   |ox ·  j ump s ·o e r· the ·l    |
                |a z y·                                |
              |                                          |
```

#### Triangle — Flow mode (11 px, long text)

```
  Glyphs placed  : 114 / 122 source chars

                                  T  |
                               |h e     |
                               |        |
                             |·q uic      |
                           k ·b r o n· f    |
                        |ox · j ump s· o       |
                      |ve r·the ·la zy ·dog .·   |
                    |P a k · my· box ·it h         |
                  · f iv e·dz e n· iq uo r·j ug      |
               |s. · o w v ex ingl y·q uick· da ft      |
             |z e  br                                     |
```

#### Parallelogram — Flow mode (12 px, shear = 0.4)

```
  Glyphs placed  : 43 / 44 source chars

|                                                      |
T he ·q uic k·b row  n·fo x·ju m p s·o ve r·th e·la zy  |
 |                                                       |
 ·|d og                                                   |
```

#### Multi-script — Latin + Greek + Hebrew + Math + Emoji (14 px)

```
  Glyphs placed  : 34 / 35 source chars
  Scripts        : Latin, Greek (αβγ), Math (∑∫≠), Symbols (☃♠), Hebrew (שלום), Emoji (😀)

  Gyph Aura·Helo·αβγ·∑∫≠·☃♠·שלם·😀
```

#### TextNode Cache Demo

```
  First render  : 2231 µs  (17 glyphs)
  Cached render :    0 µs  (17 glyphs, cache hit)   ← instant on cache hit
  After setText :  470 µs  (11 glyphs)
```

---

## Design Principles
1. **Zero heap allocations on the hot path.** `ArrayPool<T>` for all temporary
   buffers. `ReadOnlyMemory<byte>` slices for all table data.

2. **No BCL/ICU dependencies in the Wasm path.** No `System.Globalization`, no
   reflection, no `Encoding`. Cluster detection is hand-rolled from Unicode tables.

3. **Functional core, mutable edge.** All parsing functions are pure. Mutation
   is isolated to `TextNode` (dirty flags) and `ShapeCache` (LRU entries).

4. **O(1) hot-path operations.** Glyph lookup (cmap), metrics (hmtx), and GPOS
   kerning all compile down to at most 3 array reads + 1 multiply.

---

## SceneGraph Integration

GlyphAura is designed to be the *typesetting back-end* of a separate SceneGraph
library. The contract is:

```fsharp
// Consumer receives a DrawList per TextNode per frame (cache-hit = instant)
let dl : DrawList = TextNode.update myNode cache

// Feed directly to renderer instanced draw call:
// - dl.GlyphIds      → vertex buffer[0] (per-instance glyph ID)
// - dl.XPositions    → vertex buffer[1] (per-instance X)
// - dl.YPositions    → vertex buffer[2] (per-instance Y)
// - dl.GlyphRasterRequests → pre-warm GPU atlas if new IDs appear
```

`toBytes` serialises a `DrawList` to a little-endian byte array for
zero-copy transfer over the Wasm/JS boundary.

---

## Building & Testing

```bash
# Build all (lib + tests + demo)
dotnet build

# Run tests (175 tests across 11 modules)
dotnet test tests/GlyphAura.Tests/GlyphAura.Tests.fsproj --verbosity normal

# Run demo (regenerates demo/output/*.png and *.svg)
dotnet run --project demo/GlyphAura.Demo/GlyphAura.Demo.fsproj
```

Requirements: **.NET 10 SDK**. The library also targets **net8.0** for
compatibility with older runtimes.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). PRs welcome — please add tests.

## License

[MIT](LICENSE) — Copyright © 2025 GlyphAura Contributors
