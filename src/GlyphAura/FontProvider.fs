namespace GlyphAura

open System

/// Binary font parser. Reads a .ttf/.otf file into zero-copy table slices.
module FontProvider =

    /// Tag constants as uint32 for fast comparison (big-endian ASCII).
    [<Literal>]
    let private TagCmap = 0x636D6170u // "cmap"
    [<Literal>]
    let private TagHead = 0x68656164u // "head"
    [<Literal>]
    let private TagHhea = 0x68686561u // "hhea"
    [<Literal>]
    let private TagHmtx = 0x686D7478u // "hmtx"
    [<Literal>]
    let private TagMaxp = 0x6D617870u // "maxp"
    [<Literal>]
    let private TagKern = 0x6B65726Eu // "kern"
    [<Literal>]
    let private TagGpos = 0x47504F53u // "GPOS"

    /// Parse the OpenType table directory and extract table slices.
    let load (fontData: ReadOnlyMemory<byte>) : Result<FontTables * FontMetrics, string> =
        let span = fontData.Span
        if span.Length < 12 then
            Error "Font data too small for table directory"
        else

        let sfVersion = BinaryReader.readU32 span 0
        // Accept TrueType (0x00010000) and CFF (0x4F54544F = "OTTO")
        if sfVersion <> 0x00010000u && sfVersion <> 0x4F54544Fu then
            Error (sprintf "Unsupported sfVersion: 0x%08X" sfVersion)
        else

        let numTables = int (BinaryReader.readU16 span 4)
        let requiredSize = 12 + numTables * 16
        if span.Length < requiredSize then
            Error "Font data truncated in table directory"
        else

        // Parse table directory entries
        let mutable cmapOffset = -1
        let mutable cmapLength = 0
        let mutable headOffset = -1
        let mutable headLength = 0
        let mutable hheaOffset = -1
        let mutable hheaLength = 0
        let mutable hmtxOffset = -1
        let mutable hmtxLength = 0
        let mutable maxpOffset = -1
        let mutable maxpLength = 0
        let mutable kernOffset = -1
        let mutable kernLength = 0
        let mutable gposOffset = -1
        let mutable gposLength = 0

        for i in 0 .. numTables - 1 do
            let entryOffset = 12 + i * 16
            let tag = BinaryReader.readU32 span entryOffset
            let tOffset = int (BinaryReader.readU32 span (entryOffset + 8))
            let tLength = int (BinaryReader.readU32 span (entryOffset + 12))

            if tag = TagCmap then cmapOffset <- tOffset; cmapLength <- tLength
            elif tag = TagHead then headOffset <- tOffset; headLength <- tLength
            elif tag = TagHhea then hheaOffset <- tOffset; hheaLength <- tLength
            elif tag = TagHmtx then hmtxOffset <- tOffset; hmtxLength <- tLength
            elif tag = TagMaxp then maxpOffset <- tOffset; maxpLength <- tLength
            elif tag = TagKern then kernOffset <- tOffset; kernLength <- tLength
            elif tag = TagGpos then gposOffset <- tOffset; gposLength <- tLength

        // Validate required tables exist
        if cmapOffset < 0 then Error "Missing required table: cmap"
        elif headOffset < 0 then Error "Missing required table: head"
        elif hheaOffset < 0 then Error "Missing required table: hhea"
        elif hmtxOffset < 0 then Error "Missing required table: hmtx"
        elif maxpOffset < 0 then Error "Missing required table: maxp"
        else

        // Validate table bounds
        let totalLength = span.Length
        if headOffset + headLength > totalLength then Error "head table exceeds file bounds"
        elif hheaOffset + hheaLength > totalLength then Error "hhea table exceeds file bounds"
        elif maxpOffset + 6 > totalLength then Error "maxp table too small"
        elif headLength < 54 then Error "head table too small"
        elif hheaLength < 36 then Error "hhea table too small"
        else

        // Read FontMetrics from head, hhea, and maxp tables
        let headSpan = span.Slice(headOffset, headLength)
        let hheaSpan = span.Slice(hheaOffset, hheaLength)
        let maxpSpan = span.Slice(maxpOffset)

        let unitsPerEm = BinaryReader.readU16 headSpan 18
        let ascender = BinaryReader.readI16 hheaSpan 4
        let descender = BinaryReader.readI16 hheaSpan 6
        let lineGap = BinaryReader.readI16 hheaSpan 8
        let maxAdvanceWidth = BinaryReader.readU16 hheaSpan 10
        let minLeftSideBearing = BinaryReader.readI16 hheaSpan 12

        let metrics = {
            UnitsPerEm = unitsPerEm
            Ascender = ascender
            Descender = descender
            LineGap = lineGap
            MaxAdvanceWidth = maxAdvanceWidth
            MinLeftSideBearing = minLeftSideBearing
        }

        let tables = {
            Raw = fontData
            Cmap = fontData.Slice(cmapOffset, cmapLength)
            Head = fontData.Slice(headOffset, headLength)
            Hhea = fontData.Slice(hheaOffset, hheaLength)
            Hmtx = fontData.Slice(hmtxOffset, hmtxLength)
            Maxp = fontData.Slice(maxpOffset, min maxpLength (totalLength - maxpOffset))
            Kern = if kernOffset >= 0 then ValueSome(fontData.Slice(kernOffset, kernLength)) else ValueNone
            Gpos = if gposOffset >= 0 then ValueSome(fontData.Slice(gposOffset, gposLength)) else ValueNone
        }

        Ok(tables, metrics)
