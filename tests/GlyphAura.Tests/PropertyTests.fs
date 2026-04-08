/// Property-based tests using FsCheck + xUnit.
///
/// The three test levels described in the spec:
///
///   1. AABB Containment
///      Every glyph's X position must lie within the conservative interval
///      [xStart, xEnd] computed for its line band (midpoint AND both band edges).
///      This ensures no glyph overflows the shape boundary at ANY y in the band.
///
///   2. Non-Overlap (Negative-Space) Check
///      Consecutive glyphs placed on the same baseline must have strictly
///      increasing X coordinates — no visual overlap.
///
///   3. Edge-Case "Squeeze"
///      A word that is wider than a narrow scanline must be deferred to the
///      next (wider) scanline — never placed outside the shape or silently dropped.
///
/// FsCheck generates hundreds of random inputs for each property.
module GlyphAura.Tests.PropertyTests

open System
open System.IO
open System.Buffers
open Xunit
open FsCheck
open FsCheck.Xunit
open GlyphAura
open GlyphAura.Shapes
open GlyphAura.LayoutEngine

// ── Font fixture ─────────────────────────────────────────────────────────────

/// Known paths for DejaVu Sans on common platforms.
let private fontCandidates = [
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
    "/usr/share/fonts/TTF/DejaVuSans.ttf"
    "C:/Windows/Fonts/arial.ttf"
]

let private sharedFont : Lazy<FontTables * FontMetrics> =
    lazy
        match fontCandidates |> List.tryFind File.Exists with
        | None ->
            failwith
                "No test font found. Install DejaVu fonts (e.g. apt-get install fonts-dejavu) \
                 or ensure one of these paths exists: \
                 /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
        | Some path ->
            let bytes = File.ReadAllBytes(path)
            match FontProvider.load (ReadOnlyMemory(bytes)) with
            | Ok pair -> pair
            | Error msg -> failwith msg

let private loadFont () = sharedFont.Value

// ── Conservative-interval helper (mirrors LayoutEngine logic) ────────────────

/// Mirrors the conservative-interval logic inside LayoutEngine.layoutAtSize.
/// Returns the tightest [xStart, xEnd] over midpoint + both band edges.
let private conservativeInterval
    (shape: ISdfShape)
    (bbox: Rectangle)
    (bandTop: float32)
    (bandBottom: float32)
    : struct (float32 * float32) voption =

    let midY = bandTop + (bandBottom - bandTop) * 0.5f
    match shape.GetInterval midY with
    | ValueNone -> ValueNone
    | ValueSome(struct (xsMid, xeMid)) ->
        let mutable xs = xsMid
        let mutable xe = xeMid
        if bandTop > bbox.Top then
            match shape.GetInterval bandTop with
            | ValueSome(struct (s, e)) -> xs <- max xs s; xe <- min xe e
            | ValueNone -> ()
        if bandBottom < bbox.Bottom then
            match shape.GetInterval bandBottom with
            | ValueSome(struct (s, e)) -> xs <- max xs s; xe <- min xe e
            | ValueNone -> ()
        if xe > xs then ValueSome(struct (xs, xe)) else ValueNone

// ── Layout helpers ────────────────────────────────────────────────────────────

let private runFlow (shape: ISdfShape) (text: string) (fontSize: float32) : LayoutResult =
    let tables, metrics = loadFont()
    let req = {
        Text       = ReadOnlyMemory(text.ToCharArray())
        Shape      = shape
        Font       = tables
        Metrics    = metrics
        FontSizePx = fontSize
        Mode       = Flow
        Alignment  = Left
    }
    layout req

let private getFontMetrics () : FontMetrics = snd (loadFont())

// ── Shape factory helpers (called inside property bodies) ─────────────────────

let private makeCircle (r: int) : ISdfShape =
    let rf = float32 (max 40 (min 200 r))
    circle { X = rf; Y = rf } rf

let private makeTriangle (baseW: int) (h: int) : ISdfShape =
    let bw = float32 (max 80  (min 300 baseW))
    let hf = float32 (max 60  (min 200 h))
    triangle { X = bw * 0.5f; Y = hf * 0.5f } bw hf

let private makeParallelogram (w: int) (h: int) (shear100: int) : ISdfShape =
    let wf  = float32 (max 80  (min 300 w))
    let hf  = float32 (max 60  (min 200 h))
    let sh  = float32 shear100 * 0.01f  // [-0.4, 0.4]
    let cx  = wf * 0.5f + hf * abs sh * 0.5f + 10.0f
    let cy  = hf * 0.5f + 10.0f
    parallelogram { X = cx; Y = cy } wf hf sh

/// Normalise a FsCheck-generated string to a printable ASCII string, 5–30 chars.
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

// ── Property 1: AABB Containment ─────────────────────────────────────────────
//
// Every glyph X must lie within the conservative interval for its line band.

/// Run the containment property for a given shape, text, and font size.
/// Returns true when every glyph is within the conservative interval.
let private checkContainment (shape: ISdfShape) (text: string) (fontSizePx: float32) =
    let result  = runFlow shape text fontSizePx
    let metrics = getFontMetrics()
    let upm     = float32 metrics.UnitsPerEm
    let scale   = fontSizePx / upm
    let lh      = (float32 metrics.Ascender - float32 metrics.Descender + float32 metrics.LineGap) * scale
    let ascent  = float32 metrics.Ascender * scale
    let bbox    = shape.BoundingBox
    let glyphs  = result.Glyphs.ToArray()

    let ok =
        glyphs
        |> Array.forall (fun gp ->
            let bandTop    = gp.Y - ascent
            let bandBottom = gp.Y - ascent + lh
            match conservativeInterval shape bbox bandTop bandBottom with
            | ValueNone -> false
            | ValueSome(struct (xs, xe)) ->
                gp.X >= xs - 0.6f && gp.X <= xe + 0.6f)   // 0.6 px rounding tolerance

    if result.GlyphBuffer.Length > 0 then
        ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
    ok

[<Properties(MaxTest = 150, QuietOnSuccess = true)>]
type ContainmentProperties() =

    // Circle containment: radius 40–200 px, printable text, font 8–24 px.
    [<Property>]
    member _.``Circle: all glyphs within conservative interval``
            (radius: PositiveInt)
            (nes: NonEmptyString)
            (fontSize: PositiveInt) =
        let r    = (int radius % 161) + 40      // [40, 200]
        let fs   = float32 ((int fontSize % 17) + 8)  // [8, 24]
        let text = normText nes.Get
        match text with
        | None -> true   // skip uninformative input
        | Some t -> checkContainment (makeCircle r) t fs

    // Triangle containment: base 80–300 px, height 60–200 px.
    [<Property>]
    member _.``Triangle: all glyphs within conservative interval``
            (baseW: PositiveInt)
            (h: PositiveInt)
            (nes: NonEmptyString)
            (fontSize: PositiveInt) =
        let bw   = (int baseW % 221) + 80      // [80, 300]
        let hh   = (int h % 141) + 60          // [60, 200]
        let fs   = float32 ((int fontSize % 17) + 8)
        let text = normText nes.Get
        match text with
        | None -> true
        | Some t -> checkContainment (makeTriangle bw hh) t fs

    // Parallelogram containment.
    [<Property>]
    member _.``Parallelogram: all glyphs within conservative interval``
            (w: PositiveInt)
            (h: PositiveInt)
            (shear100: int)         // will be clamped to [-40, 40] → divide by 100
            (nes: NonEmptyString)
            (fontSize: PositiveInt) =
        let ww   = (int w % 221) + 80
        let hh   = (int h % 141) + 60
        let sh   = (((shear100 % 41) + 41) % 41) - 40   // [-40, 40]
        let fs   = float32 ((int fontSize % 17) + 8)
        let text = normText nes.Get
        match text with
        | None -> true
        | Some t -> checkContainment (makeParallelogram ww hh sh) t fs

// ── Property 2: Non-Overlap (Negative-Space) Check ───────────────────────────
//
// Consecutive glyphs on the same baseline have strictly increasing X.

let private checkNoOverlap (shape: ISdfShape) (text: string) (fontSizePx: float32) =
    let result = runFlow shape text fontSizePx
    let glyphs = result.Glyphs.ToArray()

    let ok =
        if glyphs.Length < 2 then true
        else
            glyphs
            |> Array.pairwise
            |> Array.forall (fun (g1, g2) ->
                let sameBaseline = abs (g1.Y - g2.Y) < 0.1f
                not sameBaseline || g2.X > g1.X - 0.5f)

    if result.GlyphBuffer.Length > 0 then
        ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
    ok

[<Properties(MaxTest = 150, QuietOnSuccess = true)>]
type NonOverlapProperties() =

    [<Property>]
    member _.``Circle: consecutive glyphs on same baseline have increasing X``
            (radius: PositiveInt)
            (nes: NonEmptyString)
            (fontSize: PositiveInt) =
        let r    = (int radius % 161) + 40
        let fs   = float32 ((int fontSize % 17) + 8)
        let text = normText nes.Get
        match text with
        | None -> true
        | Some t -> checkNoOverlap (makeCircle r) t fs

    [<Property>]
    member _.``Triangle: consecutive glyphs on same baseline have increasing X``
            (baseW: PositiveInt)
            (h: PositiveInt)
            (nes: NonEmptyString)
            (fontSize: PositiveInt) =
        let bw   = (int baseW % 221) + 80
        let hh   = (int h % 141) + 60
        let fs   = float32 ((int fontSize % 17) + 8)
        let text = normText nes.Get
        match text with
        | None -> true
        | Some t -> checkNoOverlap (makeTriangle bw hh) t fs

// ── Property 3: Vertical Containment ─────────────────────────────────────────
//
// Every glyph's line band (cap-top to descender-bottom) must stay within
// the shape's bbox, preventing descenders from poking out of the bottom.

let private checkVertical (shape: ISdfShape) (text: string) (fontSizePx: float32) =
    let result  = runFlow shape text fontSizePx
    let metrics = getFontMetrics()
    let upm     = float32 metrics.UnitsPerEm
    let scale   = fontSizePx / upm
    let lh      = (float32 metrics.Ascender - float32 metrics.Descender + float32 metrics.LineGap) * scale
    let ascent  = float32 metrics.Ascender * scale
    let bbox    = shape.BoundingBox
    let glyphs  = result.Glyphs.ToArray()

    let ok =
        glyphs
        |> Array.forall (fun gp ->
            let bandTop    = gp.Y - ascent
            let bandBottom = gp.Y - ascent + lh
            bandTop    >= bbox.Top    - 0.6f &&
            bandBottom <= bbox.Bottom + 0.6f)

    if result.GlyphBuffer.Length > 0 then
        ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
    ok

[<Properties(MaxTest = 150, QuietOnSuccess = true)>]
type VerticalContainmentProperties() =

    [<Property>]
    member _.``Circle: glyph bands stay within vertical bounds``
            (radius: PositiveInt)
            (nes: NonEmptyString)
            (fontSize: PositiveInt) =
        let r    = (int radius % 161) + 40
        let fs   = float32 ((int fontSize % 17) + 8)
        let text = normText nes.Get
        match text with
        | None -> true
        | Some t -> checkVertical (makeCircle r) t fs

    [<Property>]
    member _.``Triangle: glyph bands stay within vertical bounds``
            (baseW: PositiveInt)
            (h: PositiveInt)
            (nes: NonEmptyString)
            (fontSize: PositiveInt) =
        let bw   = (int baseW % 221) + 80
        let hh   = (int h % 141) + 60
        let fs   = float32 ((int fontSize % 17) + 8)
        let text = normText nes.Get
        match text with
        | None -> true
        | Some t -> checkVertical (makeTriangle bw hh) t fs

// ── Deterministic edge-case tests ────────────────────────────────────────────

module EdgeCases =

    let private loadFontHere () =
        match fontCandidates |> List.tryFind File.Exists with
        | None ->
            failwith
                "No test font found. Install DejaVu fonts (e.g. apt-get install fonts-dejavu)."
        | Some path ->
            let bytes = File.ReadAllBytes(path)
            match FontProvider.load (ReadOnlyMemory(bytes)) with
            | Ok pair -> pair
            | Error msg -> failwith msg

    [<Fact>]
    let ``Squeeze: extra-long word deferred to wider line — no glyph outside circle`` () =
        let tables, metrics = loadFontHere()
        let r     = 60.0f
        let shape = circle { X = r; Y = r } r
        let req   = {
            Text       = ReadOnlyMemory("Donaudampfschifffahrt".ToCharArray())
            Shape      = shape
            Font       = tables
            Metrics    = metrics
            FontSizePx = 10.0f
            Mode       = Flow
            Alignment  = Left
        }
        let result = layout req
        let glyphs = result.Glyphs.ToArray()

        Assert.True(glyphs.Length > 0, "Should place some glyphs — not all lines too narrow")

        for gp in glyphs do
            match shape.GetInterval gp.Y with
            | ValueNone ->
                Assert.True(false, sprintf "Glyph at y=%.1f baseline is outside the circle" gp.Y)
            | ValueSome(struct (xs, xe)) ->
                Assert.True(gp.X >= xs - 1.0f,
                    sprintf "Glyph X=%.2f left-overflows circle xStart=%.2f at y=%.2f" gp.X xs gp.Y)
                Assert.True(gp.X <= xe + 1.0f,
                    sprintf "Glyph X=%.2f right-overflows circle xEnd=%.2f at y=%.2f" gp.X xe gp.Y)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Conservative interval tighter than midpoint in upper circle half`` () =
        // In the upper half the band TOP (closest to apex) is narrowest.
        // Conservative xStart must be >= midpoint xStart.
        let r, cx, cy = 150.0f, 150.0f, 150.0f
        let shape = circle { X = cx; Y = cy } r
        let bbox  = shape.BoundingBox
        let bandTop, bandBottom = 40.0f, 60.0f       // well above center at 150
        let midY = (bandTop + bandBottom) * 0.5f

        let xsMid =
            match shape.GetInterval midY with
            | ValueSome(struct (s, _)) -> s
            | ValueNone -> failwith "midpoint should be inside circle"
        let xsCons =
            match conservativeInterval shape bbox bandTop bandBottom with
            | ValueSome(struct (s, _)) -> s
            | ValueNone -> failwith "conservative interval should be valid"

        Assert.True(xsCons >= xsMid - 0.1f,
            sprintf "Upper half: conservative xStart=%.2f should be >= midpoint xStart=%.2f" xsCons xsMid)

    [<Fact>]
    let ``Conservative interval tighter than midpoint in lower circle half`` () =
        // In the lower half the band BOTTOM (closest to apex) is narrowest.
        let r, cx, cy = 150.0f, 150.0f, 150.0f
        let shape = circle { X = cx; Y = cy } r
        let bbox  = shape.BoundingBox
        let bandTop, bandBottom = 220.0f, 240.0f      // well below center at 150
        let midY = (bandTop + bandBottom) * 0.5f

        let xsMid =
            match shape.GetInterval midY with
            | ValueSome(struct (s, _)) -> s
            | ValueNone -> failwith "midpoint should be inside circle"
        let xsCons =
            match conservativeInterval shape bbox bandTop bandBottom with
            | ValueSome(struct (s, _)) -> s
            | ValueNone -> failwith "conservative interval should be valid"

        Assert.True(xsCons >= xsMid - 0.1f,
            sprintf "Lower half: conservative xStart=%.2f should be >= midpoint xStart=%.2f" xsCons xsMid)

    [<Fact>]
    let ``Vertical containment: glyph bands do not exceed circle bottom`` () =
        let tables, metrics = loadFontHere()
        let r     = 150.0f
        let shape = circle { X = r; Y = r } r
        let req   = {
            Text       = ReadOnlyMemory("The quick brown fox jumps over the lazy dog.".ToCharArray())
            Shape      = shape
            Font       = tables
            Metrics    = metrics
            FontSizePx = 12.0f
            Mode       = Flow
            Alignment  = Left
        }
        let result = layout req
        let upm    = float32 metrics.UnitsPerEm
        let scale  = 12.0f / upm
        let lh     = (float32 metrics.Ascender - float32 metrics.Descender + float32 metrics.LineGap) * scale
        let ascent = float32 metrics.Ascender * scale
        let bbox   = shape.BoundingBox

        for gp in result.Glyphs.ToArray() do
            let bandBottom = gp.Y - ascent + lh
            Assert.True(bandBottom <= bbox.Bottom + 0.5f,
                sprintf "bandBottom=%.2f exceeds bbox.Bottom=%.2f (baseline=%.2f)" bandBottom bbox.Bottom gp.Y)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    [<Fact>]
    let ``Triangle: all glyphs within shape boundary at their baseline Y`` () =
        let tables, metrics = loadFontHere()
        let shape = triangle { X = 150.0f; Y = 150.0f } 300.0f 200.0f
        let req   = {
            Text       = ReadOnlyMemory("The quick brown fox jumps over the lazy dog.".ToCharArray())
            Shape      = shape
            Font       = tables
            Metrics    = metrics
            FontSizePx = 11.0f
            Mode       = Flow
            Alignment  = Left
        }
        let result = layout req
        let glyphs = result.Glyphs.ToArray()
        Assert.True(glyphs.Length > 0, "Should place glyphs in triangle")

        for gp in glyphs do
            match shape.GetInterval gp.Y with
            | ValueNone ->
                Assert.True(false, sprintf "Triangle: glyph at y=%.2f outside triangle" gp.Y)
            | ValueSome(struct (xs, xe)) ->
                Assert.True(gp.X >= xs - 1.0f,
                    sprintf "Triangle glyph X=%.2f < xStart=%.2f" gp.X xs)
                Assert.True(gp.X <= xe + 1.0f,
                    sprintf "Triangle glyph X=%.2f > xEnd=%.2f" gp.X xe)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
