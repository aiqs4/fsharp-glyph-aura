namespace GlyphAuro

open System

/// Simple Latin shaper (Mode A). Resolves characters through cmap → GlyphID,
/// applies advance widths and legacy kern adjustments, returns ShapedGlyph[].
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
        let mutable prevGlyphId = 0us
        let mutable lastFitIndex = -1
        let mutable exceeded = false
        let maxGlyphs = min text.Length buffer.Length

        for i in 0 .. text.Length - 1 do
            if not exceeded then
                let codepoint = uint32 text.[i]
                let glyphId = CMap.resolve cmap codepoint
                let metrics = Hmtx.getMetrics hmtx glyphId

                // Apply kerning adjustment from previous glyph
                let kernAdj =
                    if outIdx > 0 then
                        float32 (KernLegacy.getAdjustmentOpt kern prevGlyphId glyphId)
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
                        XAdvance = metrics.AdvanceWidth + kernAdj
                        XOffset = 0.0f
                        YOffset = 0.0f
                    }
                    totalAdvance <- totalAdvance + advance
                    prevGlyphId <- glyphId
                    outIdx <- outIdx + 1

        {
            Glyphs = buffer
            GlyphCount = outIdx
            LastFitIndex = if exceeded then lastFitIndex else -1
            TotalAdvance = totalAdvance
        }
