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
/// Fix: shape boundary for each canvas row is drawn at the same Y that the
/// layout engine used for that row's baseline.  Previously a uniform Y grid
/// was used, which caused glyphs to appear outside the boundary when the
/// uniform Y differed from the actual baseline Y.
let renderAscii (dl: DrawList) (shape: ISdfShape) (canvasW: int) (canvasH: int) (text: string) =
    let canvas = Array2D.create canvasH canvasW ' '
    let bb = shape.BoundingBox

    // Build a per-row Y table.  Default: uniform spacing through the bounding box.
    // Rows that contain glyphs are overridden with the glyph's actual baseline Y so
    // that the shape boundary is queried at exactly the same scanline that the layout
    // engine used — eliminating the visual artefact of characters outside the edge.
    let rowY = Array.init canvasH (fun row ->
        bb.Top + float32 row * (bb.Height / float32 canvasH))

    for i in 0 .. dl.GlyphCount - 1 do
        let py = dl.YPositions.[i]
        let row = int (float32 canvasH * (py - bb.Top) / bb.Height)
        if row >= 0 && row < canvasH then
            rowY.[row] <- py   // anchor to actual baseline

    // Draw shape boundary at the consistent per-row Y values
    for row in 0 .. canvasH - 1 do
        let y = rowY.[row]
        match shape.GetInterval y with
        | ValueNone -> ()
        | ValueSome(struct (xs, xe)) ->
            let lx = int (float32 canvasW * (xs - bb.Left) / bb.Width)
            let rx = int (float32 canvasW * (xe - bb.Left) / bb.Width)
            if lx >= 0 && lx < canvasW then canvas.[row, lx] <- '|'
            if rx >= 0 && rx < canvasW then canvas.[row, min (canvasW - 1) rx] <- '|'

    // Place glyphs (use source text chars when available)
    let srcChars = text.ToCharArray()
    let mutable charIdx = 0

    for i in 0 .. dl.GlyphCount - 1 do
        let px = dl.XPositions.[i]
        let py = dl.YPositions.[i]

        // Map to canvas coordinates
        let col = int (float32 canvasW * (px - bb.Left) / bb.Width)
        let row = int (float32 canvasH * (py - bb.Top) / bb.Height)

        if row >= 0 && row < canvasH && col >= 0 && col < canvasW then
            let ch =
                if charIdx < srcChars.Length && srcChars.[charIdx] <> ' ' then
                    srcChars.[charIdx]
                elif charIdx < srcChars.Length then
                    '·'
                else '·'
            canvas.[row, col] <- ch
            charIdx <- charIdx + 1

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

let private runScene (tables: FontTables) (metrics: FontMetrics) (shape: ISdfShape) (shapeName: string) (text: string) (fontSize: float32) (mode: LayoutMode) =
    printBanner (sprintf "%s — %s (%.0fpx, %A)" shapeName text fontSize mode)

    let req = {
        Text = ReadOnlyMemory(text.ToCharArray())
        Shape = shape
        Font = tables
        Metrics = metrics
        FontSizePx = fontSize
        Mode = mode
    }

    let result = layout req
    let dl = fromLayoutResult result

    printfn "  Glyphs placed  : %d / %d source chars" dl.GlyphCount text.Length
    printfn "  Font size used : %.1f px" result.FontSizeUsed
    printfn "  Unique glyphs  : %d (raster requests)" dl.GlyphRasterRequests.Length
    printfn "  Overflow       : %s" (if result.LastTextIndex >= text.Length then "none" else sprintf "%d chars clipped" (text.Length - result.LastTextIndex))
    printfn "  Bounds         : (%.0f, %.0f) → (%.0f, %.0f)" dl.TightBounds.Left dl.TightBounds.Top dl.TightBounds.Right dl.TightBounds.Bottom
    printfn ""

    // ASCII render
    renderAscii dl shape 72 24 text

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

        let pangram = "The quick brown fox jumps over the lazy dog."
        let helloWorld = "Hello, World! GlyphAura"
        let shortText = "Glyph"

        // ── Scene 1: Circle, Flow mode ────────────────────────────────────
        let circleShape = circle { X = 150.0f; Y = 150.0f } 150.0f
        runScene tables metrics circleShape "Circle r=150" pangram 14.0f Flow |> ignore

        // ── Scene 2: Circle, Fit mode ─────────────────────────────────────
        runScene tables metrics circleShape "Circle r=150 (Fit)" helloWorld 14.0f Fit |> ignore

        // ── Scene 3: Diamond ──────────────────────────────────────────────
        let diamondShape = diamond { X = 200.0f; Y = 150.0f } 150.0f 150.0f
        runScene tables metrics diamondShape "Diamond 150×150" pangram 12.0f Flow |> ignore

        // ── Scene 4: Triangle ─────────────────────────────────────────────
        let triangleShape = triangle { X = 200.0f; Y = 150.0f } 300.0f 200.0f
        runScene tables metrics triangleShape "Triangle 300×200" pangram 12.0f Flow |> ignore

        // ── Scene 5: Parallelogram ────────────────────────────────────────
        let paraShape = parallelogram { X = 200.0f; Y = 100.0f } 250.0f 180.0f 0.4f
        runScene tables metrics paraShape "Parallelogram 250×180 shear=0.4" pangram 12.0f Flow |> ignore

        // ── Scene 6: Cache benchmark ──────────────────────────────────────
        runCacheDemo tables metrics

        // ── Scene 7: PNG renders ──────────────────────────────────────
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

        // ── Scene 8: SVG renders ──────────────────────────────────────
        printBanner "SVG Renders"
        let svgResults = SvgDemo.runAll tables metrics outDir
        for (desc, path) in svgResults do
            printfn "  ✓ %s → %s" desc (Path.GetFileName(path))

        printfn ""
        printfn "  ✓ All demos complete. Ready for SceneGraph integration."
        printfn ""
        0
