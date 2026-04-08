/// GlyphAura PNG Demo — renders glyph positions and SDF shapes to bitmaps via SkiaSharp.
///
/// Produces raster output that demonstrates:
///   • Pixel-perfect glyph placement from the layout engine
///   • Shape boundary overlay with anti-aliasing
///   • Baseline indicator lines (green dashes)
///   • Cap-height band (translucent)
///   • Kerning quality test at large scale
module GlyphAura.Demo.PngDemo

open System
open System.IO
open System.Buffers
open SkiaSharp
open GlyphAura
open GlyphAura.Shapes
open GlyphAura.LayoutEngine
open GlyphAura.DrawList

// ── Colour palette ─────────────────────────────────────────────────────────

let private colBg        = SKColor(248uy, 249uy, 250uy)
let private colShape     = SKColor(74uy,  144uy, 217uy)
let private colBaseline  = SKColor(39uy,  174uy,  96uy)
let private colGlyph     = SKColor(44uy,   62uy,  80uy)
let private colGlyphTest = SKColor(192uy,  57uy,  43uy)
let private colCapBand   = SKColor(230uy, 126uy,  34uy, 40uy)

// ── Font loading ─────────────────────────────────────────────────────────────

let private loadTypeface () : SKTypeface =
    let candidates = [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
        "/usr/share/fonts/TTF/DejaVuSans.ttf"
        "/System/Library/Fonts/Helvetica.ttf"
        "C:/Windows/Fonts/arial.ttf"
    ]
    candidates
    |> List.tryFind File.Exists
    |> Option.map SKTypeface.FromFile
    |> Option.defaultValue (SKTypeface.FromFamilyName("Arial"))

// ── Drawing helpers ─────────────────────────────────────────────────────────

/// Draw the SDF shape outline as connected polylines on the canvas.
let private drawShapeOutline (canvas: SKCanvas) (shape: ISdfShape) (paint: SKPaint) =
    let bb    = shape.BoundingBox
    let steps = 120
    let leftPts  = ResizeArray<SKPoint>()
    let rightPts = ResizeArray<SKPoint>()
    for step in 0 .. steps do
        let y = bb.Top + float32 step * (bb.Height / float32 steps)
        match shape.GetInterval y with
        | ValueSome(struct (xs, xe)) ->
            leftPts.Add(SKPoint(xs, y))
            rightPts.Add(SKPoint(xe, y))
        | ValueNone -> ()
    if leftPts.Count > 1 then
        canvas.DrawPoints(SKPointMode.Polygon, leftPts.ToArray(), paint)
    if rightPts.Count > 1 then
        canvas.DrawPoints(SKPointMode.Polygon, rightPts.ToArray(), paint)

/// Render glyph positions, baselines and cap-height bands for one scene.
let private drawScene
    (canvas: SKCanvas)
    (shape: ISdfShape)
    (dl: DrawList)
    (srcText: string)
    (metrics: FontMetrics)
    (fontSizePx: float32)
    (font: SKFont)
    (paint: SKPaint) =

    let upm    = float32 metrics.UnitsPerEm
    let scale  = fontSizePx / upm
    let ascent = float32 metrics.Ascender * scale
    let bb     = shape.BoundingBox

    // Shape outline
    use shapePaint = new SKPaint(
        Color       = colShape,
        StrokeWidth = 1.5f,
        IsAntialias = true,
        Style       = SKPaintStyle.Stroke
    )
    drawShapeOutline canvas shape shapePaint

    // Collect unique baselines
    let baselines = System.Collections.Generic.HashSet<float32>()
    for i in 0 .. dl.GlyphCount - 1 do
        baselines.Add(dl.YPositions.[i]) |> ignore

    // Cap-height bands
    use capPaint = new SKPaint(Color = colCapBand, Style = SKPaintStyle.Fill)
    for y in baselines do
        canvas.DrawRect(SKRect(bb.Left, y - ascent, bb.Right, y), capPaint)

    // Baseline dashes
    use blPaint = new SKPaint(
        Color       = colBaseline,
        StrokeWidth = 0.75f,
        IsAntialias = false
    )
    blPaint.PathEffect <- SKPathEffect.CreateDash([| 4.0f; 3.0f |], 0.0f)
    for y in baselines do
        canvas.DrawLine(bb.Left, y, bb.Right, y, blPaint)

    // Glyph characters using SkiaSharp v3 font/paint API
    paint.Color       <- colGlyph
    paint.IsAntialias <- true
    paint.Style       <- SKPaintStyle.Fill

    let srcChars  = srcText.ToCharArray()
    let mutable charIdx = 0
    for i in 0 .. dl.GlyphCount - 1 do
        let x  = dl.XPositions.[i]
        let y  = dl.YPositions.[i]
        let ch = if charIdx < srcChars.Length then string srcChars.[charIdx] else "?"
        if ch <> " " then
            canvas.DrawText(ch, x, y, SKTextAlign.Left, font, paint)
        charIdx <- charIdx + 1

// ── Scene renderer ───────────────────────────────────────────────────────────

let private renderScene
    (tables: FontTables)
    (metrics: FontMetrics)
    (shape: ISdfShape)
    (text: string)
    (fontSizePx: float32)
    (mode: LayoutMode)
    (typeface: SKTypeface)
    (outPath: string) =

    let req = {
        Text       = ReadOnlyMemory(text.ToCharArray())
        Shape      = shape; Font = tables; Metrics = metrics
        FontSizePx = fontSizePx; Mode = mode
    }
    let result = layout req
    let dl     = fromLayoutResult result

    let bb  = shape.BoundingBox
    let pad = 20
    let w   = int bb.Width  + pad * 2
    let h   = int bb.Height + pad * 2

    let info    = SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
    use surface = SKSurface.Create(info)
    let canvas  = surface.Canvas
    canvas.Clear(colBg)

    use font  = new SKFont(typeface, result.FontSizeUsed)
    use paint = new SKPaint()

    canvas.Save()    |> ignore
    canvas.Translate(float32 pad - bb.Left, float32 pad - bb.Top)
    drawScene canvas shape dl text metrics result.FontSizeUsed font paint
    canvas.Restore()

    // Caption
    use captionFont  = new SKFont(SKTypeface.FromFamilyName("monospace"), 10.0f)
    use captionPaint = new SKPaint(Color = SKColor(100uy, 100uy, 100uy), IsAntialias = true)
    let caption = sprintf "GlyphAura | %d glyphs | %.1fpx | %A" dl.GlyphCount result.FontSizeUsed mode
    canvas.DrawText(caption, 6.0f, float32 h - 5.0f, SKTextAlign.Left, captionFont, captionPaint)

    use image  = surface.Snapshot()
    use data   = image.Encode(SKEncodedImageFormat.Png, 100)
    use stream = File.OpenWrite(outPath)
    data.SaveTo(stream)

    if result.GlyphBuffer.Length > 0 then
        ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    outPath

// ── Kerning test ─────────────────────────────────────────────────────────────

let private renderKerningTest
    (tables: FontTables)
    (metrics: FontMetrics)
    (typeface: SKTypeface)
    (outPath: string) =

    let fontSize = 72.0f
    let pairs    = [| "VA"; "Te"; "AV"; "To" |]
    let colW     = 120
    let rowH     = 200
    let w = colW * pairs.Length + 20
    let h = rowH + 50

    let info    = SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
    use surface = SKSurface.Create(info)
    let canvas  = surface.Canvas
    canvas.Clear(colBg)

    use font       = new SKFont(typeface, fontSize)
    use glyphPaint = new SKPaint(Color = colGlyphTest, IsAntialias = true, Style = SKPaintStyle.Fill)
    use blPaint    = new SKPaint(Color = colBaseline,  StrokeWidth = 1.0f)
    blPaint.PathEffect <- SKPathEffect.CreateDash([| 4.0f; 3.0f |], 0.0f)
    use labelFont  = new SKFont(SKTypeface.FromFamilyName("monospace"), 11.0f)
    use labelPaint = new SKPaint(Color = SKColor(80uy, 80uy, 80uy), IsAntialias = true)

    let upm    = float32 metrics.UnitsPerEm
    let scale  = fontSize / upm
    let ascent = float32 metrics.Ascender * scale

    let mutable col = 0
    for pair in pairs do
        let ox     = float32 col * float32 colW + 10.0f
        let oy     = 20.0f
        let shapeW = float32 colW - 10.0f
        let shapeH = float32 rowH
        let shape  = Shapes.parallelogram
                        { X = ox + shapeW * 0.5f; Y = oy + shapeH * 0.5f }
                        shapeW shapeH 0.0f

        let req = {
            Text       = ReadOnlyMemory(pair.ToCharArray())
            Shape      = shape; Font = tables; Metrics = metrics
            FontSizePx = fontSize; Mode = Flow
        }
        let result = layout req
        let dl     = fromLayoutResult result

        // Baseline
        let baseline = oy + ascent
        canvas.DrawLine(ox, baseline, ox + shapeW, baseline, blPaint)

        // Glyphs
        let srcChars  = pair.ToCharArray()
        let mutable charIdx = 0
        for i in 0 .. dl.GlyphCount - 1 do
            let x  = dl.XPositions.[i]
            let y  = dl.YPositions.[i]
            let ch = if charIdx < srcChars.Length then string srcChars.[charIdx] else "?"
            canvas.DrawText(ch, x, y, SKTextAlign.Left, font, glyphPaint)
            charIdx <- charIdx + 1

        // Label
        canvas.DrawText(pair, ox + shapeW * 0.5f - 8.0f, float32 h - 8.0f, SKTextAlign.Left, labelFont, labelPaint)

        if result.GlyphBuffer.Length > 0 then
            ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
        col <- col + 1

    // Title
    use titleFont  = new SKFont(typeface, 10.0f)
    use titlePaint = new SKPaint(Color = SKColor(80uy, 80uy, 80uy), IsAntialias = true)
    canvas.DrawText("Kerning pairs at 72px — even spacing = correct kern",
        6.0f, float32 h - 25.0f, SKTextAlign.Left, titleFont, titlePaint)

    use image  = surface.Snapshot()
    use data   = image.Encode(SKEncodedImageFormat.Png, 100)
    use stream = File.OpenWrite(outPath)
    data.SaveTo(stream)
    outPath

// ── Public API ─────────────────────────────────────────────────────────────

/// Run all PNG demo renders, writing output files to the given directory.
/// Returns a list of (description, filePath) pairs.
let runAll
    (tables: FontTables)
    (metrics: FontMetrics)
    (outDir: string)
    : (string * string) list =

    let typeface = loadTypeface()
    let pangram  = "The quick brown fox jumps over the lazy dog."
    let longText =
        "The quick brown fox jumps over the lazy dog. " +
        "Pack my box with five dozen liquor jugs. " +
        "How vexingly quick daft zebras jump!"
    let hello    = "Hello, World! GlyphAura"
    let out name = Path.Combine(outDir, name)

    let p1 = renderScene tables metrics
                (Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f)
                longText 12.0f Flow typeface (out "circle_flow.png")

    let p2 = renderScene tables metrics
                (Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f)
                hello 14.0f Fit typeface (out "circle_fit.png")

    let p3 = renderScene tables metrics
                (Shapes.diamond { X = 200.0f; Y = 150.0f } 150.0f 150.0f)
                pangram 12.0f Flow typeface (out "diamond_flow.png")

    let p4 = renderScene tables metrics
                (Shapes.triangle { X = 200.0f; Y = 150.0f } 300.0f 200.0f)
                longText 11.0f Flow typeface (out "triangle_flow.png")

    let p5 = renderScene tables metrics
                (Shapes.parallelogram { X = 200.0f; Y = 100.0f } 250.0f 180.0f 0.4f)
                pangram 12.0f Flow typeface (out "parallelogram_flow.png")

    let p6 = renderKerningTest tables metrics typeface (out "kerning_test.png")

    [
        "Circle Flow (PNG)",        p1
        "Circle Fit (PNG)",         p2
        "Diamond Flow (PNG)",       p3
        "Triangle Flow (PNG)",      p4
        "Parallelogram Flow (PNG)", p5
        "Kerning Test (PNG)",       p6
    ]
