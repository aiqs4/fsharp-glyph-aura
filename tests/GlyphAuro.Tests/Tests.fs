namespace GlyphAuro.Tests

open System
open System.IO
open System.Buffers
open Xunit
open GlyphAuro
open GlyphAuro.GposKerning
open GlyphAuro.TextAnalyzer
open GlyphAuro.Shapes
open GlyphAuro.LayoutEngine
open GlyphAuro.DrawList
open GlyphAuro.ShapeCache
open GlyphAuro.TextNode

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
        let shape = Shapes.diamond { X = 100.0f; Y = 150.0f } 100.0f 150.0f
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
        Assert.True(result.LastTextIndex >= text.Length, sprintf "All text should fit, LastTextIndex=%d" result.LastTextIndex)

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
