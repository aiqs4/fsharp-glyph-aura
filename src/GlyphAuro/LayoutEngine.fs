namespace GlyphAuro

open System
open System.Buffers
open GlyphAuro.Shapes
open GlyphAuro.GposKerning

/// Fused Streaming Layout Engine.
/// Interleaves shaping and layout in a single streaming loop.
module LayoutEngine =

    /// Layout mode: Flow (fixed font size) or Fit (auto-size).
    [<Struct>]
    type LayoutMode =
        | Flow
        | Fit

    /// A positioned glyph in the output.
    [<Struct>]
    type GlyphPosition = {
        GlyphId: uint16
        X: float32
        Y: float32
    }

    /// Input to the layout engine.
    type LayoutRequest = {
        Text: ReadOnlyMemory<char>
        Shape: ISdfShape
        Font: FontTables
        Metrics: FontMetrics
        FontSizePx: float32
        Mode: LayoutMode
    }

    /// Output from the layout engine.
    type LayoutResult = {
        Glyphs: Memory<GlyphPosition>
        GlyphBuffer: GlyphPosition array
        TotalBounds: Rectangle
        LastTextIndex: int
        FontSizeUsed: float32
    }

    /// Precomputed font data for the layout loop.
    type private FontData = {
        Cmap: CMapTable
        Hmtx: HmtxTable
        KernLegacy: KernTable voption
        GposKern: KerningClasses voption
        NumGlyphs: int
    }

    let private loadFontData (tables: FontTables) : FontData =
        let maxpSpan = tables.Maxp.Span
        let numGlyphs = int (BinaryReader.readU16 maxpSpan 4)
        let cmap = CMap.parse tables.Cmap
        let hmtx = Hmtx.parse tables.Hmtx tables.Hhea numGlyphs
        let kernLegacy =
            match tables.Kern with
            | ValueSome kd -> KernLegacy.parse kd
            | ValueNone -> ValueNone
        let gposKern =
            match tables.Gpos with
            | ValueSome gd -> GposKerning.parse gd numGlyphs
            | ValueNone -> ValueNone
        { Cmap = cmap; Hmtx = hmtx; KernLegacy = kernLegacy; GposKern = gposKern; NumGlyphs = numGlyphs }

    /// Get kerning adjustment: GPOS takes precedence over legacy kern per OpenType spec.
    let private getKernAdjustment (fd: FontData) (left: uint16) (right: uint16) : float32 =
        match fd.GposKern with
        | ValueSome gk ->
            let v = GposKerning.getAdjustment gk left right
            if v <> 0s then float32 v
            else
            // Fallback to legacy kern
            match fd.KernLegacy with
            | ValueSome kl -> float32 (KernLegacy.getAdjustment kl left right)
            | ValueNone -> 0.0f
        | ValueNone ->
            match fd.KernLegacy with
            | ValueSome kl -> float32 (KernLegacy.getAdjustment kl left right)
            | ValueNone -> 0.0f

    /// Layout a single line with word-boundary breaking.
    /// Returns the number of glyphs placed and the text index consumed.
    let private layoutLine
        (text: ReadOnlySpan<char>)
        (fd: FontData)
        (scale: float32)
        (xStart: float32)
        (xEnd: float32)
        (y: float32)
        (buffer: GlyphPosition array)
        (outIdx: int)
        : struct (int * int) =  // (glyphsPlaced, charsConsumed)

        let availableWidth = xEnd - xStart  // pixels — shaper's totalAdvance is also in pixels
        let shapeBuf = ArrayPool<ShapedGlyph>.Shared.Rent(text.Length)

        try
            // Shape the line with width constraint
            let result = Shaper.shapeLine text fd.Cmap fd.Hmtx fd.KernLegacy scale availableWidth shapeBuf

            let mutable charsConsumed = 0
            let mutable glyphsPlaced = 0

            if result.LastFitIndex >= 0 then
                // Width exceeded — find word boundary
                let lastFit = result.LastFitIndex
                // Backtrack to last whitespace
                let mutable breakIdx = lastFit
                while breakIdx > 0 && text.[breakIdx] <> ' ' do
                    breakIdx <- breakIdx - 1

                if breakIdx = 0 && text.[0] <> ' ' then
                    // Single word exceeds line width — force break at lastFit
                    breakIdx <- max 1 lastFit

                // Place glyphs up to breakIdx
                let mutable x = xStart
                for i in 0 .. min (breakIdx - 1) (result.GlyphCount - 1) do
                    if outIdx + glyphsPlaced < buffer.Length then
                        let g = shapeBuf.[i]
                        // Apply GPOS kerning adjustment (overrides legacy kern)
                        let kernAdj =
                            if i > 0 then
                                let prevId = shapeBuf.[i - 1].GlyphId
                                let gposAdj = getKernAdjustment fd prevId g.GlyphId
                                // Legacy kern was already included in XAdvance by shaper;
                                // if GPOS provides a value, use the difference
                                match fd.GposKern with
                                | ValueSome _ ->
                                    let legacyAdj = float32 (KernLegacy.getAdjustmentOpt fd.KernLegacy prevId g.GlyphId)
                                    (gposAdj - legacyAdj) * scale
                                | ValueNone -> 0.0f
                            else 0.0f
                        buffer.[outIdx + glyphsPlaced] <- { GlyphId = g.GlyphId; X = x + kernAdj; Y = y }
                        x <- x + g.XAdvance * scale + kernAdj
                        glyphsPlaced <- glyphsPlaced + 1

                // Skip whitespace after break
                charsConsumed <- breakIdx
                while charsConsumed < text.Length && text.[charsConsumed] = ' ' do
                    charsConsumed <- charsConsumed + 1
            else
                // All glyphs fit
                let mutable x = xStart
                for i in 0 .. result.GlyphCount - 1 do
                    if outIdx + glyphsPlaced < buffer.Length then
                        let g = shapeBuf.[i]
                        let kernAdj =
                            if i > 0 then
                                let prevId = shapeBuf.[i - 1].GlyphId
                                match fd.GposKern with
                                | ValueSome _ ->
                                    let gposAdj = getKernAdjustment fd prevId g.GlyphId
                                    let legacyAdj = float32 (KernLegacy.getAdjustmentOpt fd.KernLegacy prevId g.GlyphId)
                                    (gposAdj - legacyAdj) * scale
                                | ValueNone -> 0.0f
                            else 0.0f
                        buffer.[outIdx + glyphsPlaced] <- { GlyphId = g.GlyphId; X = x + kernAdj; Y = y }
                        x <- x + g.XAdvance * scale + kernAdj
                        glyphsPlaced <- glyphsPlaced + 1
                charsConsumed <- text.Length

            struct (glyphsPlaced, charsConsumed)
        finally
            ArrayPool<ShapedGlyph>.Shared.Return(shapeBuf, false)

    /// Core layout loop: descend the shape line by line, shape text into each line.
    let private layoutAtSize
        (text: ReadOnlySpan<char>)
        (shape: ISdfShape)
        (fd: FontData)
        (fontSizePx: float32)
        (buffer: GlyphPosition array)
        (metrics: FontMetrics)
        : struct (int * int * Rectangle) =  // (glyphCount, lastTextIndex, bounds)

        let upm = float32 metrics.UnitsPerEm
        let scale = fontSizePx / upm
        let lineHeight = (float32 metrics.Ascender - float32 metrics.Descender + float32 metrics.LineGap) * scale
        let ascent = float32 metrics.Ascender * scale

        let bbox = shape.BoundingBox
        let mutable y = bbox.Top + ascent
        let mutable textIdx = 0
        let mutable outIdx = 0
        let mutable minX = Single.MaxValue
        let mutable minY = Single.MaxValue
        let mutable maxX = Single.MinValue
        let mutable maxY = Single.MinValue

        while y <= bbox.Bottom && textIdx < text.Length do
            match shape.GetInterval y with
            | ValueNone ->
                y <- y + lineHeight
            | ValueSome(struct (xStart, xEnd)) ->
                let remaining = text.Slice(textIdx)
                let struct (glyphsPlaced, charsConsumed) = layoutLine remaining fd scale xStart xEnd y buffer outIdx

                // Update bounds
                for i in outIdx .. outIdx + glyphsPlaced - 1 do
                    let gp = buffer.[i]
                    if gp.X < minX then minX <- gp.X
                    if gp.Y < minY then minY <- gp.Y
                    if gp.X > maxX then maxX <- gp.X
                    if gp.Y > maxY then maxY <- gp.Y

                textIdx <- textIdx + charsConsumed
                outIdx <- outIdx + glyphsPlaced
                y <- y + lineHeight

        let bounds =
            if outIdx = 0 then { Left = 0.0f; Top = 0.0f; Right = 0.0f; Bottom = 0.0f }
            else { Left = minX; Top = minY; Right = maxX; Bottom = maxY }

        struct (outIdx, textIdx, bounds)

    /// Check if all text fits at a given font size.
    let private allTextFits (text: ReadOnlySpan<char>) (shape: ISdfShape) (fd: FontData) (fontSize: float32) (metrics: FontMetrics) : bool =
        let buffer = ArrayPool<GlyphPosition>.Shared.Rent(text.Length)
        try
            let struct (_, lastTextIdx, _) = layoutAtSize text shape fd fontSize buffer metrics
            lastTextIdx >= text.Length
        finally
            ArrayPool<GlyphPosition>.Shared.Return(buffer, false)

    /// Mode B: Binary search for the largest font size where all text fits.
    let private findOptimalFontSize (text: ReadOnlySpan<char>) (shape: ISdfShape) (fd: FontData) (metrics: FontMetrics) : float32 =
        let bbox = shape.BoundingBox
        let mutable lo = 4.0f
        let mutable hi = bbox.Height
        // 12 iterations for < 0.1% error
        for _ in 1 .. 12 do
            let mid = (lo + hi) * 0.5f
            if allTextFits text shape fd mid metrics then
                lo <- mid
            else
                hi <- mid
        (lo + hi) * 0.5f

    /// Main entry point: layout text into a shape.
    let layout (req: LayoutRequest) : LayoutResult =
        let fd = loadFontData req.Font
        let text = req.Text.Span

        if text.Length = 0 then
            let emptyBuf = Array.empty<GlyphPosition>
            { Glyphs = Memory<GlyphPosition>.Empty
              GlyphBuffer = emptyBuf
              TotalBounds = { Left = 0.0f; Top = 0.0f; Right = 0.0f; Bottom = 0.0f }
              LastTextIndex = 0
              FontSizeUsed = req.FontSizePx }
        else

        let fontSize =
            match req.Mode with
            | Flow -> req.FontSizePx
            | Fit -> findOptimalFontSize text req.Shape fd req.Metrics

        let buffer = ArrayPool<GlyphPosition>.Shared.Rent(text.Length * 2)  // generous allocation
        let struct (glyphCount, lastTextIdx, bounds) = layoutAtSize text req.Shape fd fontSize buffer req.Metrics

        { Glyphs = Memory<GlyphPosition>(buffer, 0, glyphCount)
          GlyphBuffer = buffer
          TotalBounds = bounds
          LastTextIndex = lastTextIdx
          FontSizeUsed = fontSize }
