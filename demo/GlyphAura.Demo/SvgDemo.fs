/// GlyphAura SVG Demo — renders glyph positions and SDF shape outlines to SVG.
///
/// Produces vector output that demonstrates:
///   • Correct glyph x/y coordinates from the layout engine
///   • Shape boundary overlay (circle, diamond, triangle, parallelogram)
///   • Baseline indicator lines
///   • Kerning quality tests ("VA", "Te" at large scale)
module GlyphAura.Demo.SvgDemo

open System
open System.IO
open System.Text
open GlyphAura
open GlyphAura.Shapes
open GlyphAura.LayoutEngine
open GlyphAura.DrawList

// ── Colour palette ─────────────────────────────────────────────────────────

[<Literal>]
let private shapeStroke   = "#4a90d9"
[<Literal>]
let private baselineStroke = "#27ae60"
[<Literal>]
let private glyphFill      = "#2c3e50"
[<Literal>]
let private glyphTestFill  = "#c0392b"
[<Literal>]
let private bgFill         = "#f8f9fa"

// ── SVG helpers ─────────────────────────────────────────────────────────────

let private svgHeader (w: int) (h: int) (title: string) : string =
    sprintf "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"%d\" height=\"%d\" viewBox=\"0 0 %d %d\">\n  <title>%s</title>\n  <rect width=\"100%%\" height=\"100%%\" fill=\"%s\"/>\n" w h w h title bgFill

let private svgFooter : string = "</svg>\n"

/// XML-escape a character for SVG text content.
let private xmlEscape (c: char) : string =
    match c with
    | '<'  -> "&lt;"
    | '>'  -> "&gt;"
    | '&'  -> "&amp;"
    | '"'  -> "&quot;"
    | '\'' -> "&apos;"
    | c    -> string c

// ── Shape outline generator (polyline approximation) ────────────────────────

/// Build SVG polyline strings tracing the left and right edges of the shape.
let private shapePolylines (shape: ISdfShape) : string =
    let bb = shape.BoundingBox
    let steps = 80
    let sb = StringBuilder()
    // Left edge
    sb.Append("  <polyline points=\"") |> ignore
    let mutable first = true
    for step in 0 .. steps do
        let y = bb.Top + float32 step * (bb.Height / float32 steps)
        match shape.GetInterval y with
        | ValueSome(struct (xs, _)) ->
            if not first then sb.Append(" ") |> ignore
            sb.Append(sprintf "%.2f,%.2f" xs y) |> ignore
            first <- false
        | ValueNone -> ()
    sb.Append(sprintf "\" fill=\"none\" stroke=\"%s\" stroke-width=\"1.5\" opacity=\"0.8\"/>\n" shapeStroke) |> ignore
    // Right edge
    sb.Append("  <polyline points=\"") |> ignore
    first <- true
    for step in 0 .. steps do
        let y = bb.Top + float32 step * (bb.Height / float32 steps)
        match shape.GetInterval y with
        | ValueSome(struct (_, xe)) ->
            if not first then sb.Append(" ") |> ignore
            sb.Append(sprintf "%.2f,%.2f" xe y) |> ignore
            first <- false
        | ValueNone -> ()
    sb.Append(sprintf "\" fill=\"none\" stroke=\"%s\" stroke-width=\"1.5\" opacity=\"0.8\"/>\n" shapeStroke) |> ignore
    sb.ToString()

// ── Core SVG scene builder ─────────────────────────────────────────────────

let private buildSvg
    (shape: ISdfShape)
    (dl: DrawList)
    (srcText: string)
    (metrics: FontMetrics)
    (fontSizePx: float32)
    (title: string)
    : string =

    let bb  = shape.BoundingBox
    let pad = 20
    let svgW = int bb.Width  + pad * 2
    let svgH = int bb.Height + pad * 2
    let tx   = float32 pad - bb.Left
    let ty   = float32 pad - bb.Top

    let sb = StringBuilder()
    sb.Append(svgHeader svgW svgH title) |> ignore
    sb.Append(sprintf "  <g transform=\"translate(%.2f,%.2f)\">\n" tx ty) |> ignore

    // Shape outline
    sb.Append(shapePolylines shape) |> ignore

    // Baselines — one per distinct glyph Y value
    let baselines = System.Collections.Generic.HashSet<float32>()
    for i in 0 .. dl.GlyphCount - 1 do
        baselines.Add(dl.YPositions.[i]) |> ignore

    for y in baselines do
        let (x1, x2) =
            match shape.GetInterval y with
            | ValueSome(struct (xs, xe)) -> xs, xe
            | ValueNone -> bb.Left, bb.Right
        sb.Append(sprintf
            "  <line x1=\"%.2f\" y1=\"%.2f\" x2=\"%.2f\" y2=\"%.2f\" stroke=\"%s\" stroke-width=\"0.6\" stroke-dasharray=\"4,3\" opacity=\"0.8\"/>\n"
            x1 y x2 y baselineStroke) |> ignore

    // Glyph characters at their layout positions
    let srcChars  = srcText.ToCharArray()
    for i in 0 .. dl.GlyphCount - 1 do
        let x  = dl.XPositions.[i]
        let y  = dl.YPositions.[i]
        let ci = dl.ClusterIndices.[i]
        let ch = if ci >= 0 && ci < srcChars.Length then srcChars.[ci] else '?'
        sb.Append(sprintf
            "  <text x=\"%.2f\" y=\"%.2f\" font-family=\"DejaVu Sans,Arial,sans-serif\" font-size=\"%.1f\" fill=\"%s\">%s</text>\n"
            x y fontSizePx glyphFill (xmlEscape ch)) |> ignore

    sb.Append("  </g>\n") |> ignore

    // Legend
    sb.Append(sprintf
        "  <text x=\"6\" y=\"%d\" font-family=\"monospace\" font-size=\"10\" fill=\"#555\">%s</text>\n"
        (svgH - 22) title) |> ignore
    sb.Append(sprintf
        "  <text x=\"6\" y=\"%d\" font-family=\"monospace\" font-size=\"9\" fill=\"%s\">— baseline   </text>\n"
        (svgH - 8) baselineStroke) |> ignore
    sb.Append(sprintf
        "  <text x=\"92\" y=\"%d\" font-family=\"monospace\" font-size=\"9\" fill=\"%s\">— shape boundary</text>\n"
        (svgH - 8) shapeStroke) |> ignore

    sb.Append(svgFooter) |> ignore
    sb.ToString()

// ── Kerning quality SVG ─────────────────────────────────────────────────────

let private buildKerningSvg (tables: FontTables) (metrics: FontMetrics) : string =
    let fontSize = 72.0f
    let pairs    = [| "VA"; "Te"; "AV"; "To" |]
    let colW     = 120
    let svgH     = 260
    let svgW     = colW * pairs.Length + 20

    let sb = StringBuilder()
    sb.Append(svgHeader svgW svgH "Kerning Test — VA Te AV To") |> ignore

    let upm    = float32 metrics.UnitsPerEm
    let scale  = fontSize / upm
    let ascent = float32 metrics.Ascender * scale

    let mutable col = 0
    for pair in pairs do
        let ox     = float32 col * float32 colW + 10.0f
        let oy     = 20.0f
        let shapeW = float32 colW - 10.0f
        let shapeH = float32 svgH - 60.0f
        let shape  = Shapes.parallelogram
                        { X = ox + shapeW * 0.5f; Y = oy + shapeH * 0.5f }
                        shapeW shapeH 0.0f

        let req = {
            Text       = ReadOnlyMemory(pair.ToCharArray())
            Shape      = shape; Font = tables; Metrics = metrics
            FontSizePx = fontSize; Mode = Flow; Alignment = Left
        }
        let result = layout req
        let dl     = fromLayoutResult result

        // Baseline line
        let baseline = oy + ascent
        sb.Append(sprintf
            "  <line x1=\"%.2f\" y1=\"%.2f\" x2=\"%.2f\" y2=\"%.2f\" stroke=\"%s\" stroke-width=\"0.8\" stroke-dasharray=\"4,3\"/>\n"
            ox baseline (ox + shapeW) baseline baselineStroke) |> ignore

        // Glyphs
        let srcChars  = pair.ToCharArray()
        for i in 0 .. dl.GlyphCount - 1 do
            let x  = dl.XPositions.[i]
            let y  = dl.YPositions.[i]
            let ci = dl.ClusterIndices.[i]
            let ch = if ci >= 0 && ci < srcChars.Length then srcChars.[ci] else '?'
            sb.Append(sprintf
                "  <text x=\"%.2f\" y=\"%.2f\" font-family=\"DejaVu Sans,Arial,sans-serif\" font-size=\"%.1f\" fill=\"%s\">%s</text>\n"
                x y fontSize glyphTestFill (xmlEscape ch)) |> ignore

        // Pair label
        sb.Append(sprintf
            "  <text x=\"%.2f\" y=\"%d\" font-family=\"monospace\" font-size=\"12\" fill=\"#444\" text-anchor=\"middle\">%s</text>\n"
            (ox + shapeW * 0.5f) (svgH - 8) pair) |> ignore

        if result.GlyphBuffer.Length > 0 then
            System.Buffers.ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
        col <- col + 1

    sb.Append(sprintf
        "  <text x=\"%d\" y=\"%d\" font-family=\"monospace\" font-size=\"10\" fill=\"#888\" text-anchor=\"middle\">Kerning pairs at %.0fpx — even spacing = correct kern</text>\n"
        (svgW / 2) (svgH - 28) fontSize) |> ignore
    sb.Append(svgFooter) |> ignore
    sb.ToString()

// ── Public API ─────────────────────────────────────────────────────────────

/// Run all SVG demo renders, writing output files to the given directory.
/// Returns a list of (description, filePath) pairs.
let runAll
    (tables: FontTables)
    (metrics: FontMetrics)
    (outDir: string)
    : (string * string) list =

    let write (name: string) (content: string) =
        let path = Path.Combine(outDir, name)
        File.WriteAllText(path, content)
        path

    let pangram = "The quick brown fox jumps over the lazy dog."
    let longText =
        "The quick brown fox jumps over the lazy dog. " +
        "Pack my box with five dozen liquor jugs. " +
        "How vexingly quick daft zebras jump!"
    let hello   = "Hello, World! GlyphAura"

    let runScene (shape: ISdfShape) (text: string) (fontSize: float32) (mode: LayoutMode) (alignment: TextAlignment) (title: string) =
        let req = {
            Text = ReadOnlyMemory(text.ToCharArray())
            Shape = shape; Font = tables; Metrics = metrics
            FontSizePx = fontSize; Mode = mode; Alignment = alignment
        }
        let result = layout req
        let dl     = fromLayoutResult result
        let svg    = buildSvg shape dl text metrics result.FontSizeUsed title
        if result.GlyphBuffer.Length > 0 then
            System.Buffers.ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)
        svg

    let p1 = write "circle_flow.svg"
                (runScene (Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f)
                    longText 12.0f Flow Left "Circle r=150 — Flow 12px (long text)")

    let p2 = write "circle_fit.svg"
                (runScene (Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f)
                    hello 14.0f Fit Left "Circle r=150 — Fit (auto-size)")

    let p3 = write "diamond_flow.svg"
                (runScene (Shapes.diamond { X = 200.0f; Y = 150.0f } 150.0f 150.0f)
                    pangram 12.0f Flow Left "Diamond 150×150 — Flow 12px")

    let p4 = write "triangle_flow.svg"
                (runScene (Shapes.triangle { X = 200.0f; Y = 150.0f } 300.0f 200.0f)
                    longText 11.0f Flow Left "Triangle 300×200 — Flow 11px (long text)")

    let p5 = write "parallelogram_flow.svg"
                (runScene (Shapes.parallelogram { X = 200.0f; Y = 100.0f } 250.0f 180.0f 0.4f)
                    pangram 12.0f Flow Left "Parallelogram 250×180 shear=0.4 — Flow 12px")

    let p6 = write "kerning_test.svg" (buildKerningSvg tables metrics)

    // Center alignment demos
    let p7 = write "circle_center.svg"
                (runScene (Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f)
                    longText 12.0f Flow Center "Circle r=150 — Center 12px")

    let p8 = write "diamond_center.svg"
                (runScene (Shapes.diamond { X = 200.0f; Y = 150.0f } 150.0f 150.0f)
                    pangram 12.0f Flow Center "Diamond 150×150 — Center 12px")

    // Justified alignment demos
    let p9 = write "circle_justified.svg"
                (runScene (Shapes.circle { X = 150.0f; Y = 150.0f } 150.0f)
                    longText 12.0f Flow Justified "Circle r=150 — Justified 12px")

    [
        "Circle Flow (SVG)",           p1
        "Circle Fit (SVG)",            p2
        "Diamond Flow (SVG)",          p3
        "Triangle Flow (SVG)",         p4
        "Parallelogram Flow (SVG)",    p5
        "Kerning Test (SVG)",          p6
        "Circle Center (SVG)",         p7
        "Diamond Center (SVG)",        p8
        "Circle Justified (SVG)",      p9
    ]
