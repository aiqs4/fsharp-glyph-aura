namespace GlyphAura

open System

/// Unicode-aware shaper. Resolves codepoints (including UTF-16 surrogate pairs)
/// through cmap → GlyphID, applies advance widths and kerning adjustments,
/// returns ShapedGlyph[].
///
/// Kerning convention (matches HarfBuzz / OpenType spec):
///   XAdvance[i] = base_advance[i] + kern(glyph[i], glyph[i+1])
///
/// This "look-ahead" design means the kern adjustment is baked into the LEFT
/// glyph's advance, so the pen position after placing glyph[i] already accounts
/// for the pair adjustment and glyph[i+1]'s origin is simply the current pen.
///
/// Surrogate pair handling: the shaper decodes UTF-16 surrogate pairs into
/// full Unicode codepoints before resolving glyph IDs.  A surrogate pair
/// (two UTF-16 code units) produces exactly ONE glyph.  ClusterIndex is
/// the offset of the first char unit of the codepoint in the source text,
/// matching the HarfBuzz "cluster" semantics.
///
/// Supported scripts: any script whose glyphs fit in the font's cmap.
/// Complex scripts (Arabic, Indic) that require GSUB ligature substitution
/// will fall back to unsubstituted logical-order glyphs (no ligatures / joining
/// forms). RTL visual reordering is not performed by the shaper; the caller
/// (LayoutEngine) is responsible for RTL line mirroring when implemented.
module Shaper =

    /// Shape a line of text with optional width constraint.
    /// - text: input characters (UTF-16, may include surrogate pairs)
    /// - cmap: parsed cmap table for Unicode → GlyphID mapping
    /// - hmtx: parsed hmtx table for advance widths
    /// - kern: optional parsed kern table for pair adjustments
    /// - scale: conversion factor from design units to pixels (fontSize / unitsPerEm)
    /// - widthConstraint: maximum advance in scaled units (float32.MaxValue = unconstrained)
    /// - buffer: caller-provided buffer to write glyphs into
    let shapeLine
        (text: ReadOnlySpan<char>)
        (cmap: CMapTable)
        (hmtx: HmtxTable)
        (kern: KernTable voption)
        (scale: float32)
        (widthConstraint: float32)
        (buffer: ShapedGlyph array)
        : ShapingResult =

        let mutable outIdx = 0
        let mutable totalAdvance = 0.0f
        let mutable lastFitIndex = -1
        let mutable exceeded = false
        // Upper bound: number of codepoints ≤ number of chars (surrogates reduce count)
        let maxGlyphs = min text.Length buffer.Length

        // First pass: decode all codepoints (handling UTF-16 surrogate pairs) and
        // resolve each to a GlyphID.  Store both the glyph IDs and the char-space
        // cluster index (position of the first char unit of each codepoint).
        let glyphIds     = Array.zeroCreate<uint16> maxGlyphs
        let clusterIdxs  = Array.zeroCreate<int>    maxGlyphs
        let mutable resolvedCount = 0
        let mutable charIdx = 0
        while charIdx < text.Length && resolvedCount < maxGlyphs do
            let struct (cp, cpLen) = TextAnalyzer.decodeCodepoint text charIdx
            glyphIds.[resolvedCount]   <- CMap.resolve cmap (uint32 cp)
            clusterIdxs.[resolvedCount] <- charIdx
            resolvedCount <- resolvedCount + 1
            charIdx <- charIdx + cpLen

        for i in 0 .. resolvedCount - 1 do
            if not exceeded then
                let glyphId  = glyphIds.[i]
                let metrics  = Hmtx.getMetrics hmtx glyphId

                // Look-ahead kern: kern(current → next), applied to CURRENT glyph's advance.
                // This is the OpenType / HarfBuzz convention: the LEFT glyph's advance
                // is shortened (or lengthened) by the pair-kern so that the RIGHT glyph's
                // origin is simply the current pen position after this advance.
                let kernAdj =
                    if i + 1 < resolvedCount then
                        float32 (KernLegacy.getAdjustmentOpt kern glyphId glyphIds.[i + 1])
                    else
                        0.0f

                let advance = (metrics.AdvanceWidth + kernAdj) * scale

                // Check width constraint.  lastFitIndex is recorded in CHAR space
                // so that the LayoutEngine can use it as an index into the source text.
                if totalAdvance + advance > widthConstraint && widthConstraint < System.Single.MaxValue then
                    lastFitIndex <- if i > 0 then clusterIdxs.[i - 1] else 0
                    exceeded <- true
                else

                if outIdx < maxGlyphs then
                    buffer.[outIdx] <- {
                        GlyphId      = glyphId
                        // ClusterIndex = offset of the FIRST UTF-16 char unit of this
                        // codepoint in the source text.  For BMP codepoints this is the
                        // same as the char index.  For supplementary-plane codepoints
                        // (surrogate pairs) it is the index of the HIGH surrogate, exactly
                        // matching HarfBuzz's "cluster value" semantics.
                        ClusterIndex = clusterIdxs.[i]  // char-space offset in source text
                        XAdvance     = metrics.AdvanceWidth + kernAdj   // design units, kern to next
                        XOffset      = 0.0f
                        YOffset      = 0.0f
                    }
                    totalAdvance <- totalAdvance + advance
                    outIdx <- outIdx + 1

        {
            Glyphs = buffer
            GlyphCount = outIdx
            LastFitIndex = if exceeded then lastFitIndex else -1
            TotalAdvance = totalAdvance
        }
