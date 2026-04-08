namespace GlyphAuro

open System

/// hmtx table parser — horizontal glyph metrics.
module Hmtx =

    /// Parse the hmtx table using numberOfHMetrics from the hhea table.
    /// Returns a flat float32 array for O(1) lookup by GlyphID.
    let parse (hmtxData: ReadOnlyMemory<byte>) (hheaData: ReadOnlyMemory<byte>) (numGlyphs: int) : HmtxTable =
        let hmtxSpan = hmtxData.Span
        let hheaSpan = hheaData.Span

        // numberOfHMetrics is at offset 34 in hhea table
        let numberOfHMetrics = int (BinaryReader.readU16 hheaSpan 34)

        // Allocate flat interleaved array: [aw0, lsb0, aw1, lsb1, ...]
        let data = Array.zeroCreate<float32> (numGlyphs * 2)

        // Read full horizontal metrics (advanceWidth + leftSideBearing pairs)
        let metricsToRead = min numberOfHMetrics numGlyphs
        for i in 0 .. metricsToRead - 1 do
            let offset = i * 4
            if offset + 3 < hmtxSpan.Length then
                let aw = float32 (BinaryReader.readU16 hmtxSpan offset)
                let lsb = float32 (BinaryReader.readI16 hmtxSpan (offset + 2))
                data.[i * 2] <- aw
                data.[i * 2 + 1] <- lsb

        // Remaining glyphs share the last advanceWidth, with individual LSBs
        if numberOfHMetrics > 0 && numberOfHMetrics < numGlyphs then
            let lastAW = data.[(numberOfHMetrics - 1) * 2]
            let lsbBaseOffset = numberOfHMetrics * 4
            for i in numberOfHMetrics .. numGlyphs - 1 do
                data.[i * 2] <- lastAW
                let lsbOffset = lsbBaseOffset + (i - numberOfHMetrics) * 2
                if lsbOffset + 1 < hmtxSpan.Length then
                    data.[i * 2 + 1] <- float32 (BinaryReader.readI16 hmtxSpan lsbOffset)

        { Data = data }

    /// Get metrics for a specific GlyphID in O(1).
    let inline getMetrics (table: HmtxTable) (glyphId: uint16) : GlyphMetrics =
        let idx = int glyphId * 2
        if idx + 1 < table.Data.Length then
            { AdvanceWidth = table.Data.[idx]; LeftSideBearing = table.Data.[idx + 1] }
        else
            { AdvanceWidth = 0.0f; LeftSideBearing = 0.0f }
