namespace GlyphAura

open System
open System.Runtime.InteropServices

/// Font metrics from hhea + maxp tables.
[<Struct; StructLayout(LayoutKind.Sequential)>]
type FontMetrics = {
    UnitsPerEm: uint16
    Ascender: int16
    Descender: int16
    LineGap: int16
    MaxAdvanceWidth: uint16
    MinLeftSideBearing: int16
}

/// Table record from the OpenType table directory.
[<Struct>]
type TableRecord = {
    Tag: uint32
    Checksum: uint32
    Offset: int
    Length: int
}

/// Parsed font tables as ReadOnlyMemory<byte> slices into the original buffer — zero copy.
type FontTables = {
    Raw: ReadOnlyMemory<byte>
    Cmap: ReadOnlyMemory<byte>
    Head: ReadOnlyMemory<byte>
    Hhea: ReadOnlyMemory<byte>
    Hmtx: ReadOnlyMemory<byte>
    Maxp: ReadOnlyMemory<byte>
    Kern: ReadOnlyMemory<byte> voption
    Gpos: ReadOnlyMemory<byte> voption
}

/// cmap Format 4 segment for BMP character mapping.
[<Struct>]
type CMapSegment = {
    EndCode: uint16
    StartCode: uint16
    IdDelta: int16
    IdRangeOffset: uint16
    /// Absolute offset into the cmap subtable data for glyphIdArray lookups.
    GlyphIdArrayBase: int
}

/// cmap Format 12 sequential map group.
[<Struct>]
type CMapGroup = {
    StartCharCode: uint32
    EndCharCode: uint32
    StartGlyphId: uint32
}

/// Parsed cmap table representation.
type CMapTable =
    | Format4 of segments: CMapSegment array * cmapData: ReadOnlyMemory<byte>
    | Format12 of groups: CMapGroup array
    | Unsupported

/// Horizontal glyph metrics (advance width and left side bearing).
[<Struct>]
type GlyphMetrics = {
    AdvanceWidth: float32
    LeftSideBearing: float32
}

/// Horizontal metrics table backed by a flat float32 array for cache locality.
/// Index = GlyphID * 2; [0] = AdvanceWidth, [1] = LSB.
type HmtxTable = {
    Data: float32 array
}

/// Legacy kern table with parallel sorted arrays for O(log n) binary search lookup.
type KernTable = {
    Keys: uint32 array
    Values: int16 array
}

/// A single shaped glyph with position information.
[<Struct>]
type ShapedGlyph = {
    GlyphId: uint16
    ClusterIndex: int
    XAdvance: float32
    XOffset: float32
    YOffset: float32
}

/// Result of shaping a line of text.
type ShapingResult = {
    Glyphs: ShapedGlyph array
    GlyphCount: int
    LastFitIndex: int
    TotalAdvance: float32
}
