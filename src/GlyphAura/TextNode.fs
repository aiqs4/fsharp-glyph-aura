namespace GlyphAura

open System
open System.Numerics
open GlyphAura.Shapes
open GlyphAura.DrawList
open GlyphAura.ShapeCache
open GlyphAura.LayoutEngine

/// TextNode — the primary interactive SceneGraph entity.
/// Manages dirty flags, caching, hit-testing, and caret geometry.
module TextNode =

    /// A TextNode for the SceneGraph. Manages text layout within a shape.
    type TextNode = {
        mutable Text: string
        mutable FontSizePx: float32
        mutable Shape: ISdfShape
        mutable IsDirty: bool
        mutable Transform: Matrix3x2
        mutable CachedDrawList: DrawList voption
        mutable CachedKey: CacheKey
        Font: FontTables
        Metrics: FontMetrics
        FontId: uint64
    }

    /// Create a new TextNode.
    let create (font: FontTables) (metrics: FontMetrics) (fontId: uint64) (shape: ISdfShape) (text: string) (fontSizePx: float32) : TextNode =
        {
            Text = text
            FontSizePx = fontSizePx
            Shape = shape
            IsDirty = true
            Transform = Matrix3x2.Identity
            CachedDrawList = ValueNone
            CachedKey = Unchecked.defaultof<CacheKey>
            Font = font
            Metrics = metrics
            FontId = fontId
        }

    /// Set the text content. Marks the node as dirty.
    let setText (node: TextNode) (text: string) : unit =
        if node.Text <> text then
            node.Text <- text
            node.IsDirty <- true

    /// Set the font size. Marks the node as dirty.
    let setFontSize (node: TextNode) (size: float32) : unit =
        if node.FontSizePx <> size then
            node.FontSizePx <- size
            node.IsDirty <- true

    /// Set the shape. Marks the node as dirty.
    let setShape (node: TextNode) (shape: ISdfShape) : unit =
        node.Shape <- shape
        node.IsDirty <- true

    /// Set the transform (position/rotation only). Does NOT mark dirty — no re-layout needed.
    let setTransform (node: TextNode) (transform: Matrix3x2) : unit =
        node.Transform <- transform

    /// Build the cache key for the current node state.
    let private buildKey (node: TextNode) : CacheKey =
        let textHash = fnv1aHash node.Text
        let bbox = node.Shape.BoundingBox
        let shapeId = shapeHash 0 bbox.Left bbox.Top bbox.Right bbox.Bottom
        {
            FontId = node.FontId
            TextHash = textHash
            ShapeId = shapeId
            FontSizePx = node.FontSizePx
        }

    /// Update the TextNode: re-layout if dirty, using cache to avoid redundant work.
    let rec update (node: TextNode) (cache: ShapeCache) : DrawList =
        if not node.IsDirty then
            match node.CachedDrawList with
            | ValueSome dl -> dl
            | ValueNone ->
                node.IsDirty <- true
                update node cache  // recursive call to force layout
        else

        let key = buildKey node

        // Try cache first
        match cache.TryGet(key) with
        | ValueSome dl ->
            node.CachedDrawList <- ValueSome dl
            node.CachedKey <- key
            node.IsDirty <- false
            dl
        | ValueNone ->

        // Perform layout
        let req = {
            Text = ReadOnlyMemory(node.Text.ToCharArray())
            Shape = node.Shape
            Font = node.Font
            Metrics = node.Metrics
            FontSizePx = node.FontSizePx
            Mode = Flow
            Alignment = Left
        }
        let result = LayoutEngine.layout req
        let dl = DrawList.fromLayoutResult result

        // Return ArrayPool buffer
        if result.GlyphBuffer.Length > 0 then
            System.Buffers.ArrayPool<GlyphPosition>.Shared.Return(result.GlyphBuffer, false)

        // Cache the result
        cache.Put(key, dl)
        node.CachedDrawList <- ValueSome dl
        node.CachedKey <- key
        node.IsDirty <- false
        dl

    // ---- Hit-Testing ----

    /// Hit-test: find the glyph index at a given pixel position.
    /// Returns the glyph index (into DrawList arrays) or -1 if no hit.
    /// glyphAdvances: array of advance widths (in pixels) for each glyph, needed for hit-test width.
    let hitTest (dl: DrawList) (pointX: float32) (pointY: float32) (lineHeight: float32) (glyphAdvances: float32 array) : int =
        let mutable closest = -1
        let mutable closestDist = Single.MaxValue

        for i in 0 .. dl.GlyphCount - 1 do
            let gx = dl.XPositions.[i]
            let gy = dl.YPositions.[i]
            // Check if point is on the same line (within lineHeight)
            let dy = abs (pointY - gy)
            if dy <= lineHeight then
                // Check if point is within the glyph's horizontal extent
                let advance = if i < glyphAdvances.Length then glyphAdvances.[i] else 10.0f
                if pointX >= gx && pointX < gx + advance then
                    let dist = abs (pointX - gx) + dy * 0.1f  // prioritize horizontal closeness
                    if dist < closestDist then
                        closestDist <- dist
                        closest <- i

        closest

    /// Find the caret position (X, Y, height) for a given text index.
    /// Returns the X position between glyphs for a blinking cursor.
    let caretPosition (dl: DrawList) (textIndex: int) (lineHeight: float32) (glyphAdvances: float32 array) : struct (float32 * float32 * float32) =
        if dl.GlyphCount = 0 then
            struct (0.0f, 0.0f, lineHeight)
        elif textIndex >= dl.GlyphCount then
            // After last glyph
            let lastX = dl.XPositions.[dl.GlyphCount - 1]
            let lastY = dl.YPositions.[dl.GlyphCount - 1]
            let advance = if dl.GlyphCount - 1 < glyphAdvances.Length then glyphAdvances.[dl.GlyphCount - 1] else 10.0f
            struct (lastX + advance, lastY, lineHeight)
        elif textIndex < 0 then
            struct (dl.XPositions.[0], dl.YPositions.[0], lineHeight)
        else
            struct (dl.XPositions.[textIndex], dl.YPositions.[textIndex], lineHeight)

    /// Get selection geometry: returns (x, y, width, height) rectangles for a selection range.
    /// Each rectangle covers one line of the selection.
    let selectionRects (dl: DrawList) (startIdx: int) (endIdx: int) (lineHeight: float32) (glyphAdvances: float32 array) : struct (float32 * float32 * float32 * float32) array =
        if dl.GlyphCount = 0 || startIdx >= endIdx then Array.empty
        else

        let si = max 0 startIdx
        let ei = min (dl.GlyphCount - 1) (endIdx - 1)

        // Group selected glyphs by line (Y position)
        let rects = Array.zeroCreate<struct (float32 * float32 * float32 * float32)> (ei - si + 1)
        let mutable rectCount = 0
        let mutable lineStartX = dl.XPositions.[si]
        let mutable lineY = dl.YPositions.[si]
        let mutable lineEndX = lineStartX

        for i in si .. ei do
            let gx = dl.XPositions.[i]
            let gy = dl.YPositions.[i]
            let advance = if i < glyphAdvances.Length then glyphAdvances.[i] else 10.0f

            if abs (gy - lineY) > lineHeight * 0.5f then
                // New line — emit previous line rect
                if rectCount < rects.Length then
                    rects.[rectCount] <- struct (lineStartX, lineY - lineHeight * 0.2f, lineEndX - lineStartX, lineHeight)
                    rectCount <- rectCount + 1
                lineStartX <- gx
                lineY <- gy

            lineEndX <- gx + advance

        // Emit last line
        if rectCount < rects.Length then
            rects.[rectCount] <- struct (lineStartX, lineY - lineHeight * 0.2f, lineEndX - lineStartX, lineHeight)
            rectCount <- rectCount + 1

        Array.sub rects 0 rectCount
