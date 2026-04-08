namespace GlyphAuro

open System

/// Legacy kern table parser — Format 0 (sorted pair list).
module KernLegacy =

    /// Pack two GlyphIDs into a single uint32 key for comparison.
    let inline kernKey (left: uint16) (right: uint16) : uint32 =
        (uint32 left <<< 16) ||| uint32 right

    /// Parse kern table Format 0 into sorted parallel arrays.
    let parse (kernData: ReadOnlyMemory<byte>) : KernTable voption =
        let span = kernData.Span
        if span.Length < 4 then ValueNone
        else

        let version = BinaryReader.readU16 span 0
        let nTables = int (BinaryReader.readU16 span 2)

        if nTables = 0 || version > 0us then ValueNone
        else

        // Parse first subtable only
        let subOffset = 4
        if span.Length < subOffset + 6 then ValueNone
        else

        let coverage = BinaryReader.readU16 span (subOffset + 4)
        let format = int (coverage >>> 8) &&& 0xFF

        // Only support Format 0
        if format <> 0 then ValueNone
        else

        if span.Length < subOffset + 8 then ValueNone
        else

        let nPairs = int (BinaryReader.readU16 span (subOffset + 6))

        // Header for format 0 subtable is: version(2) + length(2) + coverage(2) + nPairs(2) + searchRange(2) + entrySelector(2) + rangeShift(2) = 14 bytes
        let pairDataOffset = subOffset + 14

        if span.Length < pairDataOffset + nPairs * 6 then ValueNone
        else

        let keys = Array.zeroCreate<uint32> nPairs
        let values = Array.zeroCreate<int16> nPairs

        for i in 0 .. nPairs - 1 do
            let off = pairDataOffset + i * 6
            let left = BinaryReader.readU16 span off
            let right = BinaryReader.readU16 span (off + 2)
            let value = BinaryReader.readI16 span (off + 4)
            keys.[i] <- kernKey left right
            values.[i] <- value

        ValueSome { Keys = keys; Values = values }

    /// Get kerning adjustment for a glyph pair. Returns 0s for absent pairs.
    let getAdjustment (table: KernTable) (left: uint16) (right: uint16) : int16 =
        let key = kernKey left right
        let keys = table.Keys

        // Binary search
        let mutable lo = 0
        let mutable hi = keys.Length - 1
        let mutable result = 0s

        while lo <= hi do
            let mid = lo + (hi - lo) / 2
            let midKey = keys.[mid]
            if midKey = key then
                result <- table.Values.[mid]
                lo <- hi + 1 // exit
            elif midKey < key then
                lo <- mid + 1
            else
                hi <- mid - 1

        result

    /// Safe wrapper that handles ValueNone kern tables.
    let inline getAdjustmentOpt (table: KernTable voption) (left: uint16) (right: uint16) : int16 =
        match table with
        | ValueSome t -> getAdjustment t left right
        | ValueNone -> 0s
