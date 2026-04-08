namespace GlyphAura

open System
open System.Buffers
open GlyphAura.Shapes
open GlyphAura.GposKerning

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
        /// Index of this glyph's source character in the original text (char-space).
        /// For BMP codepoints this equals the char index; for surrogate pairs it is
        /// the index of the high surrogate.  Matches HarfBuzz "cluster" semantics.
        ClusterIndex: int
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
    ///
    /// Narrow-line contract: if the available width is too narrow for even the
    /// FIRST glyph (GlyphCount = 0 after shaping), return (0, 0) so that
    /// textIdx does NOT advance and the same text is retried on the next,
    /// wider scanline.  This prevents text from being silently swallowed near
    /// the apex of a triangle or the top/bottom of a circle.
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

            // ── Bug fix: if no glyph fits at all, don't consume any chars.
            // The same text will be retried on the next (wider) scanline.
            // Without this guard, the old code would set charsConsumed=1 while
            // placing 0 glyphs, silently discarding characters near the apex of
            // a triangle or the extremities of a circle.
            if result.GlyphCount = 0 && result.LastFitIndex >= 0 then
                struct (0, 0)
            else

            let mutable charsConsumed = 0
            let mutable glyphsPlaced = 0

            /// Compute corrected pixel advance for glyph[i] when GPOS is available.
            /// The shaper baked legacy kern(i → i+1) into XAdvance[i]; if GPOS provides
            /// a different value for that pair, substitute it.
            let correctedAdvance (i: int) (count: int) : float32 =
                if i + 1 < count then
                    let leftId  = shapeBuf.[i].GlyphId
                    let rightId = shapeBuf.[i + 1].GlyphId
                    match fd.GposKern with
                    | ValueSome _ ->
                        let gposAdj    = getKernAdjustment fd leftId rightId
                        let legacyAdj  = float32 (KernLegacy.getAdjustmentOpt fd.KernLegacy leftId rightId)
                        shapeBuf.[i].XAdvance * scale + (gposAdj - legacyAdj) * scale
                    | ValueNone -> shapeBuf.[i].XAdvance * scale
                else
                    shapeBuf.[i].XAdvance * scale

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
                        // Glyph origin is simply the current pen position.
                        // (kern to the next glyph is already included in the advance.)
                        buffer.[outIdx + glyphsPlaced] <- { GlyphId = g.GlyphId; X = x; Y = y; ClusterIndex = g.ClusterIndex }
                        x <- x + correctedAdvance i (min breakIdx result.GlyphCount)
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
                        // Glyph origin is simply the current pen position.
                        buffer.[outIdx + glyphsPlaced] <- { GlyphId = g.GlyphId; X = x; Y = y; ClusterIndex = g.ClusterIndex }
                        x <- x + correctedAdvance i result.GlyphCount
                        glyphsPlaced <- glyphsPlaced + 1
                charsConsumed <- text.Length

            struct (glyphsPlaced, charsConsumed)
        finally
            ArrayPool<ShapedGlyph>.Shared.Return(shapeBuf, false)

    /// Compute a conservative horizontal interval for a text line band
    /// [bandTop, bandBottom].
    ///
    /// Strategy (per the scanline spec):
    ///   1. Use the band MIDPOINT as the primary query (as in the Python reference).
    ///   2. Tighten by querying the band TOP and BOTTOM and taking the intersection
    ///      (max xStart, min xEnd).  This ensures the entire vertical extent of the
    ///      line — from cap height down to descender — remains inside the shape.
    ///
    /// Without this tightening a circle's upper-half lines would start glyphs at the
    /// mid-point's wide xStart even though the shape is narrower at the band top
    /// (where ascenders sit), causing visible overflow in rendered images.
    let private getConservativeInterval
        (shape: ISdfShape)
        (bbox: Rectangle)
        (bandTop: float32)
        (bandBottom: float32)
        : struct (float32 * float32) voption =

        let midY = bandTop + (bandBottom - bandTop) * 0.5f

        match shape.GetInterval midY with
        | ValueNone -> ValueNone  // Midpoint outside shape — skip line
        | ValueSome(struct (xsMid, xeMid)) ->
            let mutable xs = xsMid
            let mutable xe = xeMid

            // Tighten with band top.
            // Skip if band top is at or above the shape's bbox.Top — at the exact
            // boundary GetInterval typically returns ValueNone (e.g. circle apex).
            if bandTop > bbox.Top then
                match shape.GetInterval bandTop with
                | ValueSome(struct (s, e)) ->
                    xs <- max xs s
                    xe <- min xe e
                | ValueNone -> ()   // Top endpoint just outside shape — midpoint is already the guard

            // Tighten with band bottom.
            // Skip if band bottom is at or below bbox.Bottom for the same reason.
            if bandBottom < bbox.Bottom then
                match shape.GetInterval bandBottom with
                | ValueSome(struct (s, e)) ->
                    xs <- max xs s
                    xe <- min xe e
                | ValueNone -> ()   // Bottom endpoint just outside shape

            if xe > xs then ValueSome(struct (xs, xe)) else ValueNone

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

        // Loop condition: ensure the FULL line band (from cap-top to descender-bottom)
        // stays within the shape's bounding box.  Specifically:
        //   bandBottom = y − ascent + lineHeight ≤ bbox.Bottom
        // which is equivalent to: y ≤ bbox.Bottom − lineHeight + ascent
        // This prevents descenders from protruding below the shape boundary.
        while y - ascent + lineHeight <= bbox.Bottom && textIdx < text.Length do
            // ── Conservative scanline interval ────────────────────────────────
            // The text line occupies the vertical band [bandTop, bandBottom].
            // We query at the midpoint AND both band edges, taking the conservative
            // intersection (max xStart, min xEnd) so that the entire band — including
            // ascenders (at bandTop) and descenders (at bandBottom) — fits inside the
            // shape.  Without the endpoint tightening, glyphs in the upper half of a
            // circle overflow the left edge at the ascender height (band top).
            let bandTop    = y - ascent
            let bandBottom = y - ascent + lineHeight

            match getConservativeInterval shape bbox bandTop bandBottom with
            | ValueNone ->
                y <- y + lineHeight
            | ValueSome(struct (xStart, xEnd)) ->
                let remaining = text.Slice(textIdx)
                let struct (glyphsPlaced, charsConsumed) = layoutLine remaining fd scale xStart xEnd y buffer outIdx

                // Adjust ClusterIndex to absolute position in the original text.
                // layoutLine sets ClusterIndex relative to the text slice — add textIdx.
                for i in outIdx .. outIdx + glyphsPlaced - 1 do
                    let gp = buffer.[i]
                    buffer.[i] <- { gp with ClusterIndex = gp.ClusterIndex + textIdx }

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
