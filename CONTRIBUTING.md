CONTRIBUTING TO GLYPH-AURO
==========================

Thank you for your interest in contributing to Glyph-Auro! This document covers
how to report issues, propose changes, and get your pull request merged.

## Code of Conduct

Be respectful. We follow the [Contributor Covenant](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A `.ttf`/`.otf` font file for testing (DejaVu Sans is used in the test suite)

### Build & Test

```bash
git clone https://github.com/aiqs4/fsharp-glyph-auro.git
cd fsharp-glyph-auro

dotnet build
dotnet test tests/GlyphAura.Tests/GlyphAura.Tests.fsproj --verbosity normal
```

Run the demo:

```bash
dotnet run --project demo/GlyphAura.Demo/GlyphAura.Demo.fsproj
```

## Project Structure

```
src/GlyphAura/          — Library (GlyphAura.dll / NuGet package)
  BinaryReader.fs       — Zero-alloc big-endian reads
  Types.fs              — Core value types (ShapedGlyph, FontMetrics, …)
  FontProvider.fs       — OpenType table directory parser
  CMap.fs               — Unicode → GlyphID (Format 4 + 12)
  Hmtx.fs               — Horizontal metrics
  KernLegacy.fs         — kern table (Format 0)
  GposKerning.fs        — GPOS LookupType 2, class-based kerning
  TextAnalyzer.fs       — TR#29 grapheme clusters, bidi stub
  Shaper.fs             — Latin shaper (Mode A)
  Shapes.fs             — SDF geometry (Circle, Diamond, Triangle, Parallelogram)
  SimdAdvance.fs        — SIMD glyph-width summation
  LayoutEngine.fs       — Fused streaming layout (Flow / Fit modes)
  DrawList.fs           — SoA output format for renderers
  ShapeCache.fs         — LRU layout cache
  TextNode.fs           — Full interactive SceneGraph entity

tests/GlyphAura.Tests/  — xUnit test suite (121 tests)
demo/GlyphAura.Demo/    — Console ASCII-art rendering demo
```

## Coding Conventions

- **Zero allocations in hot paths.** Use `ReadOnlySpan<byte>`, `ArrayPool<T>`, and value types.
- **No .NET BCL dependencies in the critical path.** Functions that may be called from Wasm must not depend on `System.Globalization`, ICU, or reflection.
- **All public functions return `Result<_, string>` or `voption`.** No exceptions in public API.
- **Prefer struct discriminated unions** (`[<Struct>]`) for high-frequency types.
- **Tests before merge.** Every new feature needs at least one test.
- Follow the existing module compilation order in `GlyphAura.fsproj`.

## Pull Request Process

1. **Fork** the repository and create a feature branch off `main`.
2. Write tests for any new functionality.
3. Run `dotnet test` — all 121 (or more) tests must pass.
4. Open a PR against `main`. Fill in the PR template.
5. At least one approval is required before merge.

## Reporting Issues

Use [GitHub Issues](https://github.com/aiqs4/fsharp-glyph-auro/issues). Please include:

- Operating system and .NET version (`dotnet --version`)
- A minimal reproducer font + code snippet
- Expected vs. actual output

## Security

Do **not** open a public GitHub Issue for security vulnerabilities. Email
`security@glyph-auro.dev` instead (or use GitHub's private advisory reporting).

## License

By contributing you agree that your contributions will be licensed under the
[MIT License](LICENSE).
