/// GlyphAura Demo — ASCII-art rendering of text in SDF shapes.
///
/// This demo shows the full pipeline:
///   font file → parse → shape → SDF layout → glyph positions → ASCII canvas
///
/// Each glyph is represented by its character on an ASCII canvas.
/// The shape boundary is drawn with '#' to visualise the SDF region.
module GlyphAura.Demo.Program

open System
open System.IO
open System.Buffers
open GlyphAura
open GlyphAura.Shapes
open GlyphAura.LayoutEngine
open GlyphAura.DrawList
open GlyphAura.ShapeCache
open GlyphAura.TextNode

// ── Canvas helpers ─────────────────────────────────────────────────────────

let private printBanner (title: string) =
    let line = String.replicate (title.Length + 4) "─"
    printfn ""
    printfn "┌%s┐" line
    printfn "│  %s  │" title
    printfn "└%s┘" line

/// Render a DrawList onto a character grid at the given scale.
/// The shape boundary is drawn with '|' characters.
///
/// Improved ASCII rendering:
///   • Auto-sized canvas proportional to shape dimensions
///   • Column-collision avoidance: glyphs on the same row are processed
///     left-to-right and bumped to the next column if occupied
///   • Shape boundary is drawn FIRST, then glyphs on top
let renderAscii (dl: DrawList) (shape: ISdfShape) (canvasW: int) (canvasH: int) (text: string) =
    let canvas = Array2D.create canvasH canvasW ' '
    let bb = shape.BoundingBox

    // Mapping helpers
    let toCol (x: float32) = int (float32 (canvasW - 1) * (x - bb.Left) / bb.Width)
    let toRow (y: float32) = int (float32 (canvasH - 1) * (y - bb.Top) / bb.Height)

    // Build a per-row Y table.  Default: uniform spacing through the bounding box.
    // Rows that contain glyphs are overridden with the glyph's actual baseline Y so
    // that the shape boundary is queried at exactly the same scanline that the layout
    // engine used — eliminating the visual artefact of characters outside the edge.
    let rowY = Array.init canvasH (fun row ->
        bb.Top + float32 row * (bb.Height / float32 (canvasH - 1)))

    for i in 0 .. dl.GlyphCount - 1 do
        let py = dl.YPositions.[i]
        let row = toRow py
        if row >= 0 && row < canvasH then
            rowY.[row] <- py   // anchor to actual baseline

    // Draw shape boundary at the consistent per-row Y values
    for row in 0 .. canvasH - 1 do
        let y = rowY.[row]
        match shape.GetInterval y with
        | ValueNone -> ()
        | ValueSome(struct (xs, xe)) ->
            let lx = toCol xs
            let rx = min (canvasW - 1) (toCol xe)
            if lx >= 0 && lx < canvasW then canvas.[row, lx] <- '|'
            if rx >= 0 && rx < canvasW && rx <> lx then canvas.[row, rx] <- '|'

    // Group glyphs by row, then process left-to-right with collision avoidance.
    // This prevents narrow glyphs (like 'i', 'l') from being overwritten by
    // adjacent glyphs that map to the same canvas column.
    let srcChars = text.ToCharArray()

    // Collect (row, col, char) tuples and sort by row then col
    let glyphEntries =
        [| for i in 0 .. dl.GlyphCount - 1 do
            let px = dl.XPositions.[i]
            let py = dl.YPositions.[i]
            let ci = dl.ClusterIndices.[i]
            let col = toCol px
            let row = toRow py
            if row >= 0 && row < canvasH && col >= 0 then
                let ch =
                    if ci >= 0 && ci < srcChars.Length && srcChars.[ci] <> ' ' then
                        srcChars.[ci]
                    else '·'
                (row, col, ch) |]

    // Sort by row, then col
    let sorted = glyphEntries |> Array.sortBy (fun (r, c, _) -> (r, c))

    // Place with collision avoidance: on each row, track the last occupied column
    let mutable lastRow = -1
    let mutable lastCol = -1
    for (row, col, ch) in sorted do
        if row <> lastRow then
            lastRow <- row
            lastCol <- -1
        // Ensure this glyph gets a unique column (bump right if collision)
        let actualCol = max col (lastCol + 1)
        if actualCol < canvasW then
            canvas.[row, actualCol] <- ch
            lastCol <- actualCol

    // Print
    for row in 0 .. canvasH - 1 do
        let line = String(Array.init canvasW (fun col -> canvas.[row, col]))
        printfn "%s" line

// ── Font loading helper ────────────────────────────────────────────────────

let private tryLoadFont () : Result<FontTables * FontMetrics, string> =
    let candidates = [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
        "/usr/share/fonts/TTF/DejaVuSans.ttf"
        "/System/Library/Fonts/Helvetica.ttf"
        "C:/Windows/Fonts/arial.ttf"
    ]
    let found =
        candidates
        |> List.tryFind File.Exists

    match found with
    | None ->
        Error "No usable font found. Install DejaVu fonts: apt-get install fonts-dejavu"
    | Some path ->
        printfn "  Loading font: %s" path
        let bytes = File.ReadAllBytes(path)
        FontProvider.load (ReadOnlyMemory(bytes))

// ── Demo scenes ──────────────────────────────────────────────────────────

let private runScene (tables: FontTables) (metrics: FontMetrics) (shape: ISdfShape) (shapeName: string) (text: string) (fontSize: float32) (mode: LayoutMode) (alignment: TextAlignment) =
    printBanner (sprintf "%s — %s (%.0fpx, %A, %A)" shapeName text fontSize mode alignment)

    let req = {
        Text = ReadOnlyMemory(text.ToCharArray())
        Shape = shape
        Font = tables
        Metrics = metrics
        FontSizePx = fontSize
        Mode = mode
        Alignment = alignment
    }

    let result = layout req
    let dl = fromLayoutResult result

    printfn "  Glyphs placed  : %d / %d source chars" dl.GlyphCount text.Length
    printfn "  Font size used : %.1f px" result.FontSizeUsed
    printfn "  Unique glyphs  : %d (raster requests)" dl.GlyphRasterRequests.Length
    printfn "  Overflow       : %s" (if result.LastTextIndex >= text.Length then "none" else sprintf "%d chars clipped" (text.Length - result.LastTextIndex))
    printfn "  Bounds         : (%.0f, %.0f) → (%.0f, %.0f)" dl.TightBounds.Left dl.TightBounds.Top dl.TightBounds.Right dl.TightBounds.Bottom
    printfn ""

    // ASCII render — auto-size canvas for better resolution
    let bb = shape.BoundingBox
    let canvasW = max 72 (min 120 (int (bb.Width / 2.5f)))
    let canvasH = max 16 (min 48 (int (bb.Height / 6.0f)))
    renderAscii dl shape canvasW canvasH text

    // Return ArrayPool buffer
    if result.GlyphBuffer.Length > 0 then
        ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

    result

let private runCacheDemo (tables: FontTables) (metrics: FontMetrics) =
    printBanner "TextNode Cache Demo"

    let shape = circle { X = 100.0f; Y = 100.0f } 100.0f
    let cache = defaultCache()
    let node = TextNode.create tables metrics 1UL shape "Cached text layout" 14.0f

    // First render — layout happens
    let sw1 = System.Diagnostics.Stopwatch.StartNew()
    let dl1 = TextNode.update node cache
    sw1.Stop()

    // Second render — cache hit
    let sw2 = System.Diagnostics.Stopwatch.StartNew()
    let dl2 = TextNode.update node cache
    sw2.Stop()

    printfn "  First render  : %.0f µs  (%d glyphs)" sw1.Elapsed.TotalMicroseconds dl1.GlyphCount
    printfn "  Cached render : %.0f µs  (%d glyphs, cache hit)" sw2.Elapsed.TotalMicroseconds dl2.GlyphCount
    printfn "  Speedup       : %.1fx" (float sw1.Elapsed.Ticks / float (max 1L sw2.Elapsed.Ticks))

    // Text change — triggers re-layout
    TextNode.setText node "Updated text"
    let sw3 = System.Diagnostics.Stopwatch.StartNew()
    let dl3 = TextNode.update node cache
    sw3.Stop()
    printfn "  After setText : %.0f µs  (%d glyphs)" sw3.Elapsed.TotalMicroseconds dl3.GlyphCount

// ── Main ──────────────────────────────────────────────────────────────────

[<EntryPoint>]
let main _ =
    printfn ""
    printfn "  ██████╗ ██╗  ██╗   ██╗██████╗ ██╗  ██╗ █████╗ ██╗   ██╗██████╗  ██████╗ "
    printfn "  ██╔════╝██║  ╚██╗ ██╔╝██╔══██╗██║  ██║██╔══██╗██║   ██║██╔══██╗██╔═══██╗"
    printfn "  ██║  ███╗██║   ╚████╔╝ ██████╔╝███████║███████║██║   ██║██████╔╝██║   ██║"
    printfn "  ██║   ██║██║    ╚██╔╝  ██╔═══╝ ██╔══██║██╔══██║██║   ██║██╔══██╗██║   ██║"
    printfn "  ╚██████╔╝███████╗██║   ██║     ██║  ██║██║  ██║╚██████╔╝██║  ██║╚██████╔╝"
    printfn "   ╚═════╝ ╚══════╝╚═╝   ╚═╝     ╚═╝  ╚═╝╚═╝  ╚═╝ ╚═════╝ ╚═╝  ╚═╝ ╚═════╝ "
    printfn ""
    printfn "  F# OpenType Shaping & SDF Layout Engine — Demo"
    printfn "  Version 0.3.0 | MIT License"
    printfn ""

    match tryLoadFont() with
    | Error msg ->
        eprintfn "ERROR: %s" msg
        1
    | Ok(tables, metrics) ->
        printfn "  Font loaded successfully."
        printfn "  UPM=%d  Ascender=%d  Descender=%d  LineGap=%d"
            metrics.UnitsPerEm metrics.Ascender metrics.Descender metrics.LineGap
        printfn ""

        // Multiple pangrams + multi-script text for full-coverage demos
        let pangram = "The quick brown fox jumps over the lazy dog."
        let longText =
            "The quick brown fox jumps over the lazy dog. " +
            "Pack my box with five dozen liquor jugs. " +
            "How vexingly quick daft zebras jump!"
        let multiScript =
            "GlyphAura: Hello αβγ ∑∫≠ ☃♠ שלום 😀"
        let helloWorld = "Hello, World! GlyphAura"

        // ── Scene 1: Circle, Flow mode (longer text) ─────────────────────
        let circleShape = circle { X = 150.0f; Y = 150.0f } 150.0f
        runScene tables metrics circleShape "Circle r=150" longText 12.0f Flow Left |> ignore

        // ── Scene 2: Circle, Fit mode ─────────────────────────────────────
        runScene tables metrics circleShape "Circle r=150 (Fit)" helloWorld 14.0f Fit Left |> ignore

        // ── Scene 3: Diamond ──────────────────────────────────────────────
        let diamondShape = diamond { X = 200.0f; Y = 150.0f } 150.0f 150.0f
        runScene tables metrics diamondShape "Diamond 150×150" pangram 12.0f Flow Left |> ignore

        // ── Scene 4: Triangle (longer text) ───────────────────────────────
        let triangleShape = triangle { X = 200.0f; Y = 150.0f } 300.0f 200.0f
        runScene tables metrics triangleShape "Triangle 300×200" longText 11.0f Flow Left |> ignore

        // ── Scene 5: Parallelogram ────────────────────────────────────────
        let paraShape = parallelogram { X = 200.0f; Y = 100.0f } 250.0f 180.0f 0.4f
        runScene tables metrics paraShape "Parallelogram 250×180 shear=0.4" pangram 12.0f Flow Left |> ignore

        // ── Scene 6: Multi-script demo ────────────────────────────────────
        let wideShape = parallelogram { X = 300.0f; Y = 50.0f } 580.0f 60.0f 0.0f
        runScene tables metrics wideShape "Multi-script" multiScript 14.0f Flow Left |> ignore

        // ── Scene 7: Center alignment ─────────────────────────────────────
        runScene tables metrics circleShape "Circle Centered" longText 12.0f Flow Center |> ignore

        // ── Scene 8: Justified alignment ──────────────────────────────────
        runScene tables metrics circleShape "Circle Justified" longText 12.0f Flow Justified |> ignore

        // ── Scene 9: Diamond centered ─────────────────────────────────────
        runScene tables metrics diamondShape "Diamond Centered" pangram 12.0f Flow Center |> ignore

        // ── Scene 10: Cache benchmark ─────────────────────────────────────
        runCacheDemo tables metrics

        // ── PNG renders ──────────────────────────────────────
        printBanner "PNG Renders (SkiaSharp)"
        // Go 5 levels up from net10.0/bin/Release/GlyphAura.Demo/demo → repo root
        let outDir =
            Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "demo", "output"))
        Directory.CreateDirectory(outDir) |> ignore
        printfn "  Output directory: %s" outDir
        let pngResults = PngDemo.runAll tables metrics outDir
        for (desc, path) in pngResults do
            printfn "  ✓ %s → %s" desc (Path.GetFileName(path))

        // ── SVG renders ──────────────────────────────────────
        printBanner "SVG Renders"
        let svgResults = SvgDemo.runAll tables metrics outDir
        for (desc, path) in svgResults do
            printfn "  ✓ %s → %s" desc (Path.GetFileName(path))

        printfn ""
        printfn "  ✓ All demos complete. Ready for SceneGraph integration."
        printfn ""
        0
