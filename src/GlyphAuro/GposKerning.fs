namespace GlyphAuro

open System

/// GPOS Class-Based Kerning — Parse GPOS LookupType 2 (PairAdjustment) Format 2.
/// Replaces legacy kern with O(classes²) class-based kerning.
module GposKerning =

    /// GPOS kerning classes: flat value matrix for O(1) lookup.
    type KerningClasses = {
        LeftClassTable: uint16 array   // GlyphID → LeftClassID
        RightClassTable: uint16 array  // GlyphID → RightClassID
        ValueMatrix: int16 array       // flat row-major [leftClass * ColumnCount + rightClass]
        ColumnCount: int
    }

    /// Parse a ClassDef table (Format 1 or Format 2) into a flat GlyphID → ClassID array.
    let private parseClassDef (span: ReadOnlySpan<byte>) (offset: int) (maxGlyphs: int) : uint16 array =
        let table = Array.zeroCreate<uint16> maxGlyphs
        if offset + 4 > span.Length then table
        else

        let format = BinaryReader.readU16 span offset
        match int format with
        | 1 ->
            // Format 1: Array of class values for consecutive glyphs
            if offset + 6 > span.Length then table
            else
            let startGlyphID = int (BinaryReader.readU16 span (offset + 2))
            let glyphCount = int (BinaryReader.readU16 span (offset + 4))
            for i in 0 .. glyphCount - 1 do
                let glyphId = startGlyphID + i
                if glyphId < maxGlyphs && offset + 6 + i * 2 + 1 < span.Length then
                    table.[glyphId] <- BinaryReader.readU16 span (offset + 6 + i * 2)
            table

        | 2 ->
            // Format 2: Ranges of glyph IDs with class values
            if offset + 4 > span.Length then table
            else
            let classRangeCount = int (BinaryReader.readU16 span (offset + 2))
            for i in 0 .. classRangeCount - 1 do
                let rangeOff = offset + 4 + i * 6
                if rangeOff + 5 < span.Length then
                    let startGlyphId = int (BinaryReader.readU16 span rangeOff)
                    let endGlyphId = int (BinaryReader.readU16 span (rangeOff + 2))
                    let classValue = BinaryReader.readU16 span (rangeOff + 4)
                    for g in startGlyphId .. min endGlyphId (maxGlyphs - 1) do
                        table.[g] <- classValue
            table

        | _ -> table

    /// Count set bits in a uint16 value format to determine value record size.
    let private countValueFormatFields (vf: uint16) : int =
        let mutable n = int vf
        let mutable count = 0
        while n <> 0 do
            count <- count + (n &&& 1)
            n <- n >>> 1
        count

    /// Parse the GPOS table to extract class-based kerning.
    /// Finds the first LookupType 2, PosFormat 2 subtable.
    let parse (gposData: ReadOnlyMemory<byte>) (maxGlyphs: int) : KerningClasses voption =
        let span = gposData.Span
        if span.Length < 10 then ValueNone
        else

        // GPOS header
        let lookupListOffset = int (BinaryReader.readU16 span 8)
        if lookupListOffset + 2 > span.Length then ValueNone
        else

        let lookupCount = int (BinaryReader.readU16 span lookupListOffset)

        // Scan for the first LookupType 2 (PairAdjustment) with Format 2 (Class-based)
        let mutable result = ValueNone
        let mutable lookupIdx = 0
        while lookupIdx < lookupCount && result.IsNone do
            let lookupOff = lookupListOffset + int (BinaryReader.readU16 span (lookupListOffset + 2 + lookupIdx * 2))
            if lookupOff + 6 <= span.Length then
                let lookupType = BinaryReader.readU16 span lookupOff
                let subTableCount = int (BinaryReader.readU16 span (lookupOff + 4))

                if lookupType = 2us then  // PairAdjustment
                    let mutable subIdx = 0
                    while subIdx < subTableCount && result.IsNone do
                        let subTableOff = lookupOff + int (BinaryReader.readU16 span (lookupOff + 6 + subIdx * 2))
                        if subTableOff + 16 <= span.Length then
                            let posFormat = BinaryReader.readU16 span subTableOff

                            if posFormat = 2us then  // Class-based
                                let vf1 = BinaryReader.readU16 span (subTableOff + 4)
                                let vf2 = BinaryReader.readU16 span (subTableOff + 6)
                                let classDef1Off = subTableOff + int (BinaryReader.readU16 span (subTableOff + 8))
                                let classDef2Off = subTableOff + int (BinaryReader.readU16 span (subTableOff + 10))
                                let class1Count = int (BinaryReader.readU16 span (subTableOff + 12))
                                let class2Count = int (BinaryReader.readU16 span (subTableOff + 14))

                                // Parse class definition tables
                                let leftClasses = parseClassDef span classDef1Off maxGlyphs
                                let rightClasses = parseClassDef span classDef2Off maxGlyphs

                                // Parse value matrix
                                // ValueFormat determines how many int16 fields per value record
                                let vr1Fields = countValueFormatFields vf1
                                let vr2Fields = countValueFormatFields vf2
                                let recordSize = (vr1Fields + vr2Fields) * 2  // bytes per Class2Record

                                let matrixStart = subTableOff + 16
                                let valueMatrix = Array.zeroCreate<int16> (class1Count * class2Count)

                                // XAdvance is the 3rd bit (0x0004) in ValueFormat
                                // We need to find its position in the value record
                                let xAdvanceFieldIndex =
                                    let mutable idx = 0
                                    let mutable bits = int vf1
                                    let mutable found = -1
                                    while bits <> 0 && found < 0 do
                                        if idx = 2 then  // bit 2 = XAdvance
                                            if (bits &&& 1) = 1 then found <- idx
                                        else
                                            if (bits &&& 1) = 1 then ()
                                        bits <- bits >>> 1
                                        idx <- idx + 1
                                    // Count how many bits are set before bit 2
                                    let mutable pos = 0
                                    for b in 0 .. 1 do
                                        if (int vf1 >>> b) &&& 1 = 1 then pos <- pos + 1
                                    if (int vf1 &&& 0x0004) <> 0 then pos else -1

                                if xAdvanceFieldIndex >= 0 then
                                    for c1 in 0 .. class1Count - 1 do
                                        for c2 in 0 .. class2Count - 1 do
                                            let entryOff = matrixStart + (c1 * class2Count + c2) * recordSize + xAdvanceFieldIndex * 2
                                            if entryOff + 1 < span.Length then
                                                valueMatrix.[c1 * class2Count + c2] <- BinaryReader.readI16 span entryOff
                                    
                                    result <- ValueSome {
                                        LeftClassTable = leftClasses
                                        RightClassTable = rightClasses
                                        ValueMatrix = valueMatrix
                                        ColumnCount = class2Count
                                    }
                        subIdx <- subIdx + 1
            lookupIdx <- lookupIdx + 1

        result

    /// Get the XAdvance kerning adjustment for a pair of glyphs.
    /// O(1): 2 array lookups + 1 multiply + 1 array lookup.
    let inline getAdjustment (t: KerningClasses) (left: uint16) (right: uint16) : int16 =
        let li = int left
        let ri = int right
        if li < t.LeftClassTable.Length && ri < t.RightClassTable.Length then
            let lc = int t.LeftClassTable.[li]
            let rc = int t.RightClassTable.[ri]
            let idx = lc * t.ColumnCount + rc
            if idx < t.ValueMatrix.Length then
                t.ValueMatrix.[idx]
            else 0s
        else 0s
