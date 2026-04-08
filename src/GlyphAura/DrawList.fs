namespace GlyphAura

open System
open System.Buffers
open GlyphAura.Shapes
open GlyphAura.LayoutEngine

/// TextRun Output Format + DrawList.
/// Canonical output contract between Glyph-Auro and any SceneGraph consumer.
/// Struct-of-Arrays layout for WebGPU instanced rendering compatibility.
module DrawList =

    /// Struct-of-Arrays layout — maps directly to WebGPU vertex buffer format.
    type DrawList = {
        /// GlyphIDs for each placed glyph.
        GlyphIds: uint16 array
        /// X positions in pixel coordinates, origin top-left.
        XPositions: float32 array
        /// Y positions in pixel coordinates, origin top-left.
        YPositions: float32 array
        /// Tight bounding box encompassing only placed glyphs.
        TightBounds: Rectangle
        /// Deduplicated, sorted list of GlyphIDs the renderer must have in the atlas.
        GlyphRasterRequests: uint16 array
        /// Number of valid glyphs.
        GlyphCount: int
    }

    /// Create an empty DrawList.
    let empty : DrawList = {
        GlyphIds = Array.empty
        XPositions = Array.empty
        YPositions = Array.empty
        TightBounds = { Left = 0.0f; Top = 0.0f; Right = 0.0f; Bottom = 0.0f }
        GlyphRasterRequests = Array.empty
        GlyphCount = 0
    }

    /// Convert a LayoutResult to a DrawList (SoA layout).
    let fromLayoutResult (result: LayoutResult) : DrawList =
        let glyphSpan = result.Glyphs.Span
        let count = glyphSpan.Length
        if count = 0 then empty
        else

        let ids = Array.zeroCreate<uint16> count
        let xs = Array.zeroCreate<float32> count
        let ys = Array.zeroCreate<float32> count

        let mutable minX = Single.MaxValue
        let mutable minY = Single.MaxValue
        let mutable maxX = Single.MinValue
        let mutable maxY = Single.MinValue

        // Collect unique glyph IDs for raster requests
        // Use a simple sorted insert approach (no Dictionary allocation)
        let uniqueIds = Array.zeroCreate<uint16> count
        let mutable uniqueCount = 0

        for i in 0 .. count - 1 do
            let gp = glyphSpan.[i]
            ids.[i] <- gp.GlyphId
            xs.[i] <- gp.X
            ys.[i] <- gp.Y

            if gp.X < minX then minX <- gp.X
            if gp.Y < minY then minY <- gp.Y
            if gp.X > maxX then maxX <- gp.X
            if gp.Y > maxY then maxY <- gp.Y

            // Binary search insert into uniqueIds
            let gid = gp.GlyphId
            let mutable lo = 0
            let mutable hi = uniqueCount - 1
            let mutable found = false
            while lo <= hi do
                let mid = lo + (hi - lo) / 2
                if uniqueIds.[mid] = gid then
                    found <- true
                    lo <- hi + 1  // exit
                elif uniqueIds.[mid] < gid then
                    lo <- mid + 1
                else
                    hi <- mid - 1
            if not found then
                // Insert at lo position
                for j in uniqueCount - 1 .. -1 .. lo do
                    uniqueIds.[j + 1] <- uniqueIds.[j]
                uniqueIds.[lo] <- gid
                uniqueCount <- uniqueCount + 1

        let rasterRequests = Array.zeroCreate<uint16> uniqueCount
        Array.Copy(uniqueIds, rasterRequests, uniqueCount)

        {
            GlyphIds = ids
            XPositions = xs
            YPositions = ys
            TightBounds = { Left = minX; Top = minY; Right = maxX; Bottom = maxY }
            GlyphRasterRequests = rasterRequests
            GlyphCount = count
        }

    /// Serialize DrawList to a Memory<byte> for zero-copy transfer.
    let toBytes (dl: DrawList) : byte array =
        if dl.GlyphCount = 0 then Array.empty
        else
        // Layout: [glyphCount:4][glyphIds:2*n][xPositions:4*n][yPositions:4*n][bounds:16][rasterCount:4][rasterIds:2*m]
        let n = dl.GlyphCount
        let m = dl.GlyphRasterRequests.Length
        let totalSize = 4 + n * 2 + n * 4 + n * 4 + 16 + 4 + m * 2
        let buf = Array.zeroCreate<byte> totalSize
        let mutable offset = 0

        // GlyphCount (int32 LE)
        BitConverter.TryWriteBytes(Span(buf, offset, 4), n) |> ignore
        offset <- offset + 4

        // GlyphIds (uint16 LE)
        for i in 0 .. n - 1 do
            BitConverter.TryWriteBytes(Span(buf, offset, 2), dl.GlyphIds.[i]) |> ignore
            offset <- offset + 2

        // X positions (float32 LE)
        for i in 0 .. n - 1 do
            BitConverter.TryWriteBytes(Span(buf, offset, 4), dl.XPositions.[i]) |> ignore
            offset <- offset + 4

        // Y positions (float32 LE)
        for i in 0 .. n - 1 do
            BitConverter.TryWriteBytes(Span(buf, offset, 4), dl.YPositions.[i]) |> ignore
            offset <- offset + 4

        // Tight bounds (4 × float32 LE)
        BitConverter.TryWriteBytes(Span(buf, offset, 4), dl.TightBounds.Left) |> ignore
        offset <- offset + 4
        BitConverter.TryWriteBytes(Span(buf, offset, 4), dl.TightBounds.Top) |> ignore
        offset <- offset + 4
        BitConverter.TryWriteBytes(Span(buf, offset, 4), dl.TightBounds.Right) |> ignore
        offset <- offset + 4
        BitConverter.TryWriteBytes(Span(buf, offset, 4), dl.TightBounds.Bottom) |> ignore
        offset <- offset + 4

        // Raster request count
        BitConverter.TryWriteBytes(Span(buf, offset, 4), m) |> ignore
        offset <- offset + 4

        // Raster request GlyphIds
        for i in 0 .. m - 1 do
            BitConverter.TryWriteBytes(Span(buf, offset, 2), dl.GlyphRasterRequests.[i]) |> ignore
            offset <- offset + 2

        buf
