namespace GlyphAura

open System

/// Simple Latin shaper (Mode A). Resolves characters through cmap → GlyphID,
/// applies advance widths and kerning adjustments, returns ShapedGlyph[].
///
/// Kerning convention (matches HarfBuzz / OpenType spec):
///   XAdvance[i] = base_advance[i] + kern(glyph[i], glyph[i+1])
///
/// This "look-ahead" design means the kern adjustment is baked into the LEFT
/// glyph's advance, so the pen position after placing glyph[i] already accounts
/// for the pair adjustment and glyph[i+1]'s origin is simply the current pen.
module Shaper =

    /// Shape a line of text with optional width constraint.
    /// - text: input characters
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
        let maxGlyphs = min text.Length buffer.Length

        // First pass: resolve all glyph IDs so we can look ahead for kern
        let glyphIds = Array.zeroCreate<uint16> (min text.Length maxGlyphs)
        let mutable resolvedCount = 0
        for i in 0 .. text.Length - 1 do
            if i < maxGlyphs then
                glyphIds.[i] <- CMap.resolve cmap (uint32 text.[i])
                resolvedCount <- i + 1

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

                // Check width constraint
                if totalAdvance + advance > widthConstraint && widthConstraint < System.Single.MaxValue then
                    lastFitIndex <- max 0 (i - 1)
                    exceeded <- true
                else

                if outIdx < maxGlyphs then
                    buffer.[outIdx] <- {
                        GlyphId = glyphId
                        ClusterIndex = i
                        XAdvance = metrics.AdvanceWidth + kernAdj   // design units, kern to next
                        XOffset  = 0.0f
                        YOffset  = 0.0f
                    }
                    totalAdvance <- totalAdvance + advance
                    outIdx <- outIdx + 1

        {
            Glyphs = buffer
            GlyphCount = outIdx
            LastFitIndex = if exceeded then lastFitIndex else -1
            TotalAdvance = totalAdvance
        }
