namespace GlyphAura

open System

/// cmap table parser — Unicode codepoint to GlyphID mapping.
/// Supports Format 4 (BMP) and Format 12 (full Unicode).
module CMap =

    /// Parse cmap Format 4 (BMP segment mapping).
    let private parseFormat4 (cmapData: ReadOnlyMemory<byte>) (subtableOffset: int) : CMapTable =
        let span = cmapData.Span.Slice(subtableOffset)
        if span.Length < 14 then Unsupported
        else

        let segCountX2 = int (BinaryReader.readU16 span 6)
        let segCount = segCountX2 / 2

        // Offsets within the subtable
        let endCodeBase = 14
        let startCodeBase = endCodeBase + segCountX2 + 2 // +2 for reservedPad
        let idDeltaBase = startCodeBase + segCountX2
        let idRangeOffsetBase = idDeltaBase + segCountX2

        let requiredLength = idRangeOffsetBase + segCountX2
        if span.Length < requiredLength then Unsupported
        else

        // Cannot use Array.init with span (byref) — use mutable loop
        let segments = Array.zeroCreate<CMapSegment> segCount
        for i in 0 .. segCount - 1 do
            let endCode = BinaryReader.readU16 span (endCodeBase + i * 2)
            let startCode = BinaryReader.readU16 span (startCodeBase + i * 2)
            let idDelta = BinaryReader.readI16 span (idDeltaBase + i * 2)
            let idRangeOffset = BinaryReader.readU16 span (idRangeOffsetBase + i * 2)

            let glyphIdArrayBase =
                if idRangeOffset = 0us then 0
                else
                    subtableOffset + idRangeOffsetBase + i * 2 + int idRangeOffset

            segments.[i] <- {
                EndCode = endCode
                StartCode = startCode
                IdDelta = idDelta
                IdRangeOffset = idRangeOffset
                GlyphIdArrayBase = glyphIdArrayBase
            }

        Format4(segments, cmapData)

    /// Parse cmap Format 12 (full Unicode sequential map groups).
    let private parseFormat12 (span: ReadOnlySpan<byte>) : CMapTable =
        if span.Length < 16 then Unsupported
        else

        let numGroups = int (BinaryReader.readU32 span 12)
        let groupsOffset = 16

        if span.Length < groupsOffset + numGroups * 12 then Unsupported
        else

        // Cannot use Array.init with span (byref) — use mutable loop
        let groups = Array.zeroCreate<CMapGroup> numGroups
        for i in 0 .. numGroups - 1 do
            let off = groupsOffset + i * 12
            groups.[i] <- {
                StartCharCode = BinaryReader.readU32 span off
                EndCharCode = BinaryReader.readU32 span (off + 4)
                StartGlyphId = BinaryReader.readU32 span (off + 8)
            }

        Format12 groups

    /// Parse the cmap table, selecting the best subtable.
    /// Preference: platformID=3,encodingID=10 (Unicode full), fallback platformID=3,encodingID=1 (BMP).
    let parse (cmapData: ReadOnlyMemory<byte>) : CMapTable =
        let span = cmapData.Span
        if span.Length < 4 then Unsupported
        else

        let numSubtables = int (BinaryReader.readU16 span 2)
        let mutable bestOffset = -1
        let mutable bestPriority = 0 // higher = better

        for i in 0 .. numSubtables - 1 do
            let recordOff = 4 + i * 8
            if recordOff + 8 <= span.Length then
                let platformID = BinaryReader.readU16 span recordOff
                let encodingID = BinaryReader.readU16 span (recordOff + 2)
                let subtableOffset = int (BinaryReader.readU32 span (recordOff + 4))

                // Priority: platformID=3, encodingID=10 (full Unicode) > platformID=3, encodingID=1 (BMP)
                // Also accept platformID=0, encodingID=3 or encodingID=4
                let priority =
                    if platformID = 3us && encodingID = 10us then 4
                    elif platformID = 0us && encodingID = 4us then 3
                    elif platformID = 3us && encodingID = 1us then 2
                    elif platformID = 0us && encodingID = 3us then 1
                    else 0

                if priority > bestPriority && subtableOffset < span.Length then
                    bestPriority <- priority
                    bestOffset <- subtableOffset

        if bestOffset < 0 then Unsupported
        else

        let subtableSpan = span.Slice(bestOffset)
        if subtableSpan.Length < 2 then Unsupported
        else

        let format = BinaryReader.readU16 subtableSpan 0

        match int format with
        | 4 -> parseFormat4 cmapData bestOffset
        | 12 -> parseFormat12 subtableSpan
        | _ -> Unsupported

    /// Binary search on Format 4 EndCode segments.
    let private binarySearchFormat4 (segments: CMapSegment array) (codepoint: uint16) : int =
        let mutable lo = 0
        let mutable hi = segments.Length - 1
        let mutable result = -1
        while lo <= hi do
            let mid = lo + (hi - lo) / 2
            if segments.[mid].EndCode < codepoint then
                lo <- mid + 1
            else
                result <- mid
                hi <- mid - 1
        result

    /// Resolve a Unicode codepoint to a GlyphID. Returns 0 (.notdef) for unmapped codepoints.
    let resolve (table: CMapTable) (codepoint: uint32) : uint16 =
        match table with
        | Unsupported -> 0us
        | Format4(segments, cmapData) ->
            if codepoint > 0xFFFFu then 0us
            else
            let cp = uint16 codepoint
            let idx = binarySearchFormat4 segments cp
            if idx < 0 then 0us
            else
            let seg = segments.[idx]
            if cp < seg.StartCode then 0us
            elif seg.IdRangeOffset = 0us then
                uint16 (int cp + int seg.IdDelta)
            else
                // Look up in glyphIdArray
                let glyphIdOffset = seg.GlyphIdArrayBase + (int cp - int seg.StartCode) * 2
                let cmapSpan = cmapData.Span
                if glyphIdOffset + 1 >= cmapSpan.Length || glyphIdOffset < 0 then 0us
                else
                let glyphId = BinaryReader.readU16 cmapSpan glyphIdOffset
                if glyphId = 0us then 0us
                else uint16 (int glyphId + int seg.IdDelta)

        | Format12 groups ->
            // Binary search for supplementary plane support
            let mutable lo = 0
            let mutable hi = groups.Length - 1
            let mutable result = 0us
            let mutable found = false
            while lo <= hi && not found do
                let mid = lo + (hi - lo) / 2
                let g = groups.[mid]
                if codepoint < g.StartCharCode then
                    hi <- mid - 1
                elif codepoint > g.EndCharCode then
                    lo <- mid + 1
                else
                    result <- uint16 (g.StartGlyphId + (codepoint - g.StartCharCode))
                    found <- true
            result
