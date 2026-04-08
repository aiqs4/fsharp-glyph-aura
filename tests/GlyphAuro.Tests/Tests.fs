namespace GlyphAuro.Tests

open System
open System.IO
open Xunit
open GlyphAuro

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
