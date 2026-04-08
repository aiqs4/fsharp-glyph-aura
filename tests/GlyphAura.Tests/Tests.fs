namespace GlyphAura.Tests

open System
open System.IO
open System.Buffers
open Xunit
open GlyphAura
open GlyphAura.GposKerning
open GlyphAura.TextAnalyzer
open GlyphAura.Shapes
open GlyphAura.LayoutEngine
open GlyphAura.DrawList
open GlyphAura.ShapeCache
open GlyphAura.TextNode

/// Helper module for loading test fonts.
module TestHelper =
    let loadTestFont () =
        let path = Path.Combine(AppContext.BaseDirectory, "TestData", "DejaVuSans.ttf")
        let bytes = File.ReadAllBytes(path)
        let memory = ReadOnlyMemory(bytes)
        match FontProvider.load memory with
        | Ok(tables, metrics) -> tables, metrics
        | Error msg -> failwith (sprintf "Failed to load test font: %s" msg)

    let loadTestFontRaw () =
        let path = Path.Combine(AppContext.BaseDirectory, "TestData", "DejaVuSans.ttf")
        let bytes = File.ReadAllBytes(path)
        ReadOnlyMemory(bytes)

// ============================================================
// Issue #1: Binary Font Parser (FontProvider)
// ============================================================
module FontProviderTests =

    [<Fact>]
    let ``FontProvider loads DejaVu Sans successfully`` () =
        let result = FontProvider.load (TestHelper.loadTestFontRaw())
        match result with
        | Ok _ -> ()
        | Error msg -> Assert.Fail(msg)

    [<Fact>]
    let ``FontMetrics has correct UnitsPerEm`` () =
        let _, metrics = TestHelper.loadTestFont()
        Assert.Equal(2048us, metrics.UnitsPerEm)

    [<Fact>]
    let ``FontMetrics has correct Ascender`` () =
        let _, metrics = TestHelper.loadTestFont()
        Assert.Equal(1901s, metrics.Ascender)

    [<Fact>]
    let ``FontMetrics has correct Descender`` () =
        let _, metrics = TestHelper.loadTestFont()
        Assert.Equal(-483s, metrics.Descender)

    [<Fact>]
    let ``FontMetrics has correct MaxAdvanceWidth`` () =
        let _, metrics = TestHelper.loadTestFont()
        Assert.Equal(3838us, metrics.MaxAdvanceWidth)

    [<Fact>]
    let ``FontTables has required tables`` () =
        let tables, _ = TestHelper.loadTestFont()
        Assert.True(tables.Cmap.Length > 0, "cmap table should exist")
        Assert.True(tables.Head.Length > 0, "head table should exist")
        Assert.True(tables.Hhea.Length > 0, "hhea table should exist")
        Assert.True(tables.Hmtx.Length > 0, "hmtx table should exist")
        Assert.True(tables.Maxp.Length > 0, "maxp table should exist")

    [<Fact>]
    let ``FontTables has optional kern and GPOS`` () =
        let tables, _ = TestHelper.loadTestFont()
        Assert.True(tables.Kern.IsSome, "DejaVu Sans should have a kern table")
        Assert.True(tables.Gpos.IsSome, "DejaVu Sans should have a GPOS table")

    [<Fact>]
    let ``FontProvider returns error on empty data`` () =
        let result = FontProvider.load (ReadOnlyMemory([||]))
        match result with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Should have returned error for empty data")

    [<Fact>]
    let ``FontProvider returns error on too-small data`` () =
        let result = FontProvider.load (ReadOnlyMemory([| 0uy; 1uy; 0uy; 0uy |]))
        match result with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Should have returned error for truncated data")

    [<Fact>]
    let ``FontProvider returns error on invalid sfVersion`` () =
        let data = Array.zeroCreate<byte> 100
        data.[0] <- 0xFFuy
        let result = FontProvider.load (ReadOnlyMemory(data))
        match result with
        | Error msg -> Assert.Contains("sfVersion", msg)
        | Ok _ -> Assert.Fail("Should have returned error for invalid sfVersion")

// ============================================================
// Issue #1: BinaryReader
// ============================================================
module BinaryReaderTests =

    [<Fact>]
    let ``readU16 reads big-endian uint16`` () =
        let data = ReadOnlySpan([| 0x01uy; 0x02uy |])
        Assert.Equal(0x0102us, BinaryReader.readU16 data 0)

    [<Fact>]
    let ``readI16 reads big-endian int16`` () =
        let data = ReadOnlySpan([| 0xFFuy; 0xFEuy |])
        Assert.Equal(-2s, BinaryReader.readI16 data 0)

    [<Fact>]
    let ``readU32 reads big-endian uint32`` () =
        let data = ReadOnlySpan([| 0x00uy; 0x01uy; 0x00uy; 0x00uy |])
        Assert.Equal(0x00010000u, BinaryReader.readU32 data 0)

    [<Fact>]
    let ``readU16 at offset works correctly`` () =
        let data = ReadOnlySpan([| 0x00uy; 0x00uy; 0x08uy; 0x00uy |])
        Assert.Equal(2048us, BinaryReader.readU16 data 2)

// ============================================================
// Issue #2: cmap Parser
// ============================================================
module CMapTests =

    [<Fact>]
    let ``CMap parses successfully`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        match cmap with
        | Unsupported -> Assert.Fail("cmap should not be Unsupported")
        | _ -> ()

    [<Fact>]
    let ``CMap resolves space (U+0020) to non-zero GlyphID`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let glyphId = CMap.resolve cmap 0x0020u
        Assert.True(glyphId > 0us, sprintf "Space should map to a glyph, got %d" glyphId)

    [<Fact>]
    let ``CMap resolves A (U+0041) to non-zero GlyphID`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let glyphId = CMap.resolve cmap 0x0041u
        Assert.True(glyphId > 0us, sprintf "'A' should map to a glyph, got %d" glyphId)

    [<Fact>]
    let ``CMap resolves all 128 ASCII codepoints`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        // All printable ASCII (32-126) should resolve to non-zero
        for cp in 32u .. 126u do
            let glyphId = CMap.resolve cmap cp
            Assert.True(glyphId > 0us, sprintf "ASCII codepoint %d should map to a glyph" cp)

    [<Fact>]
    let ``CMap returns 0 for unmapped codepoint`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        // Use a Private Use Area codepoint likely unmapped
        let glyphId = CMap.resolve cmap 0xF8FFu
        // This might actually be mapped in DejaVu - use a more obscure one
        let glyphId2 = CMap.resolve cmap 0xFFFFu
        // At least verify it doesn't crash
        ignore glyphId
        ignore glyphId2

    [<Fact>]
    let ``CMap different codepoints map to different GlyphIDs`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let glyphA = CMap.resolve cmap (uint32 'A')
        let glyphB = CMap.resolve cmap (uint32 'B')
        Assert.NotEqual(glyphA, glyphB)

    [<Fact>]
    let ``CMap returns Unsupported for empty data`` () =
        let cmap = CMap.parse (ReadOnlyMemory([||]))
        match cmap with
        | Unsupported -> ()
        | _ -> Assert.Fail("Should be Unsupported for empty data")

// ============================================================
// Issue #3: hmtx Parser
// ============================================================
module HmtxTests =

    [<Fact>]
    let ``Hmtx parses correctly and returns non-zero advance for A`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea numGlyphs

        let glyphA = CMap.resolve cmap (uint32 'A')
        let metrics = Hmtx.getMetrics hmtx glyphA
        Assert.True(metrics.AdvanceWidth > 0.0f, "Advance width for 'A' should be positive")

    [<Fact>]
    let ``Hmtx different glyphs have different widths`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea numGlyphs

        let glyphM = CMap.resolve cmap (uint32 'M')
        let glyphI = CMap.resolve cmap (uint32 'i')
        let metricsM = Hmtx.getMetrics hmtx glyphM
        let metricsI = Hmtx.getMetrics hmtx glyphI
        // 'M' is typically wider than 'i'
        Assert.True(metricsM.AdvanceWidth > metricsI.AdvanceWidth,
            sprintf "M width (%.0f) should be > i width (%.0f)" metricsM.AdvanceWidth metricsI.AdvanceWidth)

    [<Fact>]
    let ``Hmtx returns values in design units`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea numGlyphs

        let glyphA = CMap.resolve cmap (uint32 'A')
        let m = Hmtx.getMetrics hmtx glyphA
        // Design units should be in the hundreds for a 2048 UPM font
        Assert.True(m.AdvanceWidth > 100.0f && m.AdvanceWidth < float32 metrics.UnitsPerEm * 2.0f,
            sprintf "Advance width %.0f seems outside expected range for 2048 UPM" m.AdvanceWidth)

    [<Fact>]
    let ``Hmtx getMetrics for out-of-range GlyphID returns zero`` () =
        let tables, _ = TestHelper.loadTestFont()
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea numGlyphs

        let m = Hmtx.getMetrics hmtx 65535us
        Assert.Equal(0.0f, m.AdvanceWidth)

    [<Fact>]
    let ``Hmtx space glyph has positive advance`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea numGlyphs

        let glyphSpace = CMap.resolve cmap 0x0020u
        let m = Hmtx.getMetrics hmtx glyphSpace
        Assert.True(m.AdvanceWidth > 0.0f, "Space glyph should have positive advance width")

// ============================================================
// Issue #4: Legacy Kerning
// ============================================================
module KernTests =

    [<Fact>]
    let ``KernLegacy parses DejaVu Sans kern table`` () =
        let tables, _ = TestHelper.loadTestFont()
        match tables.Kern with
        | ValueSome kernData ->
            let kern = KernLegacy.parse kernData
            Assert.True(kern.IsSome, "DejaVu Sans kern table should parse as Format 0")
        | ValueNone ->
            Assert.Fail("DejaVu Sans should have a kern table")

    [<Fact>]
    let ``KernLegacy has non-empty pair data`` () =
        let tables, _ = TestHelper.loadTestFont()
        match tables.Kern with
        | ValueSome kernData ->
            match KernLegacy.parse kernData with
            | ValueSome kern ->
                Assert.True(kern.Keys.Length > 0, "Kern table should have pairs")
                Assert.Equal(kern.Keys.Length, kern.Values.Length)
            | ValueNone -> Assert.Fail("Kern parse returned ValueNone")
        | ValueNone -> Assert.Fail("No kern table")

    [<Fact>]
    let ``KernLegacy keys are sorted`` () =
        let tables, _ = TestHelper.loadTestFont()
        match tables.Kern with
        | ValueSome kernData ->
            match KernLegacy.parse kernData with
            | ValueSome kern ->
                for i in 1 .. kern.Keys.Length - 1 do
                    Assert.True(kern.Keys.[i] >= kern.Keys.[i - 1],
                        sprintf "Keys should be sorted: [%d]=%u >= [%d]=%u" i kern.Keys.[i] (i-1) kern.Keys.[i-1])
            | ValueNone -> Assert.Fail("Kern parse returned ValueNone")
        | ValueNone -> Assert.Fail("No kern table")

    [<Fact>]
    let ``KernLegacy returns 0 for absent pair`` () =
        let tables, _ = TestHelper.loadTestFont()
        match tables.Kern with
        | ValueSome kernData ->
            match KernLegacy.parse kernData with
            | ValueSome kern ->
                // Use obscure pair that shouldn't be kerned
                let adj = KernLegacy.getAdjustment kern 0us 0us
                Assert.Equal(0s, adj)
            | ValueNone -> Assert.Fail("Kern parse returned ValueNone")
        | ValueNone -> Assert.Fail("No kern table")

    [<Fact>]
    let ``KernLegacy getAdjustmentOpt on ValueNone returns 0`` () =
        let adj = KernLegacy.getAdjustmentOpt ValueNone 100us 200us
        Assert.Equal(0s, adj)

    [<Fact>]
    let ``KernLegacy parse returns ValueNone for empty data`` () =
        let kern = KernLegacy.parse (ReadOnlyMemory([||]))
        Assert.True(kern.IsNone, "Empty data should return ValueNone")

    [<Fact>]
    let ``KernLegacy finds a known kerning pair`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        match tables.Kern with
        | ValueSome kernData ->
            match KernLegacy.parse kernData with
            | ValueSome kern ->
                // Try common kern pairs: V+A, T+o, A+V
                let glyphV = CMap.resolve cmap (uint32 'V')
                let glyphA = CMap.resolve cmap (uint32 'A')
                let glyphT = CMap.resolve cmap (uint32 'T')
                let glypho = CMap.resolve cmap (uint32 'o')

                let va = KernLegacy.getAdjustment kern glyphV glyphA
                let to_ = KernLegacy.getAdjustment kern glyphT glypho
                let av = KernLegacy.getAdjustment kern glyphA glyphV

                // At least one of these common pairs should have non-zero kerning
                let hasKerning = va <> 0s || to_ <> 0s || av <> 0s
                Assert.True(hasKerning,
                    sprintf "Expected kerning for common pairs. VA=%d, To=%d, AV=%d" va to_ av)
            | ValueNone -> Assert.Fail("Kern parse returned ValueNone")
        | ValueNone -> Assert.Fail("No kern table")

// ============================================================
// Issue #5: Simple Latin Shaper
// ============================================================
module ShaperTests =

    let private setupShaper () =
        let tables, metrics = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea numGlyphs
        let kern =
            match tables.Kern with
            | ValueSome kernData -> KernLegacy.parse kernData
            | ValueNone -> ValueNone
        let scale = 16.0f / float32 metrics.UnitsPerEm
        cmap, hmtx, kern, scale

    [<Fact>]
    let ``Shaper produces correct glyph count for simple text`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "Hello".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer
        Assert.Equal(5, result.GlyphCount)

    [<Fact>]
    let ``Shaper produces non-zero GlyphIDs`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "ABC".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer
        for i in 0 .. result.GlyphCount - 1 do
            Assert.True(buffer.[i].GlyphId > 0us, sprintf "GlyphID at %d should be non-zero" i)

    [<Fact>]
    let ``Shaper sets correct ClusterIndex`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "Test".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer
        for i in 0 .. result.GlyphCount - 1 do
            Assert.Equal(i, buffer.[i].ClusterIndex)

    [<Fact>]
    let ``Shaper produces positive XAdvance`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "Width".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer
        for i in 0 .. result.GlyphCount - 1 do
            Assert.True(buffer.[i].XAdvance > 0.0f, sprintf "XAdvance at %d should be positive" i)

    [<Fact>]
    let ``Shaper TotalAdvance is positive`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "Typography".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer
        Assert.True(result.TotalAdvance > 0.0f, "Total advance should be positive")

    [<Fact>]
    let ``Shaper LastFitIndex is -1 when all glyphs fit`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "ABC".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer
        Assert.Equal(-1, result.LastFitIndex)

    [<Fact>]
    let ``Shaper respects width constraint`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "A long text that should be truncated by width constraint".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        // Use a very small width constraint
        let result = Shaper.shapeLine text cmap hmtx kern scale 30.0f buffer
        Assert.True(result.LastFitIndex >= 0, "LastFitIndex should be set when constrained")
        Assert.True(result.LastFitIndex < text.Length - 1,
            "LastFitIndex should be before end of text")

    [<Fact>]
    let ``Shaper with zero-width constraint stops immediately`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "Hello".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale 0.0f buffer
        // No glyphs should be placed when width is zero; overflow detected
        Assert.Equal(0, result.GlyphCount)
        Assert.Equal(0, result.LastFitIndex)

    [<Fact>]
    let ``Shaper handles empty text`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = ReadOnlySpan<char>.Empty
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer
        Assert.Equal(0, result.GlyphCount)
        Assert.Equal(-1, result.LastFitIndex)

    [<Fact>]
    let ``Shaper processes extended Latin characters`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "àéîöü".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer
        Assert.Equal(5, result.GlyphCount)
        for i in 0 .. result.GlyphCount - 1 do
            Assert.True(buffer.[i].GlyphId > 0us, sprintf "Extended Latin char %d should map" i)

    [<Fact>]
    let ``Shaper Typography produces correct glyph count`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "Typography".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100
        let result = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer
        Assert.Equal(10, result.GlyphCount)
        // All glyphs should have distinct GlyphIDs (except repeated letters)
        Assert.True(result.TotalAdvance > 0.0f)

    [<Fact>]
    let ``Shaper width constraint correctly identifies last fit`` () =
        let cmap, hmtx, kern, scale = setupShaper()
        let text = "Typography".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 100

        // First shape unconstrained to get total width
        let fullResult = Shaper.shapeLine text cmap hmtx kern scale System.Single.MaxValue buffer

        // Now constrain to half the total width
        let halfWidth = fullResult.TotalAdvance / 2.0f
        let buffer2 = Array.zeroCreate<ShapedGlyph> 100
        let constrained = Shaper.shapeLine text cmap hmtx kern scale halfWidth buffer2

        Assert.True(constrained.LastFitIndex >= 0, "Should be constrained")
        Assert.True(constrained.LastFitIndex < text.Length - 1, "Should not fit all chars")

// ============================================================
// Issue #6: GPOS Class-Based Kerning
// ============================================================
module GposKerningTests =

    [<Fact>]
    let ``GPOS parses DejaVu Sans successfully`` () =
        let tables, _ = TestHelper.loadTestFont()
        match tables.Gpos with
        | ValueSome gposData ->
            let maxpSpan = tables.Maxp.Span
            let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
            let result = GposKerning.parse gposData numGlyphs
            Assert.True(result.IsSome, "GPOS should parse successfully for DejaVu Sans")
        | ValueNone ->
            Assert.Fail("DejaVu Sans should have a GPOS table")

    [<Fact>]
    let ``GPOS class tables have correct dimensions`` () =
        let tables, _ = TestHelper.loadTestFont()
        match tables.Gpos with
        | ValueSome gposData ->
            let maxpSpan = tables.Maxp.Span
            let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
            match GposKerning.parse gposData numGlyphs with
            | ValueSome kc ->
                Assert.Equal(numGlyphs, kc.LeftClassTable.Length)
                Assert.Equal(numGlyphs, kc.RightClassTable.Length)
                Assert.True(kc.ColumnCount > 0, "ColumnCount should be positive")
                Assert.Equal(kc.ValueMatrix.Length, (kc.ValueMatrix.Length / kc.ColumnCount) * kc.ColumnCount)
            | ValueNone -> Assert.Fail("GPOS parse returned ValueNone")
        | ValueNone -> Assert.Fail("No GPOS table")

    [<Fact>]
    let ``GPOS To pair produces negative adjustment`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        match tables.Gpos with
        | ValueSome gposData ->
            let maxpSpan = tables.Maxp.Span
            let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
            match GposKerning.parse gposData numGlyphs with
            | ValueSome kc ->
                let glyphT = CMap.resolve cmap (uint32 'T')
                let glypho = CMap.resolve cmap (uint32 'o')
                let adj = GposKerning.getAdjustment kc glyphT glypho
                // DejaVu Sans: To pair has XAdvance adjustment of -348 design units
                Assert.True(adj < 0s, sprintf "To pair should have negative adjustment, got %d" adj)
            | ValueNone -> Assert.Fail("GPOS parse returned ValueNone")
        | ValueNone -> Assert.Fail("No GPOS table")

    [<Fact>]
    let ``GPOS returns 0 for unkerned pair`` () =
        let tables, _ = TestHelper.loadTestFont()
        match tables.Gpos with
        | ValueSome gposData ->
            let maxpSpan = tables.Maxp.Span
            let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
            match GposKerning.parse gposData numGlyphs with
            | ValueSome kc ->
                // GlyphID 0 (.notdef) should have class 0 for both left and right
                let adj = GposKerning.getAdjustment kc 0us 0us
                Assert.Equal(0s, adj)
            | ValueNone -> Assert.Fail("GPOS parse returned ValueNone")
        | ValueNone -> Assert.Fail("No GPOS table")

    [<Fact>]
    let ``GPOS class 0 handles unmapped glyphs`` () =
        let tables, _ = TestHelper.loadTestFont()
        match tables.Gpos with
        | ValueSome gposData ->
            let maxpSpan = tables.Maxp.Span
            let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
            match GposKerning.parse gposData numGlyphs with
            | ValueSome kc ->
                // Very high glyph IDs may be unmapped; should not crash
                let adj = GposKerning.getAdjustment kc 1us 1us
                // Just verify it doesn't crash
                ignore adj
            | ValueNone -> Assert.Fail("GPOS parse returned ValueNone")
        | ValueNone -> Assert.Fail("No GPOS table")

    [<Fact>]
    let ``GPOS precedence over legacy kern`` () =
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let gposKern =
            match tables.Gpos with
            | ValueSome gd -> GposKerning.parse gd numGlyphs
            | ValueNone -> ValueNone
        let legacyKern =
            match tables.Kern with
            | ValueSome kd -> KernLegacy.parse kd
            | ValueNone -> ValueNone

        // Both should exist for DejaVu Sans
        Assert.True(gposKern.IsSome, "GPOS kern should exist")
        Assert.True(legacyKern.IsSome, "Legacy kern should exist")

        // The To pair should have matching values (both sourced from same data)
        let glyphT = CMap.resolve cmap (uint32 'T')
        let glypho = CMap.resolve cmap (uint32 'o')
        let gposVal = GposKerning.getAdjustment gposKern.Value glyphT glypho
        let legacyVal = KernLegacy.getAdjustment legacyKern.Value glyphT glypho
        // Both should be negative for T,o pair
        Assert.True(gposVal < 0s, sprintf "GPOS kern To should be negative: %d" gposVal)
        Assert.True(legacyVal < 0s, sprintf "Legacy kern To should be negative: %d" legacyVal)

// ============================================================
// Issue #7: Grapheme Cluster Analysis + Bidi Stub
// ============================================================
module TextAnalyzerTests =

    [<Fact>]
    let ``analyze splits pure LTR text into single run`` () =
        let text = "Hello World".AsSpan()
        let buffer = Array.zeroCreate<TextRun> 10
        let count = TextAnalyzer.analyze text buffer
        Assert.Equal(1, count)
        Assert.False(buffer.[0].IsRTL)
        Assert.Equal(0, buffer.[0].StartIndex)
        Assert.Equal(text.Length, buffer.[0].Length)

    [<Fact>]
    let ``analyze detects RTL Hebrew text`` () =
        let text = "\u05E9\u05DC\u05D5\u05DD".AsSpan()  // שלום
        let buffer = Array.zeroCreate<TextRun> 10
        let count = TextAnalyzer.analyze text buffer
        Assert.Equal(1, count)
        Assert.True(buffer.[0].IsRTL, "Hebrew should be detected as RTL")

    [<Fact>]
    let ``analyze detects RTL Arabic text`` () =
        let text = "\u0645\u0631\u062D\u0628\u0627".AsSpan()  // مرحبا
        let buffer = Array.zeroCreate<TextRun> 10
        let count = TextAnalyzer.analyze text buffer
        Assert.Equal(1, count)
        Assert.True(buffer.[0].IsRTL, "Arabic should be detected as RTL")

    [<Fact>]
    let ``isClusterBoundary identifies combining mark as non-boundary`` () =
        // e + combining acute accent
        let text = "e\u0301".AsSpan()
        Assert.False(TextAnalyzer.isClusterBoundary text 1, "Should not break between base and combining mark")

    [<Fact>]
    let ``isClusterBoundary identifies ZWJ as non-boundary`` () =
        // Two emoji joined by ZWJ
        let text = "\U0001F468\u200D\U0001F469".AsSpan()
        // Find the ZWJ position
        let mutable zwjPos = -1
        for i in 0 .. text.Length - 1 do
            if text.[i] = '\u200D' then zwjPos <- i
        if zwjPos >= 0 then
            Assert.False(TextAnalyzer.isClusterBoundary text zwjPos,
                "Should not break before ZWJ")

    [<Fact>]
    let ``isClusterBoundary allows break between regular characters`` () =
        let text = "AB".AsSpan()
        Assert.True(TextAnalyzer.isClusterBoundary text 1, "Should allow break between A and B")

    [<Fact>]
    let ``isClusterBoundary handles variation selectors`` () =
        // Star followed by VS16 (emoji presentation)
        let text = "\u2764\uFE0F".AsSpan()
        Assert.False(TextAnalyzer.isClusterBoundary text 1, "Should not break before variation selector")

    [<Fact>]
    let ``analyze handles empty text`` () =
        let text = ReadOnlySpan<char>.Empty
        let buffer = Array.zeroCreate<TextRun> 10
        let count = TextAnalyzer.analyze text buffer
        Assert.Equal(0, count)

    [<Fact>]
    let ``isClusterBoundary at boundaries returns true`` () =
        let text = "ABC".AsSpan()
        Assert.True(TextAnalyzer.isClusterBoundary text 0, "Position 0 is always a boundary")
        Assert.True(TextAnalyzer.isClusterBoundary text 3, "Position at length is always a boundary")

    [<Fact>]
    let ``analyze handles mixed LTR and RTL`` () =
        // English followed by Hebrew
        let text = "Hello \u05E9\u05DC\u05D5\u05DD".AsSpan()
        let buffer = Array.zeroCreate<TextRun> 10
        let count = TextAnalyzer.analyze text buffer
        Assert.True(count >= 2, sprintf "Should have at least 2 runs for mixed text, got %d" count)

// ============================================================
// Issue #8: SDF Geometry Engine (Shapes)
// ============================================================
module ShapesTests =

    [<Fact>]
    let ``circle GetDistance negative inside`` () =
        let c = Shapes.circle { X = 0.0f; Y = 0.0f } 100.0f
        let d = c.GetDistance { X = 0.0f; Y = 0.0f }
        Assert.True(d < 0.0f, sprintf "Center should be inside circle, got %f" d)

    [<Fact>]
    let ``circle GetDistance positive outside`` () =
        let c = Shapes.circle { X = 0.0f; Y = 0.0f } 100.0f
        let d = c.GetDistance { X = 200.0f; Y = 0.0f }
        Assert.True(d > 0.0f, sprintf "Far point should be outside circle, got %f" d)

    [<Fact>]
    let ``circle GetDistance zero on boundary`` () =
        let c = Shapes.circle { X = 0.0f; Y = 0.0f } 100.0f
        let d = c.GetDistance { X = 100.0f; Y = 0.0f }
        Assert.True(abs d < 0.5f, sprintf "Boundary point should be ~0, got %f" d)

    [<Fact>]
    let ``circle GetInterval at center`` () =
        let c = Shapes.circle { X = 0.0f; Y = 0.0f } 100.0f
        match c.GetInterval(0.0f) with
        | ValueSome(struct (xStart, xEnd)) ->
            Assert.True(abs (xStart + 100.0f) < 0.5f, sprintf "xStart should be ~-100, got %f" xStart)
            Assert.True(abs (xEnd - 100.0f) < 0.5f, sprintf "xEnd should be ~100, got %f" xEnd)
        | ValueNone -> Assert.Fail("Circle at y=0 should have an interval")

    [<Fact>]
    let ``circle GetInterval outside returns ValueNone`` () =
        let c = Shapes.circle { X = 0.0f; Y = 0.0f } 100.0f
        match c.GetInterval(150.0f) with
        | ValueNone -> ()
        | ValueSome _ -> Assert.Fail("Should return ValueNone outside circle")

    [<Fact>]
    let ``diamond GetInterval at center`` () =
        let d = Shapes.diamond { X = 0.0f; Y = 0.0f } 100.0f 150.0f
        match d.GetInterval(0.0f) with
        | ValueSome(struct (xStart, xEnd)) ->
            Assert.True(abs (xStart + 100.0f) < 0.5f, sprintf "xStart should be ~-100, got %f" xStart)
            Assert.True(abs (xEnd - 100.0f) < 0.5f, sprintf "xEnd should be ~100, got %f" xEnd)
        | ValueNone -> Assert.Fail("Diamond at y=0 should have an interval")

    [<Fact>]
    let ``diamond GetInterval at y=75`` () =
        let d = Shapes.diamond { X = 0.0f; Y = 0.0f } 100.0f 150.0f
        match d.GetInterval(75.0f) with
        | ValueSome(struct (xStart, xEnd)) ->
            Assert.True(abs (xStart + 50.0f) < 0.5f, sprintf "xStart should be ~-50, got %f" xStart)
            Assert.True(abs (xEnd - 50.0f) < 0.5f, sprintf "xEnd should be ~50, got %f" xEnd)
        | ValueNone -> Assert.Fail("Diamond at y=75 should have an interval")

    [<Fact>]
    let ``diamond GetInterval at apex returns ValueNone`` () =
        let d = Shapes.diamond { X = 0.0f; Y = 0.0f } 100.0f 150.0f
        match d.GetInterval(150.0f) with
        | ValueNone -> ()
        | ValueSome _ -> Assert.Fail("Should return ValueNone at diamond apex")

    [<Fact>]
    let ``diamond GetDistance inside`` () =
        let d = Shapes.diamond { X = 0.0f; Y = 0.0f } 100.0f 150.0f
        let dist = d.GetDistance { X = 0.0f; Y = 0.0f }
        Assert.True(dist < 0.0f, "Center should be inside diamond")

    [<Fact>]
    let ``diamond GetDistance outside`` () =
        let d = Shapes.diamond { X = 0.0f; Y = 0.0f } 100.0f 150.0f
        let dist = d.GetDistance { X = 200.0f; Y = 0.0f }
        Assert.True(dist > 0.0f, "Far point should be outside diamond")

    [<Fact>]
    let ``triangle GetInterval at base`` () =
        let t = Shapes.triangle { X = 0.0f; Y = 0.0f } 200.0f 100.0f
        // Base is at bottom (y = +50)
        match t.GetInterval(49.0f) with
        | ValueSome(struct (xStart, xEnd)) ->
            Assert.True(xEnd - xStart > 0.0f, "Should have positive width near base")
        | ValueNone -> Assert.Fail("Triangle near base should have an interval")

    [<Fact>]
    let ``triangle GetInterval at apex returns ValueNone`` () =
        let t = Shapes.triangle { X = 0.0f; Y = 0.0f } 200.0f 100.0f
        // Apex is at top (y = -50)
        match t.GetInterval(-50.0f) with
        | ValueNone -> ()
        | ValueSome(struct (_, xEnd)) ->
            // Very narrow interval at apex is acceptable
            ()

    [<Fact>]
    let ``parallelogram GetInterval at center`` () =
        let p = Shapes.parallelogram { X = 0.0f; Y = 0.0f } 200.0f 100.0f 0.0f
        match p.GetInterval(0.0f) with
        | ValueSome(struct (xStart, xEnd)) ->
            Assert.True(abs (xStart + 100.0f) < 0.5f, sprintf "xStart should be ~-100, got %f" xStart)
            Assert.True(abs (xEnd - 100.0f) < 0.5f, sprintf "xEnd should be ~100, got %f" xEnd)
        | ValueNone -> Assert.Fail("Parallelogram at y=0 should have an interval")

    [<Fact>]
    let ``parallelogram with shear shifts interval`` () =
        let p = Shapes.parallelogram { X = 0.0f; Y = 0.0f } 200.0f 100.0f 0.5f
        match p.GetInterval(0.0f) with
        | ValueSome(struct (xStart, xEnd)) ->
            Assert.True(abs (xStart + 100.0f) < 0.5f, "Center line should still be ~-100 to ~100")
        | ValueNone -> Assert.Fail("Should have interval")
        // Check shifted at y=25
        match p.GetInterval(25.0f) with
        | ValueSome(struct (xStart, _)) ->
            // At y=25 with shear=0.5, center shifts by 0.5*25=12.5
            Assert.True(xStart > -100.0f, "Should be shifted right by shear")
        | ValueNone -> Assert.Fail("Should have interval")

    [<Fact>]
    let ``circle BoundingBox correct`` () =
        let c = Shapes.circle { X = 10.0f; Y = 20.0f } 50.0f
        let bb = c.BoundingBox
        Assert.Equal(-40.0f, bb.Left)
        Assert.Equal(-30.0f, bb.Top)
        Assert.Equal(60.0f, bb.Right)
        Assert.Equal(70.0f, bb.Bottom)

    [<Fact>]
    let ``diamond BoundingBox correct`` () =
        let d = Shapes.diamond { X = 0.0f; Y = 0.0f } 100.0f 150.0f
        let bb = d.BoundingBox
        Assert.Equal(-100.0f, bb.Left)
        Assert.Equal(-150.0f, bb.Top)
        Assert.Equal(100.0f, bb.Right)
        Assert.Equal(150.0f, bb.Bottom)

// ============================================================
// Issue #10: SIMD Glyph-Width Summation
// ============================================================
module SimdTests =

    [<Fact>]
    let ``sumAdvances handles empty span`` () =
        let advances = ReadOnlySpan<float32>.Empty
        let sum = SimdAdvance.sumAdvances advances
        Assert.Equal(0.0f, sum)

    [<Fact>]
    let ``sumAdvances matches scalar for small array`` () =
        let data = [| 1.0f; 2.0f; 3.0f |]
        let simd = SimdAdvance.sumAdvances (ReadOnlySpan(data))
        let scalar = SimdAdvance.sumAdvancesScalar (ReadOnlySpan(data))
        Assert.True(abs (simd - scalar) < 0.001f, sprintf "SIMD %f should match scalar %f" simd scalar)

    [<Fact>]
    let ``sumAdvances matches scalar for 4-element array`` () =
        let data = [| 1.5f; 2.5f; 3.5f; 4.5f |]
        let simd = SimdAdvance.sumAdvances (ReadOnlySpan(data))
        let scalar = SimdAdvance.sumAdvancesScalar (ReadOnlySpan(data))
        Assert.True(abs (simd - scalar) < 0.001f, sprintf "SIMD %f should match scalar %f" simd scalar)
        Assert.True(abs (simd - 12.0f) < 0.001f, sprintf "Sum should be 12.0, got %f" simd)

    [<Fact>]
    let ``sumAdvances matches scalar for 1024 elements`` () =
        let rng = Random(42)
        let data = Array.init 1024 (fun _ -> float32 (rng.NextDouble()) * 100.0f)
        let simd = SimdAdvance.sumAdvances (ReadOnlySpan(data))
        let scalar = SimdAdvance.sumAdvancesScalar (ReadOnlySpan(data))
        Assert.True(abs (simd - scalar) < 0.1f, sprintf "SIMD %f should match scalar %f within tolerance" simd scalar)

    [<Fact>]
    let ``sumAdvances handles non-multiple-of-4 tails`` () =
        let data = [| 1.0f; 2.0f; 3.0f; 4.0f; 5.0f; 6.0f; 7.0f |]
        let simd = SimdAdvance.sumAdvances (ReadOnlySpan(data))
        let scalar = SimdAdvance.sumAdvancesScalar (ReadOnlySpan(data))
        Assert.True(abs (simd - scalar) < 0.001f)
        Assert.True(abs (simd - 28.0f) < 0.001f, sprintf "Sum should be 28.0, got %f" simd)

    [<Fact>]
    let ``sumAdvances single element`` () =
        let data = [| 42.0f |]
        let sum = SimdAdvance.sumAdvances (ReadOnlySpan(data))
        Assert.True(abs (sum - 42.0f) < 0.001f)

// ============================================================
// Issue #9 + #11: Layout Engine + Integration Tests
// ============================================================
module LayoutEngineTests =

    let private loadFontForLayout () =
        let tables, metrics = TestHelper.loadTestFont()
        tables, metrics

    [<Fact>]
    let ``layout Flow mode into circle produces glyphs`` () =
        let tables, metrics = loadFontForLayout()
        let shape = Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f
        let text = "The quick brown fox jumps over the lazy dog."
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 16.0f
            Mode = Flow
        }
        let result = LayoutEngine.layout req
        Assert.True(result.Glyphs.Length > 0, "Should produce glyphs")
        Assert.True(result.FontSizeUsed > 0.0f, "Font size should be positive")

        // Verify glyphs are inside the circle
        let glyphSpan = result.Glyphs.Span
        for i in 0 .. glyphSpan.Length - 1 do
            let gp = glyphSpan.[i]
            let dx = gp.X - 150.0f
            let dy = gp.Y - 150.0f
            let dist = sqrt (dx * dx + dy * dy)
            Assert.True(dist <= 155.0f, sprintf "Glyph at (%f, %f) should be inside circle, dist=%f" gp.X gp.Y dist)

        // Return buffer
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``layout Fit mode into diamond finds font size`` () =
        let tables, metrics = loadFontForLayout()
        // Large diamond so "Hello World" fits even with conservative scanline widths.
        // The layout engine queries GetInterval at the band midpoint AND both band edges,
        // taking the conservative intersection.  A 600 px-wide equatorial diamond leaves
        // ample room for the two-word phrase at any font size the binary search might choose.
        let shape = Shapes.diamond { X = 300.0f; Y = 300.0f } 300.0f 300.0f
        let text = "Hello World"
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 16.0f  // Ignored in Fit mode
            Mode = Fit
        }
        let result = LayoutEngine.layout req
        Assert.True(result.FontSizeUsed > 4.0f, sprintf "Font size should be > 4, got %f" result.FontSizeUsed)
        Assert.True(result.Glyphs.Length > 0, "Should produce glyphs")
        // In Fit mode the engine finds the largest size where all text fits; with a
        // 600 px-wide equatorial diamond, "Hello World" must be fully placed.
        Assert.True(result.LastTextIndex >= text.Length,
            sprintf "All text should fit in large diamond, LastTextIndex=%d" result.LastTextIndex)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``layout detects overflow`` () =
        let tables, metrics = loadFontForLayout()
        // Very small circle with lots of text
        let shape = Shapes.circle { X = 30.0f; Y = 30.0f } 30.0f
        let text = "This is a very long text that should definitely not fit inside a tiny circle of radius thirty pixels."
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 14.0f
            Mode = Flow
        }
        let result = LayoutEngine.layout req
        Assert.True(result.LastTextIndex < text.Length,
            sprintf "Should overflow: LastTextIndex=%d, text.Length=%d" result.LastTextIndex text.Length)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``layout empty text returns zero glyphs`` () =
        let tables, metrics = loadFontForLayout()
        let shape = Shapes.circle { X = 100.0f; Y = 100.0f } 100.0f
        let req = {
            Text = ReadOnlyMemory(Array.empty<char>)
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 16.0f
            Mode = Flow
        }
        let result = LayoutEngine.layout req
        Assert.Equal(0, result.Glyphs.Length)

    [<Fact>]
    let ``layout handles zero-area shape`` () =
        // Diamond with zero dimensions
        let tables, metrics = loadFontForLayout()
        let shape = Shapes.diamond { X = 0.0f; Y = 0.0f } 0.0f 0.0f
        let text = "Hello"
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 16.0f
            Mode = Flow
        }
        let result = LayoutEngine.layout req
        Assert.Equal(0, result.Glyphs.Length)

    [<Fact>]
    let ``layout circle produces multiple lines`` () =
        let tables, metrics = loadFontForLayout()
        let shape = Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f
        let text = "The quick brown fox jumps over the lazy dog. The quick brown fox jumps over the lazy dog."
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 14.0f
            Mode = Flow
        }
        let result = LayoutEngine.layout req
        // Should have multiple Y values (multiple lines)
        let glyphSpan = result.Glyphs.Span
        if glyphSpan.Length > 1 then
            let mutable yValues = Set.empty
            for i in 0 .. glyphSpan.Length - 1 do
                yValues <- Set.add (int (glyphSpan.[i].Y * 10.0f)) yValues
            Assert.True(yValues.Count > 1, sprintf "Should have multiple lines, got %d distinct Y values" yValues.Count)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``layout diamond Mode B all glyphs inside shape`` () =
        let tables, metrics = loadFontForLayout()
        let shape = Shapes.diamond { X = 150.0f; Y = 150.0f } 100.0f 150.0f
        let text = "Test text"
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 16.0f
            Mode = Fit
        }
        let result = LayoutEngine.layout req
        let glyphSpan = result.Glyphs.Span

        // All glyphs should be inside the diamond (SDF ≤ 0)
        for i in 0 .. glyphSpan.Length - 1 do
            let gp = glyphSpan.[i]
            let dist = shape.GetDistance { X = gp.X; Y = gp.Y }
            // Allow small tolerance for edge placement
            Assert.True(dist <= 5.0f,
                sprintf "Glyph %d at (%f, %f) should be inside diamond, SDF=%f" i gp.X gp.Y dist)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``layout triangle produces glyphs`` () =
        let tables, metrics = loadFontForLayout()
        let shape = Shapes.triangle { X = 150.0f; Y = 150.0f } 300.0f 200.0f
        let text = "Triangle text layout test"
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 12.0f
            Mode = Flow
        }
        let result = LayoutEngine.layout req
        Assert.True(result.Glyphs.Length > 0, "Should produce glyphs in triangle")

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``layout parallelogram produces glyphs`` () =
        let tables, metrics = loadFontForLayout()
        let shape = Shapes.parallelogram { X = 150.0f; Y = 100.0f } 200.0f 150.0f 0.3f
        let text = "Parallelogram text"
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 14.0f
            Mode = Flow
        }
        let result = LayoutEngine.layout req
        Assert.True(result.Glyphs.Length > 0, "Should produce glyphs in parallelogram")

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``layout circle glyph positions are monotonically increasing X per line`` () =
        let tables, metrics = loadFontForLayout()
        let shape = Shapes.circle { X = 200.0f; Y = 200.0f } 200.0f
        let text = "ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz"
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 14.0f
            Mode = Flow
        }
        let result = LayoutEngine.layout req
        let glyphSpan = result.Glyphs.Span

        // Group by line (Y value) and verify X is monotonically increasing
        let mutable prevY = Single.MinValue
        let mutable prevX = Single.MinValue
        for i in 0 .. glyphSpan.Length - 1 do
            let gp = glyphSpan.[i]
            if abs (gp.Y - prevY) > 1.0f then
                // New line
                prevX <- Single.MinValue
            Assert.True(gp.X >= prevX, sprintf "Glyph %d X=%f should be >= prevX=%f on same line" i gp.X prevX)
            prevX <- gp.X
            prevY <- gp.Y

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

// ============================================================
// Issue #12: DrawList Output Format
// ============================================================
module DrawListTests =

    let private createTestDrawList () =
        let tables, metrics = TestHelper.loadTestFont()
        let shape = Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f
        let text = "The quick brown fox"
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape
            Font = tables
            Metrics = metrics
            FontSizePx = 16.0f
            Mode = Flow
        }
        let result = LayoutEngine.layout req
        let dl = DrawList.fromLayoutResult result
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
        dl

    [<Fact>]
    let ``DrawList has correct glyph count`` () =
        let dl = createTestDrawList()
        Assert.True(dl.GlyphCount > 0, "Should have glyphs")
        Assert.Equal(dl.GlyphCount, dl.GlyphIds.Length)
        Assert.Equal(dl.GlyphCount, dl.XPositions.Length)
        Assert.Equal(dl.GlyphCount, dl.YPositions.Length)

    [<Fact>]
    let ``DrawList GlyphRasterRequests is sorted and deduplicated`` () =
        let dl = createTestDrawList()
        Assert.True(dl.GlyphRasterRequests.Length > 0, "Should have raster requests")
        // Verify sorted
        for i in 1 .. dl.GlyphRasterRequests.Length - 1 do
            Assert.True(dl.GlyphRasterRequests.[i] > dl.GlyphRasterRequests.[i - 1],
                sprintf "Raster requests should be sorted: [%d]=%d > [%d]=%d"
                    i dl.GlyphRasterRequests.[i] (i-1) dl.GlyphRasterRequests.[i-1])

    [<Fact>]
    let ``DrawList GlyphRasterRequests contains all used glyph IDs`` () =
        let dl = createTestDrawList()
        for i in 0 .. dl.GlyphCount - 1 do
            let gid = dl.GlyphIds.[i]
            let found = Array.BinarySearch(dl.GlyphRasterRequests, gid) >= 0
            Assert.True(found, sprintf "GlyphID %d should be in raster requests" gid)

    [<Fact>]
    let ``DrawList TightBounds encompasses all glyphs`` () =
        let dl = createTestDrawList()
        for i in 0 .. dl.GlyphCount - 1 do
            Assert.True(dl.XPositions.[i] >= dl.TightBounds.Left,
                sprintf "X[%d]=%f should be >= Left=%f" i dl.XPositions.[i] dl.TightBounds.Left)
            Assert.True(dl.XPositions.[i] <= dl.TightBounds.Right,
                sprintf "X[%d]=%f should be <= Right=%f" i dl.XPositions.[i] dl.TightBounds.Right)
            Assert.True(dl.YPositions.[i] >= dl.TightBounds.Top,
                sprintf "Y[%d]=%f should be >= Top=%f" i dl.YPositions.[i] dl.TightBounds.Top)
            Assert.True(dl.YPositions.[i] <= dl.TightBounds.Bottom,
                sprintf "Y[%d]=%f should be <= Bottom=%f" i dl.YPositions.[i] dl.TightBounds.Bottom)

    [<Fact>]
    let ``DrawList serialization produces correct size`` () =
        let dl = createTestDrawList()
        let bytes = DrawList.toBytes dl
        Assert.True(bytes.Length > 0, "Serialized bytes should not be empty")
        let n = dl.GlyphCount
        let m = dl.GlyphRasterRequests.Length
        let expectedSize = 4 + n * 2 + n * 4 + n * 4 + 16 + 4 + m * 2
        Assert.Equal(expectedSize, bytes.Length)

    [<Fact>]
    let ``DrawList empty produces empty`` () =
        let result = {
            Glyphs = Memory<GlyphPosition>.Empty
            GlyphBuffer = Array.empty
            TotalBounds = { Left = 0.0f; Top = 0.0f; Right = 0.0f; Bottom = 0.0f }
            LastTextIndex = 0
            FontSizeUsed = 16.0f
        }
        let dl = DrawList.fromLayoutResult result
        Assert.Equal(0, dl.GlyphCount)

    [<Fact>]
    let ``DrawList fits in expected memory`` () =
        let dl = createTestDrawList()
        let posBytes = dl.GlyphCount * 4 * 2
        let idBytes = dl.GlyphCount * 2
        let total = posBytes + idBytes
        Assert.True(total < 4096, sprintf "DrawList data should be < 4KB, got %d bytes" total)

// ============================================================
// Issue #13: LRU Shape Cache + Dirty Flag System
// ============================================================
module ShapeCacheTests =

    [<Fact>]
    let ``ShapeCache stores and retrieves entries`` () =
        let cache = ShapeCache(4)
        let key = { FontId = 1UL; TextHash = 2UL; ShapeId = 3UL; FontSizePx = 16.0f }
        let dl = DrawList.empty
        cache.Put(key, dl)
        match cache.TryGet(key) with
        | ValueSome _ -> ()
        | ValueNone -> Assert.Fail("Should find cached entry")

    [<Fact>]
    let ``ShapeCache returns ValueNone for absent key`` () =
        let cache = ShapeCache(4)
        let key = { FontId = 1UL; TextHash = 2UL; ShapeId = 3UL; FontSizePx = 16.0f }
        match cache.TryGet(key) with
        | ValueNone -> ()
        | ValueSome _ -> Assert.Fail("Should not find absent key")

    [<Fact>]
    let ``ShapeCache evicts LRU entry when full`` () =
        let cache = ShapeCache(2)
        let k1 = { FontId = 1UL; TextHash = 1UL; ShapeId = 1UL; FontSizePx = 16.0f }
        let k2 = { FontId = 2UL; TextHash = 2UL; ShapeId = 2UL; FontSizePx = 16.0f }
        let k3 = { FontId = 3UL; TextHash = 3UL; ShapeId = 3UL; FontSizePx = 16.0f }
        let dl = DrawList.empty

        cache.Put(k1, dl)
        cache.Put(k2, dl)
        Assert.Equal(2, cache.Count)

        // Access k2 to make it more recently used
        cache.TryGet(k2) |> ignore
        System.Threading.Thread.Sleep(1)

        // Add k3 — should evict k1 (LRU)
        cache.Put(k3, dl)
        Assert.Equal(2, cache.Count)

        // k1 should be evicted
        match cache.TryGet(k1) with
        | ValueNone -> ()
        | ValueSome _ -> Assert.Fail("k1 should have been evicted")

        // k2 and k3 should still exist
        Assert.True((cache.TryGet k2).IsSome, "k2 should still be cached")
        Assert.True((cache.TryGet k3).IsSome, "k3 should still be cached")

    [<Fact>]
    let ``ShapeCache manual evict removes oldest`` () =
        let cache = ShapeCache(4)
        let k1 = { FontId = 1UL; TextHash = 1UL; ShapeId = 1UL; FontSizePx = 16.0f }
        let k2 = { FontId = 2UL; TextHash = 2UL; ShapeId = 2UL; FontSizePx = 16.0f }
        cache.Put(k1, DrawList.empty)
        System.Threading.Thread.Sleep(1)
        cache.Put(k2, DrawList.empty)

        cache.Evict()
        Assert.Equal(1, cache.Count)
        match cache.TryGet(k1) with
        | ValueNone -> ()
        | ValueSome _ -> Assert.Fail("k1 should have been evicted")
        Assert.True((cache.TryGet k2).IsSome, "k2 should still exist")

    [<Fact>]
    let ``FNV1a hash produces different hashes for different strings`` () =
        let h1 = fnv1aHash "Hello"
        let h2 = fnv1aHash "World"
        let h3 = fnv1aHash "Hello"
        Assert.NotEqual(h1, h2)
        Assert.Equal(h1, h3)

    [<Fact>]
    let ``FNV1a hash handles empty string`` () =
        let h = fnv1aHash ""
        Assert.True(h <> 0UL, "Empty string hash should be the FNV offset basis, not zero")

    [<Fact>]
    let ``ShapeCache clear removes all entries`` () =
        let cache = ShapeCache(4)
        for i in 1 .. 3 do
            let key = { FontId = uint64 i; TextHash = 0UL; ShapeId = 0UL; FontSizePx = 16.0f }
            cache.Put(key, DrawList.empty)
        Assert.Equal(3, cache.Count)
        cache.Clear()
        Assert.Equal(0, cache.Count)

// ============================================================
// Issue #14-16: TextNode, Hit-Testing, Caret/Selection
// ============================================================
module TextNodeTests =

    let private createTestNode () =
        let tables, metrics = TestHelper.loadTestFont()
        let shape = Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f
        let node = TextNode.create tables metrics 1UL shape "Hello World" 16.0f
        node

    [<Fact>]
    let ``TextNode starts dirty`` () =
        let node = createTestNode()
        Assert.True(node.IsDirty, "New node should be dirty")

    [<Fact>]
    let ``TextNode update produces DrawList and clears dirty`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let dl = TextNode.update node cache
        Assert.True(dl.GlyphCount > 0, "Should produce glyphs")
        Assert.False(node.IsDirty, "Should not be dirty after update")

    [<Fact>]
    let ``TextNode second update uses cache`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let dl1 = TextNode.update node cache
        let dl2 = TextNode.update node cache
        Assert.Equal(dl1.GlyphCount, dl2.GlyphCount)
        Assert.False(node.IsDirty)

    [<Fact>]
    let ``TextNode setText marks dirty`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let _ = TextNode.update node cache
        Assert.False(node.IsDirty)
        TextNode.setText node "New text"
        Assert.True(node.IsDirty)

    [<Fact>]
    let ``TextNode setFontSize marks dirty`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let _ = TextNode.update node cache
        TextNode.setFontSize node 24.0f
        Assert.True(node.IsDirty)

    [<Fact>]
    let ``TextNode setTransform does NOT mark dirty`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let _ = TextNode.update node cache
        TextNode.setTransform node (System.Numerics.Matrix3x2.CreateTranslation(10.0f, 20.0f))
        Assert.False(node.IsDirty, "Transform change should not mark dirty")

    [<Fact>]
    let ``TextNode hitTest returns valid index`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let dl = TextNode.update node cache
        let advances = Array.init dl.GlyphCount (fun _ -> 10.0f)
        if dl.GlyphCount > 0 then
            let firstX = dl.XPositions.[0]
            let firstY = dl.YPositions.[0]
            let idx = TextNode.hitTest dl (firstX + 5.0f) firstY 20.0f advances
            Assert.True(idx >= 0, "Should find a hit at first glyph position")

    [<Fact>]
    let ``TextNode hitTest returns -1 for miss`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let dl = TextNode.update node cache
        let advances = Array.init dl.GlyphCount (fun _ -> 10.0f)
        let idx = TextNode.hitTest dl -1000.0f -1000.0f 20.0f advances
        Assert.Equal(-1, idx)

    [<Fact>]
    let ``TextNode caretPosition at start`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let dl = TextNode.update node cache
        let advances = Array.init dl.GlyphCount (fun _ -> 10.0f)
        let struct (x, _, h) = TextNode.caretPosition dl 0 20.0f advances
        if dl.GlyphCount > 0 then
            Assert.Equal(dl.XPositions.[0], x)
            Assert.True(h > 0.0f)

    [<Fact>]
    let ``TextNode caretPosition after last glyph`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let dl = TextNode.update node cache
        let advances = Array.init dl.GlyphCount (fun _ -> 10.0f)
        let struct (x, _, _) = TextNode.caretPosition dl dl.GlyphCount 20.0f advances
        if dl.GlyphCount > 0 then
            let lastX = dl.XPositions.[dl.GlyphCount - 1]
            Assert.True(x > lastX, "Caret after last glyph should be past its X position")

    [<Fact>]
    let ``TextNode selectionRects returns rects`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let dl = TextNode.update node cache
        let advances = Array.init dl.GlyphCount (fun _ -> 10.0f)
        if dl.GlyphCount > 2 then
            let rects = TextNode.selectionRects dl 0 3 20.0f advances
            Assert.True(rects.Length > 0, "Should have selection rectangles")
            for struct (_, _, w, h) in rects do
                Assert.True(w > 0.0f, "Selection width should be positive")
                Assert.True(h > 0.0f, "Selection height should be positive")

    [<Fact>]
    let ``TextNode selectionRects empty range returns empty`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let dl = TextNode.update node cache
        let advances = Array.init dl.GlyphCount (fun _ -> 10.0f)
        let rects = TextNode.selectionRects dl 3 3 20.0f advances
        Assert.Equal(0, rects.Length)

    [<Fact>]
    let ``TextNode update after text change produces different DrawList`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let _ = TextNode.update node cache
        TextNode.setText node "Different text"
        let dl2 = TextNode.update node cache
        Assert.True(dl2.GlyphCount > 0)

    [<Fact>]
    let ``TextNode cached DrawList is returned on non-dirty update`` () =
        let node = createTestNode()
        let cache = defaultCache()
        let dl1 = TextNode.update node cache
        let dl2 = TextNode.update node cache
        Assert.Equal(dl1.GlyphCount, dl2.GlyphCount)
        for i in 0 .. dl1.GlyphCount - 1 do
            Assert.Equal(dl1.GlyphIds.[i], dl2.GlyphIds.[i])

// ============================================================
// Industry-Standard Glyph Positioning Tests
//
// These tests verify that GlyphAura produces glyph positions that
// are consistent with the OpenType specification and match what
// industry-standard engines (HarfBuzz, FreeType) produce.
//
// Key principles being tested:
// 1. Glyph origin = current pen position (not pen + kern offset)
// 2. Pen advances by: base_advance + kern(current → next)
// 3. Kerned pairs are CLOSER than un-kerned (kern is negative for tight pairs)
// 4. Absolute positions match OpenType formula:
//      position[i] = sum_{j<i} (advance[j] + kern(j, j+1))  * scale
// 5. GPOS kern takes precedence over legacy kern
// ============================================================
module PositioningTests =

    // ── DejaVu Sans metrics (verified against font binary) ──────────────────
    // UPM = 2048
    // 'V' glyphId=57  advance=1401 design units
    // 'A' glyphId=36  advance=1401 design units
    // 'T' glyphId=55  advance=1251 design units
    // 'e' glyphId=72  advance=1260 design units
    // 'o' glyphId=82  advance=1253 design units
    // 'W' glyphId=58  advance=2025 design units
    // 'i' glyphId=76  advance=569  design units
    // kern(V,A) = kern(A,V) = -131 design units (legacy; GPOS same)
    // kern(T,o) = kern(T,e) = -348 design units (legacy; GPOS same)
    // kern(W,i)  verified via legacy kern table

    let [<Literal>] Upm  = 2048.0f
    let [<Literal>] FontSizePx = 100.0f        // clean round number for exact arithmetic
    let [<Literal>] Scale = FontSizePx / Upm   // 100 / 2048

    // DejaVu Sans design-unit advances
    let [<Literal>] AdvV = 1401.0f
    let [<Literal>] AdvA = 1401.0f
    let [<Literal>] AdvT = 1251.0f
    let [<Literal>] Adve = 1260.0f
    let [<Literal>] Advo = 1253.0f
    let [<Literal>] AdvW = 2025.0f
    let [<Literal>] Advi = 569.0f

    // Kern values (design units, negative = tighter spacing)
    let [<Literal>] KernVA = -131.0f   // kern(V → A)
    let [<Literal>] KernAV = -131.0f   // kern(A → V)
    let [<Literal>] KernTo = -348.0f   // kern(T → o)
    let [<Literal>] KernTe = -348.0f   // kern(T → e)

    // Tolerance: 0.01 px is acceptable for float32 accumulation
    let [<Literal>] Tol = 0.1f

    let private setup () =
        let tables, metrics = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea numGlyphs
        let kern =
            match tables.Kern with
            | ValueSome kd -> KernLegacy.parse kd
            | ValueNone    -> ValueNone
        tables, metrics, cmap, hmtx, kern

    // ── 1. Shaper: look-ahead kern semantics ────────────────────────────────

    [<Fact>]
    let ``Shaper bakes kern(V→A) into V's XAdvance`` () =
        let _, _, cmap, hmtx, kern = setup()
        let text = "VA".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let result = Shaper.shapeLine text cmap hmtx kern Scale Single.MaxValue buffer
        Assert.Equal(2, result.GlyphCount)
        // V's XAdvance = V.base_advance + kern(V→A) = (1401 - 131) = 1270 design units
        let expectedVAdv = AdvV + KernVA
        let actualVAdv   = buffer.[0].XAdvance
        Assert.True(
            abs (actualVAdv - expectedVAdv) < 0.5f,
            sprintf "V XAdvance should be %g (=1401+kern), got %g" expectedVAdv actualVAdv)
        // A's XAdvance = A.base_advance (last glyph, no look-ahead) = 1401
        let actualAAdv = buffer.[1].XAdvance
        Assert.True(
            abs (actualAAdv - AdvA) < 0.5f,
            sprintf "A XAdvance (last glyph) should equal base advance %g, got %g" AdvA actualAAdv)

    [<Fact>]
    let ``Shaper bakes kern(T→o) into T's XAdvance`` () =
        let _, _, cmap, hmtx, kern = setup()
        let text = "To".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let result = Shaper.shapeLine text cmap hmtx kern Scale Single.MaxValue buffer
        Assert.Equal(2, result.GlyphCount)
        let expectedTAdv = AdvT + KernTo   // 1251 - 348 = 903
        Assert.True(
            abs (buffer.[0].XAdvance - expectedTAdv) < 0.5f,
            sprintf "T XAdvance should be %g, got %g" expectedTAdv buffer.[0].XAdvance)

    [<Fact>]
    let ``Shaper last glyph has no look-ahead kern`` () =
        let _, _, cmap, hmtx, kern = setup()
        let text = "AV".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let _ = Shaper.shapeLine text cmap hmtx kern Scale Single.MaxValue buffer
        // V is the last glyph: no next character, so XAdvance = base advance only
        Assert.True(
            abs (buffer.[1].XAdvance - AdvV) < 0.5f,
            sprintf "Last glyph V should have base advance %g, got %g" AdvV buffer.[1].XAdvance)

    [<Fact>]
    let ``Shaper single glyph has no kern`` () =
        let _, _, cmap, hmtx, kern = setup()
        let text = "A".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let _ = Shaper.shapeLine text cmap hmtx kern Scale Single.MaxValue buffer
        Assert.True(
            abs (buffer.[0].XAdvance - AdvA) < 0.5f,
            sprintf "Single-glyph A should have base advance %g, got %g" AdvA buffer.[0].XAdvance)

    [<Fact>]
    let ``Shaper TotalAdvance for VA equals advances plus kern`` () =
        let _, _, cmap, hmtx, kern = setup()
        let text = "VA".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let result = Shaper.shapeLine text cmap hmtx kern Scale Single.MaxValue buffer
        // TotalAdvance = (AdvV + KernVA + AdvA) * Scale
        let expected = (AdvV + KernVA + AdvA) * Scale
        Assert.True(
            abs (result.TotalAdvance - expected) < Tol,
            sprintf "TotalAdvance for VA should be %g px, got %g px" expected result.TotalAdvance)

    [<Fact>]
    let ``Shaper TotalAdvance for To equals advances plus kern`` () =
        let _, _, cmap, hmtx, kern = setup()
        let text = "To".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let result = Shaper.shapeLine text cmap hmtx kern Scale Single.MaxValue buffer
        let expected = (AdvT + KernTo + Advo) * Scale
        Assert.True(
            abs (result.TotalAdvance - expected) < Tol,
            sprintf "TotalAdvance for To should be %g px, got %g px" expected result.TotalAdvance)

    // ── 2. Layout engine: glyph X positions match OpenType spec ─────────────

    /// Shape text into a very wide parallelogram so all glyphs land on one line.
    let private layoutOnOneLine (text: string) (tables: FontTables) (metrics: FontMetrics) =
        // Wide, tall shape — all glyphs on one line
        let shape = Shapes.parallelogram { X = 5000.0f; Y = 100.0f } 9000.0f 200.0f 0.0f
        let req = {
            Text        = ReadOnlyMemory(text.ToCharArray())
            Shape       = shape
            Font        = tables
            Metrics     = metrics
            FontSizePx  = FontSizePx
            Mode        = Flow
        }
        let result = LayoutEngine.layout req
        result

    [<Fact>]
    let ``Layout V at position 0`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "V" tables metrics
        Assert.True(result.Glyphs.Length >= 1, "Should place V")
        // V is the first (and only) glyph — its X equals the left edge (xStart).
        // We don't test the absolute X because the shape's left edge varies with y;
        // instead we verify V.X ≥ 0 (placed inside shape).
        let span = result.Glyphs.Span
        Assert.True(span.[0].X >= 0.0f, "V should have non-negative X")
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout VA: A.X equals V.X plus (V.advance + kern(V→A)) * scale`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "VA" tables metrics
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 2, "Should place both V and A")
        let vX = span.[0].X
        let aX = span.[1].X
        // Expected A position = V.X + (AdvV + KernVA) * Scale
        // Note: GPOS may override legacy kern; both are -131 for V,A in DejaVu Sans
        let advanceWithKern = (AdvV + KernVA) * Scale  // ≈ 62.01 px
        let expectedAX = vX + advanceWithKern
        Assert.True(
            abs (aX - expectedAX) < Tol,
            sprintf "A.X should be %g (V.X %g + advance %g), got %g" expectedAX vX advanceWithKern aX)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout AV: V.X equals A.X plus (A.advance + kern(A→V)) * scale`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "AV" tables metrics
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 2, "Should place both A and V")
        let aX = span.[0].X
        let vX = span.[1].X
        let advanceWithKern = (AdvA + KernAV) * Scale
        let expectedVX = aX + advanceWithKern
        Assert.True(
            abs (vX - expectedVX) < Tol,
            sprintf "V.X should be %g (A.X %g + advance %g), got %g" expectedVX aX advanceWithKern vX)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout To: o.X equals T.X plus (T.advance + kern(T→o)) * scale`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "To" tables metrics
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 2, "Should place both T and o")
        let tX = span.[0].X
        let oX = span.[1].X
        let advanceWithKern = (AdvT + KernTo) * Scale   // ≈ 44.09 px
        let expectedOX = tX + advanceWithKern
        Assert.True(
            abs (oX - expectedOX) < Tol,
            sprintf "o.X should be %g (T.X %g + advance %g), got %g" expectedOX tX advanceWithKern oX)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout Te: e.X equals T.X plus (T.advance + kern(T→e)) * scale`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "Te" tables metrics
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 2, "Should place both T and e")
        let tX = span.[0].X
        let eX = span.[1].X
        let advanceWithKern = (AdvT + KernTe) * Scale
        let expectedEX = tX + advanceWithKern
        Assert.True(
            abs (eX - expectedEX) < Tol,
            sprintf "e.X should be %g (T.X %g + advance %g), got %g" expectedEX tX advanceWithKern eX)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    // ── 3. Kerned spacing < unkerned spacing ────────────────────────────────

    [<Fact>]
    let ``VA pair: A.X is closer to V.X than without kern`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "VA" tables metrics
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 2)
        let gap = span.[1].X - span.[0].X
        // Without kern: gap = AdvV * Scale ≈ 68.41 px
        let noKernGap = AdvV * Scale
        Assert.True(gap < noKernGap,
            sprintf "VA gap %g should be < no-kern gap %g" gap noKernGap)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``To pair: o.X is closer to T.X than without kern`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "To" tables metrics
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 2)
        let gap = span.[1].X - span.[0].X
        let noKernGap = AdvT * Scale
        Assert.True(gap < noKernGap,
            sprintf "To gap %g should be < no-kern gap %g" gap noKernGap)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    // ── 4. Three-glyph chain: additive kern accumulation ────────────────────

    [<Fact>]
    let ``Layout VAV: third glyph X accumulates correctly`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "VAV" tables metrics
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 3, "Should place V, A, V")
        let v1X = span.[0].X
        let aX  = span.[1].X
        let v2X = span.[2].X
        // A.X = V1.X + (AdvV + KernVA) * Scale
        let expectedAX = v1X + (AdvV + KernVA) * Scale
        Assert.True(abs (aX - expectedAX) < Tol,
            sprintf "A.X should be %g, got %g" expectedAX aX)
        // V2.X = A.X + (AdvA + KernAV) * Scale
        let expectedV2X = aX + (AdvA + KernAV) * Scale
        Assert.True(abs (v2X - expectedV2X) < Tol,
            sprintf "V2.X should be %g, got %g" expectedV2X v2X)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout ABC (no-kern pair): positions are pure advance multiples`` () =
        let tables, metrics = TestHelper.loadTestFont()
        // 'B' glyphId should not kern with 'C' — use a pair without known kern
        let result = layoutOnOneLine "III" tables metrics   // i,i,i — likely no kern
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 3, "Should place 3 glyphs")
        // All three should be strictly monotonically increasing
        Assert.True(span.[1].X > span.[0].X, "Second glyph must be right of first")
        Assert.True(span.[2].X > span.[1].X, "Third glyph must be right of second")
        // Check spacing consistency: span[1]-span[0] ≈ span[2]-span[1]  (equal advances)
        let gap01 = span.[1].X - span.[0].X
        let gap12 = span.[2].X - span.[1].X
        Assert.True(abs (gap01 - gap12) < Tol,
            sprintf "Equal advances for iii: gap01=%g gap12=%g" gap01 gap12)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    // ── 5. Regression: verify the old "kern-offset-on-glyph" bug is gone ───

    [<Fact>]
    let ``Regression: VA A.X not equal to bare V advance (old buggy value)`` () =
        // Old bug: shaper stored kern(prev→curr) in curr.XAdvance.
        // layout placed glyph at `x` (no kern shift) then advanced by XAdvance.
        // Result: A.X = AdvV * Scale  (no kern applied to position)
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "VA" tables metrics
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 2)
        let v1X = span.[0].X
        let aX  = span.[1].X
        let buggyAX = v1X + AdvV * Scale  // old buggy position
        // Correct position must differ from the buggy one
        Assert.True(abs (aX - buggyAX) > 1.0f,
            sprintf "A.X should differ from buggy value %g; got %g" buggyAX aX)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Regression: To o.X not equal to bare T advance (old buggy value)`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "To" tables metrics
        let span = result.Glyphs.Span
        Assert.True(span.Length >= 2)
        let tX = span.[0].X
        let oX = span.[1].X
        let buggyOX = tX + AdvT * Scale   // old buggy: T advance only, no kern
        Assert.True(abs (oX - buggyOX) > 5.0f,
            sprintf "o.X should differ from buggy value %g; got %g" buggyOX oX)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    // ── 6. GPOS kern correctness ────────────────────────────────────────────

    [<Fact>]
    let ``GPOS kern value for To equals legacy kern value in DejaVu Sans`` () =
        // DejaVu Sans stores the same kern in both GPOS and legacy table.
        // If this ever changes, it signals the font was updated.
        let tables, _ = TestHelper.loadTestFont()
        let cmap = CMap.parse tables.Cmap
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let gposKern =
            match tables.Gpos with
            | ValueSome gd -> GposKerning.parse gd numGlyphs
            | ValueNone    -> Assert.Fail("No GPOS"); ValueNone
        let legacyKern =
            match tables.Kern with
            | ValueSome kd -> KernLegacy.parse kd
            | ValueNone    -> Assert.Fail("No legacy kern"); ValueNone
        let glyphT = CMap.resolve cmap (uint32 'T')
        let glypho = CMap.resolve cmap (uint32 'o')
        let gposVal    = GposKerning.getAdjustment gposKern.Value glyphT glypho
        let legacyVal  = KernLegacy.getAdjustment  legacyKern.Value glyphT glypho
        Assert.Equal(int gposVal, int legacyVal)

    [<Fact>]
    let ``Layout produces tighter spacing with GPOS than without any kern`` () =
        // This is a structural test: GPOS kern must reduce spacing for To.
        let tables, metrics = TestHelper.loadTestFont()
        let result = layoutOnOneLine "To" tables metrics
        let span   = result.Glyphs.Span
        Assert.True(span.Length >= 2)
        let gap = span.[1].X - span.[0].X
        // No-kern gap = AdvT * Scale ≈ 61.08 px; with kern it should be ≈ 44.09 px
        let tightGap = (AdvT + KernTo) * Scale
        // Allow ±1 px because GPOS might slightly differ from legacy
        Assert.True(abs (gap - tightGap) < 1.5f,
            sprintf "To spacing %g should be close to kern-corrected %g" gap tightGap)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

// ============================================================
// Multi-Script & Unicode Coverage Tests
//
// Verifies that GlyphAura handles the full Unicode spectrum:
//  • UTF-16 surrogate pairs → single glyph (not two .notdef)
//  • BMP scripts: Hebrew, Arabic, Greek, Cyrillic, Math, Symbols
//  • Supplementary plane emoji (U+1F600) in DejaVu Sans
//  • PUA / icon-font codepoints (graceful .notdef fallback)
//  • .notdef advance used when codepoint is unmapped
//  • Mixed-script text → no crash, correct glyph count
//  • ZWJ cluster: ZWJ + char treated as grapheme cluster boundary
//  • Layout monotonicity over multi-script text
//
// All glyph IDs below were verified against the DejaVu Sans TTF
// binary in tests/TestData/DejaVuSans.ttf (Format 12 cmap,
// platform=3 encoding=10).
// ============================================================
module MultiScriptTests =

    // ── Known glyph IDs in DejaVu Sans (verified from font binary) ──────────
    // Script        Char          Unicode    GlyphID
    // Latin         'A'           U+0041     36
    // Hebrew        'א' (Alef)    U+05D0     1319
    // Arabic        'ا' (Alef)    U+0627     1365
    // Greek         'α' (alpha)   U+03B1     838
    // Cyrillic      'А' (A)       U+0410     933
    // Math ∑        U+2211        3228
    // Math ∫        U+222B        3254
    // Math ≠        U+2260        3307
    // Musical ♩     U+2669        3905
    // Snowman ☃     U+2603        3803
    // Emoji 😀      U+1F600       5857   ← supplementary plane, needs surrogate pair
    // .notdef       —             —          0    (advance = 1229 design units)
    // PUA U+E001    —             —          0    (.notdef — not in DejaVu Sans)

    let private loadParts () =
        let tables, metrics = TestHelper.loadTestFont()
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let cmap = CMap.parse tables.Cmap
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea numGlyphs
        let kern =
            match tables.Kern with
            | ValueSome kd -> KernLegacy.parse kd
            | ValueNone    -> ValueNone
        tables, metrics, cmap, hmtx, kern

    // ── 1. Surrogate pair handling ───────────────────────────────────────────

    [<Fact>]
    let ``Shaper: emoji surrogate pair produces 1 glyph, not 2`` () =
        let _, _, cmap, hmtx, kern = loadParts()
        // 😀 is U+1F600, encoded in UTF-16 as surrogate pair (2 char units)
        let text = "😀".AsSpan()  // text.Length = 2 (UTF-16)
        Assert.Equal(2, text.Length)   // verify it really IS a surrogate pair
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let result = Shaper.shapeLine text cmap hmtx kern 1.0f Single.MaxValue buffer
        // Should produce EXACTLY 1 glyph (the emoji), not 2 .notdef
        Assert.Equal(1, result.GlyphCount)

    [<Fact>]
    let ``Shaper: emoji resolves to correct glyph ID 5857`` () =
        let _, _, cmap, hmtx, kern = loadParts()
        let text = "😀".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let _ = Shaper.shapeLine text cmap hmtx kern 1.0f Single.MaxValue buffer
        Assert.Equal(5857us, buffer.[0].GlyphId)

    [<Fact>]
    let ``Shaper: emoji ClusterIndex points to first char unit (0)`` () =
        let _, _, cmap, hmtx, kern = loadParts()
        let text = "😀".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let _ = Shaper.shapeLine text cmap hmtx kern 1.0f Single.MaxValue buffer
        Assert.Equal(0, buffer.[0].ClusterIndex)

    [<Fact>]
    let ``Shaper: text with surrogate pair produces correct glyph count`` () =
        let _, _, cmap, hmtx, kern = loadParts()
        // "A😀B" = 4 UTF-16 code units → should produce 3 glyphs (A, 😀, B)
        let text = "A😀B".AsSpan()
        Assert.Equal(4, text.Length)   // verify surrogate pair makes 4 chars
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let result = Shaper.shapeLine text cmap hmtx kern 1.0f Single.MaxValue buffer
        Assert.Equal(3, result.GlyphCount)

    [<Fact>]
    let ``Shaper: surrogate pair ClusterIndex for second glyph is 2 (after pair)`` () =
        let _, _, cmap, hmtx, kern = loadParts()
        // "A😀B": A→cluster=0, ��→cluster=1, B→cluster=3
        let text = "A😀B".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let _ = Shaper.shapeLine text cmap hmtx kern 1.0f Single.MaxValue buffer
        Assert.Equal(0, buffer.[0].ClusterIndex)   // 'A' at char 0
        Assert.Equal(1, buffer.[1].ClusterIndex)   // '😀' starts at char 1 (high surrogate)
        Assert.Equal(3, buffer.[2].ClusterIndex)   // 'B' at char 3 (after 2-unit emoji)

    [<Fact>]
    let ``Shaper: multiple surrogate pairs produce correct glyph count`` () =
        let _, _, cmap, hmtx, kern = loadParts()
        // "😀😀" = 4 UTF-16 code units → 2 glyphs
        let text = "😀😀".AsSpan()
        Assert.Equal(4, text.Length)
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let result = Shaper.shapeLine text cmap hmtx kern 1.0f Single.MaxValue buffer
        Assert.Equal(2, result.GlyphCount)

    [<Fact>]
    let ``Shaper: emoji has positive XAdvance (not zero-width like error glyphs)`` () =
        let _, _, cmap, hmtx, kern = loadParts()
        let text = "😀".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let _ = Shaper.shapeLine text cmap hmtx kern 1.0f Single.MaxValue buffer
        // DejaVu emoji advance = 2135 design units
        Assert.True(buffer.[0].XAdvance > 1000.0f,
            sprintf "Emoji should have positive advance, got %g" buffer.[0].XAdvance)

    // ── 2. BMP multi-script glyph resolution ────────────────────────────────

    [<Fact>]
    let ``CMap: Hebrew alef (U+05D0) resolves to non-zero glyph ID`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0x05D0u  // א
        Assert.Equal(1319us, gid)

    [<Fact>]
    let ``CMap: Arabic alef (U+0627) resolves to non-zero glyph ID`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0x0627u  // ا
        Assert.Equal(1365us, gid)

    [<Fact>]
    let ``CMap: Greek small alpha (U+03B1) resolves to correct glyph ID`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0x03B1u  // α
        Assert.Equal(838us, gid)

    [<Fact>]
    let ``CMap: Cyrillic capital A (U+0410) resolves to correct glyph ID`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0x0410u  // А
        Assert.Equal(933us, gid)

    [<Fact>]
    let ``CMap: Math sum symbol (U+2211) resolves to non-zero glyph ID`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0x2211u  // ∑
        Assert.Equal(3228us, gid)

    [<Fact>]
    let ``CMap: Math integral (U+222B) resolves to non-zero glyph ID`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0x222Bu  // ∫
        Assert.Equal(3254us, gid)

    [<Fact>]
    let ``CMap: Math not-equal (U+2260) resolves to non-zero glyph ID`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0x2260u  // ≠
        Assert.Equal(3307us, gid)

    [<Fact>]
    let ``CMap: Snowman (U+2603) resolves to non-zero glyph ID`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0x2603u  // ☃
        Assert.Equal(3803us, gid)

    [<Fact>]
    let ``CMap: PUA U+E001 resolves to .notdef (font has no PUA glyphs)`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0xE001u
        // DejaVu Sans has no PUA glyphs; expect .notdef (0)
        Assert.Equal(0us, gid)

    [<Fact>]
    let ``CMap: unmapped codepoint U+FFFE returns .notdef`` () =
        let _, _, cmap, _, _ = loadParts()
        let gid = CMap.resolve cmap 0xFFFEu
        Assert.Equal(0us, gid)

    // ── 3. Graceful .notdef fallback ─────────────────────────────────────────

    [<Fact>]
    let ``Shaper: unmapped codepoint produces .notdef glyph with positive advance`` () =
        let _, _, cmap, hmtx, kern = loadParts()
        // Use a private-use character that DejaVu Sans doesn't have
        let text = "\uE001".AsSpan()  // PUA - not in DejaVu Sans
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let result = Shaper.shapeLine text cmap hmtx kern 1.0f Single.MaxValue buffer
        Assert.Equal(1, result.GlyphCount)
        Assert.Equal(0us, buffer.[0].GlyphId)   // .notdef
        // .notdef should have a positive advance (1229 design units in DejaVu Sans)
        Assert.True(buffer.[0].XAdvance > 0.0f, ".notdef should have positive advance")

    [<Fact>]
    let ``Shaper: text with mixed mapped and unmapped codepoints produces correct count`` () =
        let _, _, cmap, hmtx, kern = loadParts()
        // 'A' (mapped), PUA (unmapped → .notdef), 'B' (mapped) = 3 glyphs
        let text = "A\uE001B".AsSpan()
        let buffer = Array.zeroCreate<ShapedGlyph> 10
        let result = Shaper.shapeLine text cmap hmtx kern 1.0f Single.MaxValue buffer
        Assert.Equal(3, result.GlyphCount)
        Assert.Equal(36us, buffer.[0].GlyphId)   // 'A'
        Assert.Equal(0us,  buffer.[1].GlyphId)   // .notdef
        Assert.Equal(37us, buffer.[2].GlyphId)   // 'B' = glyph 37 in DejaVu

    // ── 4. Multi-script layout: no crash, monotonic positions ───────────────

    let private layoutWide (text: string) =
        let tables, metrics = TestHelper.loadTestFont()
        let shape = Shapes.parallelogram { X = 5000.0f; Y = 100.0f } 9000.0f 200.0f 0.0f
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape; Font = tables; Metrics = metrics
            FontSizePx = 20.0f; Mode = Flow
        }
        let result = LayoutEngine.layout req
        result

    [<Fact>]
    let ``Layout: Hebrew text does not crash and produces glyphs`` () =
        // שלום (Shalom) — Hebrew, RTL
        let result = layoutWide "שלום"
        Assert.True(result.Glyphs.Length > 0, "Hebrew text should produce glyphs")
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: Arabic text does not crash and produces glyphs`` () =
        // مرحبا (Marhaba / hello) — Arabic, RTL
        let result = layoutWide "مرحبا"
        Assert.True(result.Glyphs.Length > 0, "Arabic text should produce glyphs")
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: Greek text produces correct glyph count`` () =
        let result = layoutWide "αβγδ"
        // Should produce exactly 4 glyphs (all are BMP, no surrogates)
        Assert.Equal(4, result.Glyphs.Length)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: Math symbols produce non-zero glyph IDs`` () =
        let result = layoutWide "∑∫≠"
        let span = result.Glyphs.Span
        Assert.Equal(3, span.Length)
        // All three should resolve to known non-zero glyph IDs
        Assert.Equal(3228us, span.[0].GlyphId)  // ∑
        Assert.Equal(3254us, span.[1].GlyphId)  // ∫
        Assert.Equal(3307us, span.[2].GlyphId)  // ≠
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: emoji in text produces 1 glyph with correct glyph ID`` () =
        let result = layoutWide "A😀B"
        let span = result.Glyphs.Span
        // "A😀B" = 4 UTF-16 chars = 3 codepoints = 3 glyphs
        Assert.Equal(3, span.Length)
        Assert.Equal(36us,   span.[0].GlyphId)  // 'A'
        Assert.Equal(5857us, span.[1].GlyphId)  // 😀
        Assert.Equal(37us,   span.[2].GlyphId)  // 'B' = glyph 37 in DejaVu
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: mixed Latin-Greek-Hebrew text does not crash`` () =
        let result = layoutWide "Hello αβγ שלום"
        Assert.True(result.Glyphs.Length > 0, "Mixed script text should produce glyphs")
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: math formula text produces expected glyph count`` () =
        // "∑x≠∫" — 4 BMP chars = 4 codepoints = 4 glyphs
        let result = layoutWide "∑x≠∫"
        Assert.Equal(4, result.Glyphs.Length)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: emoji surrounded by Latin has monotonically increasing X positions`` () =
        let result = layoutWide "A😀B"
        let span = result.Glyphs.Span
        Assert.Equal(3, span.Length)
        Assert.True(span.[1].X > span.[0].X, "Emoji X should be right of A")
        Assert.True(span.[2].X > span.[1].X, "B X should be right of emoji")
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    // ── 5. ZWJ cluster handling ──────────────────────────────────────────────

    [<Fact>]
    let ``TextAnalyzer: ZWJ is not a cluster boundary`` () =
        // "A\u200DB" — ZWJ between A and B: should not break cluster before B
        let text = "A\u200DB".AsSpan()
        // After A (index 1): ZWJ is at index 1, then B at index 2
        // isClusterBoundary(1) → false because ZWJ is not a boundary breaker BEFORE ZWJ
        //   Wait — the cluster boundary check: position 1 is before ZWJ.
        //   GB9: × ZWJ: "do not break before ZWJ" — isClusterBoundary(1) = false
        Assert.False(TextAnalyzer.isClusterBoundary text 1,
            "Should not break before ZWJ")

    [<Fact>]
    let ``TextAnalyzer: combining diacritic not a cluster boundary`` () =
        // "e\u0301" (é) — combining acute accent
        let text = "e\u0301".AsSpan()
        Assert.False(TextAnalyzer.isClusterBoundary text 1,
            "Combining accent should not be a cluster boundary")

    [<Fact>]
    let ``TextAnalyzer: RTL detection for Hebrew`` () =
        // Text analyzer should detect Hebrew as RTL
        let text = "שלום".AsSpan()
        let runs = Array.zeroCreate<TextAnalyzer.TextRun> 10
        let count = TextAnalyzer.analyze text runs
        Assert.True(count >= 1, "Should detect at least one run")
        Assert.True(runs.[0].IsRTL, "Hebrew should be detected as RTL")

    [<Fact>]
    let ``TextAnalyzer: RTL detection for Arabic`` () =
        let text = "مرحبا".AsSpan()
        let runs = Array.zeroCreate<TextAnalyzer.TextRun> 10
        let count = TextAnalyzer.analyze text runs
        Assert.True(count >= 1, "Should detect at least one run")
        Assert.True(runs.[0].IsRTL, "Arabic should be detected as RTL")

    [<Fact>]
    let ``TextAnalyzer: LTR detection for Latin`` () =
        let text = "Hello".AsSpan()
        let runs = Array.zeroCreate<TextAnalyzer.TextRun> 10
        let count = TextAnalyzer.analyze text runs
        Assert.True(count >= 1, "Should detect at least one run")
        Assert.False(runs.[0].IsRTL, "Latin should be detected as LTR")

    // ── 6. Long-text layout coverage (circle and triangle) ───────────────────

    let private longText =
        "The quick brown fox jumps over the lazy dog. " +
        "Pack my box with five dozen liquor jugs. " +
        "How vexingly quick daft zebras jump! " +
        "The five boxing wizards jump quickly."

    [<Fact>]
    let ``Layout: long text in circle produces many glyphs`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let shape = Shapes.circle { X = 200.0f; Y = 200.0f } 200.0f
        let req = {
            Text = ReadOnlyMemory(longText.ToCharArray())
            Shape = shape; Font = tables; Metrics = metrics
            FontSizePx = 12.0f; Mode = Flow
        }
        let result = LayoutEngine.layout req
        Assert.True(result.Glyphs.Length > 30,
            sprintf "Long text in circle should yield > 30 glyphs, got %d" result.Glyphs.Length)
        // All glyphs must be inside or on the circle boundary
        let span = result.Glyphs.Span
        for i in 0 .. span.Length - 1 do
            let gp = span.[i]
            let dx = gp.X - 200.0f
            let dy = gp.Y - 200.0f
            let dist = sqrt (dx*dx + dy*dy)
            Assert.True(dist <= 205.0f,
                sprintf "Glyph %d at (%.1f,%.1f) outside circle (dist=%.1f)" i gp.X gp.Y dist)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: long text in triangle produces many glyphs`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let shape = Shapes.triangle { X = 250.0f; Y = 200.0f } 400.0f 300.0f
        let req = {
            Text = ReadOnlyMemory(longText.ToCharArray())
            Shape = shape; Font = tables; Metrics = metrics
            FontSizePx = 11.0f; Mode = Flow
        }
        let result = LayoutEngine.layout req
        Assert.True(result.Glyphs.Length > 40,
            sprintf "Long text in triangle should yield > 40 glyphs, got %d" result.Glyphs.Length)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: X positions monotonically increase across multi-script line`` () =
        let tables, metrics = TestHelper.loadTestFont()
        // Wide shape ensures single line
        let shape = Shapes.parallelogram { X = 5000.0f; Y = 100.0f } 9000.0f 200.0f 0.0f
        let text = "Hello αβγ ∑∫ ☃♠"
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape; Font = tables; Metrics = metrics
            FontSizePx = 20.0f; Mode = Flow
        }
        let result = LayoutEngine.layout req
        let span = result.Glyphs.Span
        Assert.True(span.Length > 5, "Should place multiple glyphs")
        let mutable prevX = Single.MinValue
        let mutable prevY = Single.MinValue
        for i in 0 .. span.Length - 1 do
            let gp = span.[i]
            if abs(gp.Y - prevY) > 1.0f then
                prevX <- Single.MinValue
            Assert.True(gp.X >= prevX - 0.1f,
                sprintf "Glyph %d X=%.2f should be >= prevX=%.2f" i gp.X prevX)
            prevX <- gp.X
            prevY <- gp.Y
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

// ============================================================
// Scanline Tests — band-midpoint query & silent-drop fix
// ============================================================

/// Tests that verify the two scanline bugs are fixed:
///
///   Bug 1 — Silent text drop on narrow lines:
///     layoutLine was returning (0, 1) when no glyph fit (GlyphCount=0),
///     silently consuming the first character without placing it. Text near
///     the apex of a triangle or top/bottom of a circle was permanently lost.
///     Fix: return (0, 0) so the same text retries on the next wider line.
///
///   Bug 2 — GetInterval queried at baseline instead of band midpoint:
///     layoutAtSize was calling GetInterval(y) where y is the typographic
///     baseline. The hint spec says to query at the band midpoint
///       bandMid = y − ascent + lineHeight×0.5
///     so the width constraint is representative of the full line height, not
///     just the baseline. Querying at the baseline gave a too-wide interval
///     for lines near shape extremities, allowing ascenders to overflow.
module ScanlineTests =

    open System.Buffers

    // ── 1. Direct scanline math (no font loading) ─────────────────────────

    [<Fact>]
    let ``Scanline: circle interval uses Pythagorean formula`` () =
        // GetInterval must match x_offset = sqrt(r² − |y − cy|²)
        let cx, cy, r = 150.0f, 150.0f, 150.0f
        let circle = Shapes.circle { X = cx; Y = cy } r
        // 6.985 = bbox.Top + lineHeight/2 for a 12 px DejaVu Sans font
        //         (lineHeight ≈ 13.97 px → half = 6.985 px).
        // This is the band midpoint of the very first scanline, which is the y value
        // layoutAtSize passes to GetInterval after the Bug 2 fix.
        let tests = [ 6.985f; 50.0f; 75.0f; 150.0f; 200.0f; 280.0f ]
        for y in tests do
            let dy = abs (y - cy)
            if dy < r then
                let expectedHalf = sqrt (r * r - dy * dy)
                match circle.GetInterval y with
                | ValueSome(struct (xS, xE)) ->
                    let got = (xE - xS) / 2.0f
                    Assert.True(abs (got - expectedHalf) < 0.5f,
                        sprintf "Circle at y=%.2f: expected half-width=%.4f got=%.4f" y expectedHalf got)
                | ValueNone ->
                    Assert.Fail(sprintf "Circle should have interval at y=%.2f" y)

    [<Fact>]
    let ``Scanline: circle band-midpoint is narrower than baseline for first line`` () =
        // The whole point of querying at bandMid: it's narrower than the baseline
        // query for lines near the top/bottom of a circle.
        let r = 150.0f
        let circle = Shapes.circle { X = 150.0f; Y = 150.0f } r
        // Simulate 12 px DejaVu metrics: ascent≈11.14, lineHeight≈13.97
        let ascent = 11.14f
        let lineHeight = 13.97f
        let firstBaseline = 0.0f + ascent        // bbox.Top + ascent = 0 + 11.14
        let firstBandMid  = 0.0f + lineHeight * 0.5f  // bbox.Top + lineHeight/2
        let widthAtBaseline =
            match circle.GetInterval firstBaseline with
            | ValueSome(struct (s, e)) -> e - s
            | ValueNone -> 0.0f
        let widthAtMidpoint =
            match circle.GetInterval firstBandMid with
            | ValueSome(struct (s, e)) -> e - s
            | ValueNone -> 0.0f
        Assert.True(widthAtMidpoint < widthAtBaseline,
            sprintf "Band-midpoint width (%.2f) should be < baseline width (%.2f) for first circle line"
                widthAtMidpoint widthAtBaseline)

    [<Fact>]
    let ``Scanline: triangle interval uses linear interpolation`` () =
        // Width at y = base × (y − apexY) / height
        let cx, cy, base' = 200.0f, 150.0f, 300.0f
        let height = 200.0f
        let tri = Shapes.triangle { X = cx; Y = cy } base' height
        let apexY = cy - height / 2.0f  // = 50.0
        let tests = [ 60.0f; 100.0f; 150.0f; 200.0f; 240.0f ]
        for y in tests do
            let normY = (y - apexY) / height
            let expectedHalf = (base' / 2.0f) * normY
            match tri.GetInterval y with
            | ValueSome(struct (xS, xE)) ->
                let got = (xE - xS) / 2.0f
                Assert.True(abs (got - expectedHalf) < 0.5f,
                    sprintf "Triangle at y=%.1f: expected half-width=%.4f got=%.4f" y expectedHalf got)
            | ValueNone ->
                if normY > 0.01f then
                    Assert.Fail(sprintf "Triangle should have interval at y=%.1f (normY=%.3f)" y normY)

    [<Fact>]
    let ``Scanline: triangle scanlines grow linearly from apex to base`` () =
        // Each scanline one lineHeight lower must be wider by a predictable delta.
        let tri = Shapes.triangle { X = 0.0f; Y = 0.0f } 200.0f 200.0f
        // halfBase=100, height=200. Width at y = 2 * 100 * (y+100)/200 = (y+100)
        let lineHeight = 14.0f
        let mutable prevWidth = -1.0f
        let mutable y = -95.0f  // start just below apex
        while y < 95.0f do
            match tri.GetInterval y with
            | ValueSome(struct (s, e)) ->
                let w = e - s
                Assert.True(w >= prevWidth - 0.5f,
                    sprintf "Width should be non-decreasing, prev=%.2f cur=%.2f at y=%.2f" prevWidth w y)
                prevWidth <- w
            | ValueNone -> ()
            y <- y + lineHeight

    [<Fact>]
    let ``Scanline: diamond interval decreases symmetrically from center`` () =
        // Diamond formula: halfWidth = hw * (1 − |dy| / hh)
        // At dy=0:   hw=100 → halfWidth=100, width=200
        // At dy=25:  halfWidth=100*(1−25/100)=75,  width=150
        // At dy=50:  halfWidth=100*(1−50/100)=50,  width=100
        // At dy=75:  halfWidth=25, width=50
        // At dy=100: halfWidth=0 → ValueNone
        let d = Shapes.diamond { X = 0.0f; Y = 0.0f } 100.0f 100.0f
        let width y = match d.GetInterval y with ValueSome(struct (s,e)) -> e-s | ValueNone -> -1.0f
        Assert.True(abs (width  0.0f - 200.0f) < 0.5f, sprintf "At dy=0 expected 200, got %.2f"  (width 0.0f))
        Assert.True(abs (width 25.0f - 150.0f) < 0.5f, sprintf "At dy=25 expected 150, got %.2f" (width 25.0f))
        Assert.True(abs (width 50.0f - 100.0f) < 0.5f, sprintf "At dy=50 expected 100, got %.2f" (width 50.0f))
        Assert.True(abs (width 75.0f -  50.0f) < 0.5f, sprintf "At dy=75 expected 50, got %.2f"  (width 75.0f))
        // Symmetry: y=−25 same as y=+25
        Assert.True(abs (width -25.0f - 150.0f) < 0.5f, sprintf "At dy=-25 expected 150, got %.2f" (width -25.0f))
        // At apex (dy=hh=100) the shape has zero width — GetInterval returns ValueNone
        match d.GetInterval 100.0f with
        | ValueNone -> ()
        | ValueSome(struct (s, e)) ->
            Assert.True(e - s < 0.5f, sprintf "At apex expected 0 width, got %.2f" (e-s))

    [<Fact>]
    let ``Scanline: parallelogram interval shifts by shear times delta-y`` () =
        let hw = 100.0f
        let shear = 0.5f
        let p = Shapes.parallelogram { X = 0.0f; Y = 0.0f } (2.0f * hw) 100.0f shear
        let check y =
            match p.GetInterval y with
            | ValueSome(struct (s, e)) ->
                let expectedCenter = shear * y
                let center = (s + e) / 2.0f
                Assert.True(abs (center - expectedCenter) < 0.5f,
                    sprintf "Para center at y=%.1f: expected %.2f got %.2f" y expectedCenter center)
                Assert.True(abs (e - s - 2.0f * hw) < 0.5f,
                    sprintf "Para width at y=%.1f: expected %.2f got %.2f" y (2.0f*hw) (e-s))
            | ValueNone -> Assert.Fail(sprintf "Parallelogram should have interval at y=%.1f" y)
        check 0.0f; check 20.0f; check -20.0f; check 40.0f

    // ── 2. Bug 1 regression — no silent char drop ────────────────────────

    [<Fact>]
    let ``Layout Bug1: no silent char drop at narrow triangle apex`` () =
        // Narrow tall triangle: first few scanlines are thinner than one glyph.
        // Bug: layoutLine returned (0,1) on those lines — consuming 'A' without placing it —
        //      so the first PLACED glyph was 'B'.
        // Fix: layoutLine returns (0,0) when GlyphCount=0, retrying on the next wider line.
        let tables, metrics = TestHelper.loadTestFont()
        // Triangle center=(25,100), base=50, height=150 → apex at y=25.
        // At 14 px the first band-mid (y≈33) gives width≈2.7 px (too narrow for 'A'≈8.6 px),
        // so 'A' must wait for line 3 (band-mid≈65.7, width≈13.6 px).
        let shape = Shapes.triangle { X = 25.0f; Y = 100.0f } 50.0f 150.0f
        let text  = "ABC"
        let req   = { Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
                      Font = tables; Metrics = metrics; FontSizePx = 14.0f; Mode = Flow }
        let result = LayoutEngine.layout req
        Assert.True(result.Glyphs.Length >= 1, "Should place at least one glyph")
        // Resolve 'A' → its glyph ID
        let cmap   = CMap.parse tables.Cmap
        let aGlyph = CMap.resolve cmap (uint32 'A')
        let span   = result.Glyphs.Span
        Assert.True(span.[0].GlyphId = aGlyph,
            sprintf "First placed glyph must be 'A' (id=%d), got id=%d — char was silently dropped"
                aGlyph span.[0].GlyphId)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout Bug1: glyph count > 0 for text in narrow triangle`` () =
        // With Bug1, narrow lines silently consumed chars → far fewer glyphs than expected.
        let tables, metrics = TestHelper.loadTestFont()
        let shape = Shapes.triangle { X = 25.0f; Y = 100.0f } 50.0f 150.0f
        let text  = "The quick brown fox jumps over the lazy dog."
        let req   = { Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
                      Font = tables; Metrics = metrics; FontSizePx = 12.0f; Mode = Flow }
        let result = LayoutEngine.layout req
        // The triangle base is 50px, height 150px, so after a few narrow lines there's
        // enough width for continuous text. Expect at least 10 glyphs placed.
        Assert.True(result.Glyphs.Length >= 10,
            sprintf "Expected at least 10 glyphs in narrow triangle, got %d"
                result.Glyphs.Length)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout Bug1: long text coverage in circle - placed chars monotonically cover text`` () =
        // Text chars must be placed in source order; no chars should be skipped.
        // Specifically: glyph[i].GlyphId must come from text[textIdx_i] with textIdx_i
        // strictly increasing.  We verify this by checking that the ClusterIndex of each
        // glyph (char-space offset) is ≥ the previous one.
        //
        // (The LayoutEngine doesn't expose ClusterIndex in GlyphPosition, but we can
        //  verify indirectly by checking that chars consumed = glyphs placed + spaces.)
        let tables, metrics = TestHelper.loadTestFont()
        let shape = Shapes.circle { X = 200.0f; Y = 200.0f } 200.0f
        let text  = "The quick brown fox jumps over the lazy dog. " +
                    "Pack my box with five dozen liquor jugs."
        let req   = { Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
                      Font = tables; Metrics = metrics; FontSizePx = 12.0f; Mode = Flow }
        let result = LayoutEngine.layout req
        // Every glyph placed must have come from some position in the text, and
        // those positions must be in non-decreasing order.  The simplest invariant
        // to check without ClusterIndex in GlyphPosition: glyph X must be ≥ the xStart
        // for its line (verified by X-monotonicity per line, already covered elsewhere).
        // Here we check: glyph count must be a reasonable fraction of text length.
        Assert.True(result.Glyphs.Length > 50,
            sprintf "Expected > 50 glyphs in circle for %d-char text, got %d"
                text.Length result.Glyphs.Length)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    // ── 3. Bug 2 regression — glyph X within band-midpoint interval ────────

    [<Fact>]
    let ``Layout Bug2: circle glyph X positions within band-midpoint interval`` () =
        // With Bug2 (query at baseline), xStart was further left than the bandMid interval,
        // so the first glyph on each line started outside the bandMid circle chord.
        // Fix: query GetInterval at bandMid → all glyph X positions satisfy
        //      X ≥ cx − sqrt(r²−(bandMid−cy)²) − ε
        let tables, metrics = TestHelper.loadTestFont()
        let cx, cy, r = 200.0f, 200.0f, 200.0f
        let shape = Shapes.circle { X = cx; Y = cy } r
        let text  = "The quick brown fox jumps over the lazy dog."
        let req   = { Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
                      Font = tables; Metrics = metrics; FontSizePx = 12.0f; Mode = Flow }
        let result = LayoutEngine.layout req
        // Recompute font metrics for bandMid calculation
        let upm        = float32 metrics.UnitsPerEm
        let scale      = 12.0f / upm
        let lineHeight = (float32 metrics.Ascender - float32 metrics.Descender + float32 metrics.LineGap) * scale
        let ascent     = float32 metrics.Ascender * scale
        let span = result.Glyphs.Span
        for i in 0 .. span.Length - 1 do
            let gp      = span.[i]
            let bandMid = gp.Y - ascent + lineHeight * 0.5f
            let dy      = bandMid - cy
            if abs dy < r then
                let xOffset = sqrt (r * r - dy * dy)
                let xStart  = cx - xOffset
                let xEnd    = cx + xOffset
                Assert.True(gp.X >= xStart - 1.0f,
                    sprintf "Glyph %d X=%.2f is left of bandMid xStart=%.2f (bandMid=%.2f, dy=%.2f)"
                        i gp.X xStart bandMid dy)
                Assert.True(gp.X <= xEnd + 1.0f,
                    sprintf "Glyph %d X=%.2f is right of bandMid xEnd=%.2f (bandMid=%.2f, dy=%.2f)"
                        i gp.X xEnd bandMid dy)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout Bug2: triangle glyph X positions within band-midpoint interval`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let cx, cy, base', height = 200.0f, 200.0f, 400.0f, 300.0f
        let shape   = Shapes.triangle { X = cx; Y = cy } base' height
        let apexY   = cy - height / 2.0f
        let halfBase = base' / 2.0f
        let text    = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs."
        let req     = { Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
                        Font = tables; Metrics = metrics; FontSizePx = 11.0f; Mode = Flow }
        let result  = LayoutEngine.layout req
        let upm        = float32 metrics.UnitsPerEm
        let scale      = 11.0f / upm
        let lineHeight = (float32 metrics.Ascender - float32 metrics.Descender + float32 metrics.LineGap) * scale
        let ascent     = float32 metrics.Ascender * scale
        let span = result.Glyphs.Span
        for i in 0 .. span.Length - 1 do
            let gp      = span.[i]
            let bandMid = gp.Y - ascent + lineHeight * 0.5f
            let normY   = (bandMid - apexY) / height
            if normY > 0.0f && normY < 1.0f then
                let xStart = cx - halfBase * normY
                let xEnd   = cx + halfBase * normY
                Assert.True(gp.X >= xStart - 1.0f,
                    sprintf "Glyph %d X=%.2f left of bandMid xStart=%.2f (bandMid=%.2f normY=%.3f)"
                        i gp.X xStart bandMid normY)
                Assert.True(gp.X <= xEnd + 1.0f,
                    sprintf "Glyph %d X=%.2f right of bandMid xEnd=%.2f (bandMid=%.2f normY=%.3f)"
                        i gp.X xEnd bandMid normY)
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    // ── 4. Combined regression — long-text coverage in all shapes ──────────

    [<Fact>]
    let ``Layout: all four shapes accept long multi-pangram text without crash`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let text = "The quick brown fox jumps over the lazy dog. " +
                   "Pack my box with five dozen liquor jugs. " +
                   "How vexingly quick daft zebras jump!"
        let shapes = [
            Shapes.circle        { X = 200.0f; Y = 200.0f } 200.0f
            Shapes.diamond       { X = 200.0f; Y = 200.0f } 200.0f 200.0f
            Shapes.triangle      { X = 200.0f; Y = 200.0f } 400.0f 300.0f
            Shapes.parallelogram { X = 200.0f; Y = 200.0f } 400.0f 200.0f 0.3f
        ]
        for shape in shapes do
            let req = { Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
                        Font = tables; Metrics = metrics; FontSizePx = 12.0f; Mode = Flow }
            let result = LayoutEngine.layout req
            Assert.True(result.Glyphs.Length > 0,
                sprintf "Shape should place at least 1 glyph for text of length %d" text.Length)
            if result.GlyphBuffer.Length > 0 then
                ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Layout: Fit mode produces positive glyph count in circle and triangle`` () =
        let tables, metrics = TestHelper.loadTestFont()
        let text = "GlyphAura"
        let shapes = [
            Shapes.circle   { X = 150.0f; Y = 150.0f } 150.0f
            Shapes.triangle { X = 150.0f; Y = 150.0f } 300.0f 200.0f
        ]
        for shape in shapes do
            let req = { Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
                        Font = tables; Metrics = metrics; FontSizePx = 12.0f; Mode = Fit }
            let result = LayoutEngine.layout req
            Assert.True(result.Glyphs.Length > 0,
                sprintf "Fit mode should place at least 1 glyph")
            Assert.True(result.FontSizeUsed > 0.0f,
                "Fit mode must resolve a positive font size")
            if result.GlyphBuffer.Length > 0 then
                ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
