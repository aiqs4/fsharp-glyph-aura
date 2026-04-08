# Changelog

All notable changes to Glyph-Auro are documented in this file.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

## [0.3.0] — 2025-04-08

### Added — Sprint 3: SceneGraph Output, Caching & Interaction

- **`DrawList`** – Struct-of-Arrays output format (`uint16[]` GlyphIds, `float32[]` X/Y positions).
  Includes deduplicated, sorted `GlyphRasterRequests` for atlas pre-warm. Binary serialization via
  `toBytes` for zero-copy Wasm/JS transfer.
- **`ShapeCache`** – LRU eviction with configurable capacity (default 32 entries). `CacheKey` is
  all value-typed (FNV-1a `uint64` hashes + `float32` font size). Thread-safe dirty-flag system.
- **`TextNode`** – Full interactive SceneGraph entity: dirty-flag system, cache-backed `update`,
  pixel-accurate hit-testing (`hitTest`), caret positioning (`caretPosition`), multi-line
  selection geometry (`selectionRects`). `setTransform` does not trigger re-layout.

## [0.2.0] — 2025-04-08

### Added — Sprint 2: Complex Shaping + SDF Geometry + Fused Layout

- **`GposKerning`** – GPOS `LookupType 2` Format 2 (class-based). Parses `ClassDef` Format 1/2
  into flat `int16[]` value matrix. O(1) lookup via 2 array reads + 1 multiply. GPOS takes
  precedence over legacy `kern` per OpenType spec.
- **`TextAnalyzer`** – TR#29 grapheme cluster boundaries (ZWJ sequences, combining marks,
  variation selectors, Regional Indicators, Hangul jamo). Bidi stub: pure LTR/RTL run
  identification from Unicode block ranges.
- **`Shapes`** – SDF geometry engine. `ISdfShape` interface with `GetDistance`, `GetInterval`
  (binary search, 16 iterations, sub-pixel precision), and `BoundingBox`. Shapes: Circle,
  Diamond (Raute), Triangle, Parallelogram.
- **`LayoutEngine`** – Fused streaming loop. Mode A (Flow): fixed font size, stops at shape
  boundary. Mode B (Fit): binary-searches for largest font size that fits all text (12 iterations,
  <0.1% error). Word-boundary line breaking. `ArrayPool`-rented buffers, zero heap allocations.
- **`SimdAdvance`** – `Vector128<float32>` SIMD summation with scalar fallback. Maps to Wasm
  `v128` SIMD instructions on .NET Wasm backends.

## [0.1.0] — 2025-04-08

### Added — Sprint 1: Binary Foundation & Simple Shaping

- **`BinaryReader`** – Inline big-endian `uint16`/`int16`/`uint32` reads on `ReadOnlySpan<byte>`.
  Zero allocations.
- **`FontProvider`** – Parses OpenType table directory; exposes each table as a
  `ReadOnlyMemory<byte>` slice (zero-copy). Populates `FontMetrics` from `hhea` + `maxp`.
  Returns `Error "…"` on malformed input.
- **`CMap`** – Unicode → GlyphID. Format 4 (BMP, O(log n) segment binary search) + Format 12
  (full Unicode via group binary search).
- **`Hmtx`** – Horizontal metrics. Flat `float32[]` for cache-local O(1) `getMetrics`.
- **`KernLegacy`** – `kern` table Format 0. Packed `uint32` keys, binary search, no `Dictionary`.
- **`Shaper`** – Simple Latin shaper (Mode A). Width-constrained `shapeLine` returns
  `ShapeResult` with `GlyphCount`, `TotalAdvance`, and `LastFitIndex`.

[Unreleased]: https://github.com/aiqs4/fsharp-glyph-auro/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/aiqs4/fsharp-glyph-auro/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/aiqs4/fsharp-glyph-auro/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/aiqs4/fsharp-glyph-auro/releases/tag/v0.1.0
