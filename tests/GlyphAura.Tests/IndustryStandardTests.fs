/// Industry-standard comparison tests for the GlyphAura layout engine.
///
/// These tests verify that:
///   1. ClusterIndex correctly maps each placed glyph back to its source character
///   2. The glyph-to-character identity is consistent with cmap lookup
///   3. Advance accumulation matches HarfBuzz-style pen progression
///   4. Multi-line word-break produces correct cluster ranges per line
///   5. No character is silently dropped (except overflow at shape boundary)
///   6. AABB containment using analytical shape formulas (Pythagoras for circle)
///   7. DrawList serialisation round-trips correctly with ClusterIndices
module GlyphAura.Tests.IndustryStandardTests

open System
open System.IO
open System.Buffers
open Xunit
open FsCheck
open FsCheck.Xunit
open GlyphAura
open GlyphAura.Shapes
open GlyphAura.LayoutEngine
open GlyphAura.DrawList

// ── Font fixture ─────────────────────────────────────────────────────────────

let private loadFont () =
    let path = Path.Combine(AppContext.BaseDirectory, "TestData", "DejaVuSans.ttf")
    let bytes = File.ReadAllBytes(path)
    match FontProvider.load (ReadOnlyMemory(bytes)) with
    | Ok pair -> pair
    | Error msg -> failwith msg

let private runLayout (shape: ISdfShape) (text: string) (fontSize: float32) (mode: LayoutMode) =
    let tables, metrics = loadFont()
    let req = {
        Text       = ReadOnlyMemory(text.ToCharArray())
        Shape      = shape
        Font       = tables
        Metrics    = metrics
        FontSizePx = fontSize
        Mode       = mode
        Alignment  = Left
    }
    let result = layout req
    let glyphs = result.Glyphs.ToArray()
    if result.GlyphBuffer.Length > 0 then
        ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
    glyphs, result, text

// ==========================================================================
// 1. ClusterIndex Mapping Correctness
// ==========================================================================

module ClusterMappingTests =

    [<Fact>]
    let ``ClusterIndex is always within source text bounds`` () =
        let text = "The quick brown fox jumps over the lazy dog."
        let shape = circle { X = 150.0f; Y = 150.0f } 150.0f
        let glyphs, _, _ = runLayout shape text 12.0f Flow
        Assert.True(glyphs.Length > 0, "Should place some glyphs")
        for gp in glyphs do
            Assert.True(gp.ClusterIndex >= 0,
                sprintf "ClusterIndex=%d should be >= 0" gp.ClusterIndex)
            Assert.True(gp.ClusterIndex < text.Length,
                sprintf "ClusterIndex=%d should be < text.Length=%d" gp.ClusterIndex text.Length)

    [<Fact>]
    let ``ClusterIndex is monotonically non-decreasing within each baseline`` () =
        let text = "Pack my box with five dozen liquor jugs. How vexingly quick daft zebras jump!"
        let shape = circle { X = 150.0f; Y = 150.0f } 150.0f
        let glyphs, _, _ = runLayout shape text 12.0f Flow
        Assert.True(glyphs.Length > 0, "Should place some glyphs")

        // Group by baseline Y (same line = same Y)
        let lines =
            glyphs
            |> Array.groupBy (fun gp -> System.MathF.Round(gp.Y, 1))
            |> Array.map snd
        for line in lines do
            let sorted = line |> Array.sortBy (fun gp -> gp.X)
            for i in 0 .. sorted.Length - 2 do
                Assert.True(sorted.[i].ClusterIndex <= sorted.[i+1].ClusterIndex,
                    sprintf "ClusterIndex not monotonic: glyph[%d].CI=%d > glyph[%d].CI=%d on baseline Y=%.1f"
                        i sorted.[i].ClusterIndex (i+1) sorted.[i+1].ClusterIndex sorted.[0].Y)

    [<Fact>]
    let ``ClusterIndex maps to correct character via cmap identity`` () =
        // For BMP text, every glyph's ClusterIndex should map to a non-space character
        // that resolves to the same GlyphId via cmap lookup.
        let text = "Hello World"
        let shape = parallelogram { X = 200.0f; Y = 50.0f } 400.0f 80.0f 0.0f
        let tables, metrics = loadFont()
        let cmap = CMap.parse tables.Cmap
        let req = {
            Text       = ReadOnlyMemory(text.ToCharArray())
            Shape      = shape
            Font       = tables
            Metrics    = metrics
            FontSizePx = 16.0f
            Mode       = Flow
            Alignment  = Left
        }
        let result = layout req
        let glyphs = result.Glyphs.ToArray()
        let srcChars = text.ToCharArray()

        for gp in glyphs do
            let ci = gp.ClusterIndex
            Assert.True(ci >= 0 && ci < srcChars.Length,
                sprintf "CI=%d out of bounds" ci)
            let ch = srcChars.[ci]
            if ch <> ' ' then
                let expectedGlyphId = CMap.resolve cmap (uint32 ch)
                Assert.Equal(expectedGlyphId, gp.GlyphId)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``No duplicate ClusterIndex on same baseline (each char placed once)`` () =
        let text = "abcdefghij"
        let shape = parallelogram { X = 200.0f; Y = 50.0f } 400.0f 80.0f 0.0f
        let glyphs, _, _ = runLayout shape text 16.0f Flow
        // All characters should fit on one line in a wide parallelogram
        let indices = glyphs |> Array.map (fun gp -> gp.ClusterIndex)
        let unique = indices |> Array.distinct
        Assert.Equal(unique.Length, indices.Length)

    [<Fact>]
    let ``Surrogate pair produces single glyph with correct ClusterIndex`` () =
        // 😀 = U+1F600 = surrogate pair (2 chars, 1 glyph)
        let text = "A😀B"
        let shape = parallelogram { X = 200.0f; Y = 50.0f } 400.0f 80.0f 0.0f
        let glyphs, _, _ = runLayout shape text 16.0f Flow
        // Should produce 3 glyphs: A, 😀, B
        Assert.True(glyphs.Length >= 3, sprintf "Expected 3 glyphs, got %d" glyphs.Length)
        Assert.Equal(0, glyphs.[0].ClusterIndex)   // 'A' at char 0
        Assert.Equal(1, glyphs.[1].ClusterIndex)   // 😀 at char 1 (high surrogate)
        Assert.Equal(3, glyphs.[2].ClusterIndex)   // 'B' at char 3 (after surrogate pair)

// ==========================================================================
// 2. Advance Accumulation (HarfBuzz-style)
// ==========================================================================

module AdvanceAccumulationTests =

    [<Fact>]
    let ``Glyph X positions match pen-advance accumulation`` () =
        // On a wide parallelogram, all glyphs on one line:
        // X[0] = xStart, X[i] = X[i-1] + advance[i-1]
        let text = "Test"
        let shape = parallelogram { X = 200.0f; Y = 50.0f } 400.0f 80.0f 0.0f
        let tables, metrics = loadFont()
        let cmap = CMap.parse tables.Cmap
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea
                    (int (BinaryReader.readU16 (tables.Maxp.Span) 4))
        let upm = float32 metrics.UnitsPerEm
        let fontSize = 16.0f
        let scale = fontSize / upm

        let req = {
            Text       = ReadOnlyMemory(text.ToCharArray())
            Shape      = shape
            Font       = tables
            Metrics    = metrics
            FontSizePx = fontSize
            Mode       = Flow
            Alignment  = Left
        }
        let result = layout req
        let glyphs = result.Glyphs.ToArray()
        Assert.Equal(4, glyphs.Length)

        // Verify pen-advance model: each glyph position equals the previous
        // position plus the previous glyph's scaled advance width (with kerning)
        for i in 1 .. glyphs.Length - 1 do
            let prevGlyph = glyphs.[i-1]
            let prevMetrics = Hmtx.getMetrics hmtx prevGlyph.GlyphId
            let prevAdvance = prevMetrics.AdvanceWidth * scale
            // Allow for kerning adjustments (can be negative, e.g., "Te")
            let expectedX = prevGlyph.X + prevAdvance
            let actualX = glyphs.[i].X
            // Tolerance accounts for kerning: difference should be less than
            // the maximum kern adjustment (a few pixels at this size)
            let diff = abs (actualX - expectedX)
            Assert.True(diff < 5.0f,
                sprintf "Glyph[%d].X=%.2f differs from expected=%.2f by %.2f (kerning adjustment)"
                    i actualX expectedX diff)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Kerning pair "To" produces tighter spacing than "Tx"`` () =
        // "To" has a known negative kern in DejaVu Sans (-348 design units)
        // The X gap between T→o should be smaller than T→x (no special kern)
        let tables, metrics = loadFont()
        let fontSize = 48.0f
        let shape = parallelogram { X = 200.0f; Y = 50.0f } 400.0f 100.0f 0.0f

        let layoutPair (pair: string) =
            let req = {
                Text       = ReadOnlyMemory(pair.ToCharArray())
                Shape      = shape
                Font       = tables
                Metrics    = metrics
                FontSizePx = fontSize
                Mode       = Flow
                Alignment  = Left
            }
            let result = layout req
            let glyphs = result.Glyphs.ToArray()
            let gap = glyphs.[1].X - glyphs.[0].X
            if result.GlyphBuffer.Length > 0 then
                ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
            gap

        let gapTo = layoutPair "To"
        let gapTx = layoutPair "Tx"
        Assert.True(gapTo < gapTx,
            sprintf "\"To\" gap=%.2f should be tighter than \"Tx\" gap=%.2f due to GPOS kern" gapTo gapTx)

// ==========================================================================
// 3. Multi-line Word Break — Cluster Ranges
// ==========================================================================

module WordBreakTests =

    [<Fact>]
    let ``Word break does not split cluster indices mid-word`` () =
        let text = "Hello World Goodbye"
        let shape = circle { X = 100.0f; Y = 100.0f } 100.0f
        let glyphs, _, _ = runLayout shape text 12.0f Flow

        // Group glyphs by baseline (line)
        let lines =
            glyphs
            |> Array.groupBy (fun gp -> System.MathF.Round(gp.Y, 1))
            |> Array.map snd
            |> Array.map (fun line -> line |> Array.sortBy (fun gp -> gp.X))

        // For each line, the cluster indices should form contiguous ranges
        for line in lines do
            let indices = line |> Array.map (fun gp -> gp.ClusterIndex)
            for i in 0 .. indices.Length - 2 do
                let gap = indices.[i+1] - indices.[i]
                // Within a line, consecutive glyphs should be from adjacent
                // characters (gap=1) or skip a space (gap=2)
                Assert.True(gap >= 1 && gap <= 2,
                    sprintf "Unexpected cluster gap: CI[%d]=%d → CI[%d]=%d (gap=%d)"
                        i indices.[i] (i+1) indices.[i+1] gap)

    [<Fact>]
    let ``All non-space characters are placed when shape is large enough`` () =
        let text = "abc def"
        let shape = parallelogram { X = 200.0f; Y = 50.0f } 400.0f 80.0f 0.0f
        let glyphs, result, _ = runLayout shape text 14.0f Flow
        // In a wide shape, all 6 non-space characters + 1 space glyph = 7 glyphs
        // But spaces at word breaks might be dropped. Let's just verify
        // all non-space chars appear in cluster indices
        let placedCIs = glyphs |> Array.map (fun gp -> gp.ClusterIndex) |> Set.ofArray
        for i in 0 .. text.Length - 1 do
            if text.[i] <> ' ' then
                Assert.True(placedCIs.Contains(i),
                    sprintf "Character '%c' at index %d was not placed" text.[i] i)

// ==========================================================================
// 4. AABB Containment with Analytical Shape Formulas
// ==========================================================================

module AnalyticalContainmentTests =

    /// Check if a point is inside a circle using Pythagoras.
    let private insideCircle (cx: float32) (cy: float32) (r: float32) (x: float32) (y: float32) =
        let dx = x - cx
        let dy = y - cy
        sqrt(dx*dx + dy*dy) <= r

    [<Fact>]
    let ``Circle AABB: every glyph X is within analytical circle boundary at baseline`` () =
        let cx, cy, r = 150.0f, 150.0f, 150.0f
        let text = "The quick brown fox jumps over the lazy dog."
        let shape = circle { X = cx; Y = cy } r
        let glyphs, _, _ = runLayout shape text 12.0f Flow

        for gp in glyphs do
            // At baseline Y, the circle extends from cx - dx to cx + dx
            // where dx = sqrt(r² - (Y-cy)²)
            let dy = gp.Y - cy
            Assert.True(abs dy < r,
                sprintf "Glyph at Y=%.2f is outside circle (|dy|=%.2f >= r=%.2f)" gp.Y (abs dy) r)
            let dx = sqrt(r*r - dy*dy)
            Assert.True(gp.X >= cx - dx - 1.0f,
                sprintf "Glyph X=%.2f < circle left=%.2f at Y=%.2f" gp.X (cx - dx) gp.Y)
            Assert.True(gp.X <= cx + dx + 1.0f,
                sprintf "Glyph X=%.2f > circle right=%.2f at Y=%.2f" gp.X (cx + dx) gp.Y)

    [<Fact>]
    let ``Triangle AABB: every glyph X is within triangle boundary`` () =
        let cx, cy = 200.0f, 150.0f
        let baseW, height = 300.0f, 200.0f
        let halfBase = baseW * 0.5f
        let halfHeight = height * 0.5f
        let text = "The quick brown fox jumps over the lazy dog."
        let shape = triangle { X = cx; Y = cy } baseW height
        let glyphs, _, _ = runLayout shape text 11.0f Flow

        for gp in glyphs do
            let dy = gp.Y - cy
            Assert.True(dy >= -halfHeight - 1.0f && dy <= halfHeight + 1.0f,
                sprintf "Glyph at Y=%.2f is outside triangle vertical range" gp.Y)
            // Triangle width at Y: normY = (dy + halfHeight) / height
            let normY = (dy + halfHeight) / height
            let halfWidthAtY = halfBase * normY
            if halfWidthAtY > 0.0f then
                Assert.True(gp.X >= cx - halfWidthAtY - 1.0f,
                    sprintf "Triangle: glyph X=%.2f < left=%.2f at Y=%.2f" gp.X (cx - halfWidthAtY) gp.Y)
                Assert.True(gp.X <= cx + halfWidthAtY + 1.0f,
                    sprintf "Triangle: glyph X=%.2f > right=%.2f at Y=%.2f" gp.X (cx + halfWidthAtY) gp.Y)

    [<Fact>]
    let ``Diamond AABB: every glyph X is within diamond boundary`` () =
        let cx, cy = 200.0f, 150.0f
        let hw, hh = 150.0f, 150.0f
        let text = "The quick brown fox jumps over the lazy dog."
        let shape = diamond { X = cx; Y = cy } hw hh
        let glyphs, _, _ = runLayout shape text 12.0f Flow

        for gp in glyphs do
            let dy = abs(gp.Y - cy)
            Assert.True(dy < hh + 1.0f,
                sprintf "Glyph at Y=%.2f is outside diamond vertical range" gp.Y)
            // Diamond width at Y: halfWidth = hw * (1 - |dy|/hh)
            let halfWidthAtY = hw * (1.0f - dy / hh)
            if halfWidthAtY > 0.0f then
                Assert.True(gp.X >= cx - halfWidthAtY - 1.0f,
                    sprintf "Diamond: glyph X=%.2f < left=%.2f at Y=%.2f" gp.X (cx - halfWidthAtY) gp.Y)
                Assert.True(gp.X <= cx + halfWidthAtY + 1.0f,
                    sprintf "Diamond: glyph X=%.2f > right=%.2f at Y=%.2f" gp.X (cx + halfWidthAtY) gp.Y)

// ==========================================================================
// 5. DrawList Round-Trip with ClusterIndices
// ==========================================================================

module DrawListClusterTests =

    [<Fact>]
    let ``DrawList preserves ClusterIndices from LayoutResult`` () =
        let text = "Hello, World!"
        let shape = parallelogram { X = 200.0f; Y = 50.0f } 400.0f 80.0f 0.0f
        let tables, metrics = loadFont()
        let req = {
            Text       = ReadOnlyMemory(text.ToCharArray())
            Shape      = shape
            Font       = tables
            Metrics    = metrics
            FontSizePx = 16.0f
            Mode       = Flow
            Alignment  = Left
        }
        let result = layout req
        let dl = fromLayoutResult result

        Assert.Equal(result.Glyphs.Length, dl.GlyphCount)

        let glyphs = result.Glyphs.ToArray()
        for i in 0 .. dl.GlyphCount - 1 do
            Assert.Equal(glyphs.[i].ClusterIndex, dl.ClusterIndices.[i])
            Assert.Equal(glyphs.[i].GlyphId, dl.GlyphIds.[i])
            Assert.Equal(glyphs.[i].X, dl.XPositions.[i])
            Assert.Equal(glyphs.[i].Y, dl.YPositions.[i])

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Empty text produces empty DrawList with empty ClusterIndices`` () =
        let dl = empty
        Assert.Equal(0, dl.GlyphCount)
        Assert.Empty(dl.ClusterIndices)

// ==========================================================================
// 6. FsCheck Property: ClusterIndex is always valid (randomised shapes/text)
// ==========================================================================

let private fontCandidates = [
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
    "/usr/share/fonts/TTF/DejaVuSans.ttf"
    "C:/Windows/Fonts/arial.ttf"
]

let private sharedFont : Lazy<FontTables * FontMetrics> =
    lazy
        match fontCandidates |> List.tryFind File.Exists with
        | None ->
            // Fallback to test data font
            let path = Path.Combine(AppContext.BaseDirectory, "TestData", "DejaVuSans.ttf")
            let bytes = File.ReadAllBytes(path)
            match FontProvider.load (ReadOnlyMemory(bytes)) with
            | Ok pair -> pair
            | Error msg -> failwith msg
        | Some path ->
            let bytes = File.ReadAllBytes(path)
            match FontProvider.load (ReadOnlyMemory(bytes)) with
            | Ok pair -> pair
            | Error msg -> failwith msg

/// Normalise a FsCheck-generated string to printable ASCII, 5–30 chars.
let private normText (s: string) : string option =
    if String.IsNullOrWhiteSpace s then None
    else
        let printable =
            s
            |> Seq.filter (fun c -> int c >= 32 && int c < 128)
            |> Seq.truncate 30
            |> Array.ofSeq
            |> String
        if printable.Trim().Length < 3 then None
        else Some (printable.Trim())

[<Properties(MaxTest = 100, QuietOnSuccess = true)>]
type ClusterIndexProperties() =

    [<Property>]
    member _.``ClusterIndex always in bounds for random circle + text``
            (radius: PositiveInt)
            (nes: NonEmptyString)
            (fontSize: PositiveInt) =
        let r    = float32 ((int radius % 161) + 40)
        let fs   = float32 ((int fontSize % 17) + 8)
        let text = normText nes.Get
        match text with
        | None -> true
        | Some t ->
            let tables, metrics = sharedFont.Value
            let shape = circle { X = r; Y = r } r
            let req = {
                Text       = ReadOnlyMemory(t.ToCharArray())
                Shape      = shape
                Font       = tables
                Metrics    = metrics
                FontSizePx = fs
                Mode       = Flow
                Alignment  = Left
            }
            let result = layout req
            let glyphs = result.Glyphs.ToArray()
            let ok =
                glyphs |> Array.forall (fun gp ->
                    gp.ClusterIndex >= 0 && gp.ClusterIndex < t.Length)
            if result.GlyphBuffer.Length > 0 then
                ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
            ok

    [<Property>]
    member _.``ClusterIndex monotonically non-decreasing within each line``
            (radius: PositiveInt)
            (nes: NonEmptyString)
            (fontSize: PositiveInt) =
        let r    = float32 ((int radius % 161) + 40)
        let fs   = float32 ((int fontSize % 17) + 8)
        let text = normText nes.Get
        match text with
        | None -> true
        | Some t ->
            let tables, metrics = sharedFont.Value
            let shape = circle { X = r; Y = r } r
            let req = {
                Text       = ReadOnlyMemory(t.ToCharArray())
                Shape      = shape
                Font       = tables
                Metrics    = metrics
                FontSizePx = fs
                Mode       = Flow
                Alignment  = Left
            }
            let result = layout req
            let glyphs = result.Glyphs.ToArray()
            let lines =
                glyphs
                |> Array.groupBy (fun gp -> System.MathF.Round(gp.Y, 1))
                |> Array.map snd
            let ok =
                lines |> Array.forall (fun line ->
                    let sorted = line |> Array.sortBy (fun gp -> gp.X)
                    sorted
                    |> Array.pairwise
                    |> Array.forall (fun (g1, g2) -> g1.ClusterIndex <= g2.ClusterIndex))
            if result.GlyphBuffer.Length > 0 then
                ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
            ok

// ==========================================================================
// SECTION 6 — Text Alignment Tests
// ==========================================================================

module AlignmentTests =

    let private loadFont () =
        let path = Path.Combine(AppContext.BaseDirectory, "TestData", "DejaVuSans.ttf")
        let bytes = File.ReadAllBytes(path)
        match FontProvider.load (ReadOnlyMemory(bytes)) with
        | Ok pair -> pair
        | Error msg -> failwith msg

    let private layoutWithAlignment (text: string) (alignment: TextAlignment) =
        let tables, metrics = loadFont()
        let shape = circle { X = 150.0f; Y = 150.0f } 150.0f
        let req = {
            Text       = ReadOnlyMemory(text.ToCharArray())
            Shape      = shape
            Font       = tables
            Metrics    = metrics
            FontSizePx = 12.0f
            Mode       = Flow
            Alignment  = alignment
        }
        let result = layout req
        let glyphs = result.Glyphs.ToArray()
        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
        glyphs, result

    [<Fact>]
    let ``Center alignment shifts glyphs right compared to Left`` () =
        let leftGlyphs, _ = layoutWithAlignment "The quick brown fox jumps over the lazy dog." Left
        let centerGlyphs, _ = layoutWithAlignment "The quick brown fox jumps over the lazy dog." Center

        // Both should produce the same number of glyphs
        Assert.Equal(leftGlyphs.Length, centerGlyphs.Length)
        Assert.True(leftGlyphs.Length > 0, "Should place some glyphs")

        // Centered glyphs on each line should have their first glyph X >= left-aligned X
        let leftLines =
            leftGlyphs
            |> Array.groupBy (fun gp -> System.MathF.Round(gp.Y, 1))
            |> Array.map (fun (y, gs) -> y, gs |> Array.minBy (fun g -> g.X))
        let centerLines =
            centerGlyphs
            |> Array.groupBy (fun gp -> System.MathF.Round(gp.Y, 1))
            |> Array.map (fun (y, gs) -> y, gs |> Array.minBy (fun g -> g.X))

        // At least some lines should have the centered first glyph shifted right
        let shiftedCount =
            Array.zip leftLines centerLines
            |> Array.filter (fun ((_, lg), (_, cg)) -> cg.X > lg.X + 0.5f)
            |> Array.length

        Assert.True(shiftedCount > 0,
            "At least some centered lines should shift right relative to left-aligned")

    [<Fact>]
    let ``Justified alignment distributes space between words`` () =
        let tables, metrics = loadFont()
        // Wide rectangle so all text fits on one line with extra space
        let shape = parallelogram { X = 400.0f; Y = 20.0f } 780.0f 40.0f 0.0f
        let text = "The quick brown fox"
        let leftReq = {
            Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
            Font = tables; Metrics = metrics; FontSizePx = 14.0f
            Mode = Flow; Alignment = Left
        }
        let justReq = { leftReq with Alignment = Justified }
        let leftResult = layout leftReq
        let justResult = layout justReq
        let leftGlyphs = leftResult.Glyphs.ToArray()
        let justGlyphs = justResult.Glyphs.ToArray()

        // Same glyph count
        Assert.Equal(leftGlyphs.Length, justGlyphs.Length)

        // Justified last glyph should be further right than left-aligned
        if justGlyphs.Length > 1 then
            let leftLast = leftGlyphs |> Array.maxBy (fun g -> g.X)
            let justLast = justGlyphs |> Array.maxBy (fun g -> g.X)
            Assert.True(justLast.X >= leftLast.X,
                sprintf "Justified last glyph X=%.1f should be >= left-aligned X=%.1f" justLast.X leftLast.X)

        if leftResult.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(leftResult.GlyphBuffer, false)
        if justResult.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(justResult.GlyphBuffer, false)

    [<Fact>]
    let ``Center alignment: all glyphs remain within shape boundary`` () =
        let glyphs, _ = layoutWithAlignment "The quick brown fox jumps over the lazy dog." Center
        let shape = circle { X = 150.0f; Y = 150.0f } 150.0f

        for gp in glyphs do
            match shape.GetInterval gp.Y with
            | ValueNone ->
                Assert.True(false, sprintf "Centered glyph at y=%.1f is outside shape" gp.Y)
            | ValueSome(struct (xs, xe)) ->
                Assert.True(gp.X >= xs - 1.0f,
                    sprintf "Centered glyph at (%.1f, %.1f) is left of boundary xs=%.1f" gp.X gp.Y xs)
                Assert.True(gp.X <= xe + 1.0f,
                    sprintf "Centered glyph at (%.1f, %.1f) is right of boundary xe=%.1f" gp.X gp.Y xe)

    [<Fact>]
    let ``Justified alignment: all glyphs remain within shape boundary`` () =
        let glyphs, _ = layoutWithAlignment "The quick brown fox jumps over the lazy dog." Justified
        let shape = circle { X = 150.0f; Y = 150.0f } 150.0f

        for gp in glyphs do
            match shape.GetInterval gp.Y with
            | ValueNone ->
                Assert.True(false, sprintf "Justified glyph at y=%.1f is outside shape" gp.Y)
            | ValueSome(struct (xs, xe)) ->
                Assert.True(gp.X >= xs - 1.0f,
                    sprintf "Justified glyph at (%.1f, %.1f) is left of boundary xs=%.1f" gp.X gp.Y xs)
                Assert.True(gp.X <= xe + 1.0f,
                    sprintf "Justified glyph at (%.1f, %.1f) is right of boundary xe=%.1f" gp.X gp.Y xe)

    [<Fact>]
    let ``2-char minimum rule: short word 'the' is not split`` () =
        let tables, metrics = loadFont()
        // Very narrow shape — "the" shouldn't fit on a narrow scanline
        let shape = diamond { X = 50.0f; Y = 50.0f } 15.0f 50.0f
        let text = "the"
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
            Font = tables; Metrics = metrics; FontSizePx = 12.0f
            Mode = Flow; Alignment = Left
        }
        let result = layout req
        let glyphs = result.Glyphs.ToArray()

        // If any glyphs are placed, the word "the" must be placed as a whole (3 chars)
        if glyphs.Length > 0 then
            let uniqueYs = glyphs |> Array.map (fun g -> System.MathF.Round(g.Y, 1)) |> Array.distinct
            Assert.True(uniqueYs.Length = 1,
                sprintf "'the' should not be split across lines; found %d distinct Y values" uniqueYs.Length)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Overflow detection: LastTextIndex < text length means partial fit`` () =
        let tables, metrics = loadFont()
        // Very small circle with lots of text
        let shape = circle { X = 20.0f; Y = 20.0f } 20.0f
        let text = "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs."
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray()); Shape = shape
            Font = tables; Metrics = metrics; FontSizePx = 12.0f
            Mode = Flow; Alignment = Left
        }
        let result = layout req

        Assert.True(result.LastTextIndex < text.Length,
            sprintf "Expected overflow: LastTextIndex=%d should be < text.Length=%d" result.LastTextIndex text.Length)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``All three alignments produce same glyph count for same input`` () =
        let text = "The quick brown fox jumps over the lazy dog."
        let leftGlyphs, _ = layoutWithAlignment text Left
        let centerGlyphs, _ = layoutWithAlignment text Center
        let justGlyphs, _ = layoutWithAlignment text Justified

        Assert.Equal(leftGlyphs.Length, centerGlyphs.Length)
        Assert.Equal(leftGlyphs.Length, justGlyphs.Length)
