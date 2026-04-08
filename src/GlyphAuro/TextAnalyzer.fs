namespace GlyphAuro

open System

/// Grapheme Cluster Analysis + Bidi Stub.
/// Implements Unicode TR#29 grapheme cluster boundaries and basic LTR/RTL run detection.
module TextAnalyzer =

    /// A text run with direction information.
    [<Struct>]
    type TextRun = {
        StartIndex: int
        Length: int
        IsRTL: bool
    }

    // Unicode General Category helpers for cluster boundary detection.
    // Implements a subset of TR#29 needed for Latin, emoji, and common scripts.

    /// Check if a codepoint is a combining mark (Mn, Mc, Me categories).
    let private isCombiningMark (cp: int) : bool =
        // Combining Diacritical Marks: U+0300–U+036F
        (cp >= 0x0300 && cp <= 0x036F)
        // Combining Diacritical Marks Extended: U+1AB0–U+1AFF
        || (cp >= 0x1AB0 && cp <= 0x1AFF)
        // Combining Diacritical Marks Supplement: U+1DC0–U+1DFF
        || (cp >= 0x1DC0 && cp <= 0x1DFF)
        // Combining Diacritical Marks for Symbols: U+20D0–U+20FF
        || (cp >= 0x20D0 && cp <= 0x20FF)
        // Combining Half Marks: U+FE20–U+FE2F
        || (cp >= 0xFE20 && cp <= 0xFE2F)
        // General Category Mn in common blocks
        || (cp >= 0x0483 && cp <= 0x0489)  // Cyrillic combining
        || (cp >= 0x0591 && cp <= 0x05BD)  // Hebrew combining
        || (cp = 0x05BF)
        || (cp >= 0x05C1 && cp <= 0x05C2)
        || (cp >= 0x05C4 && cp <= 0x05C5)
        || (cp = 0x05C7)
        || (cp >= 0x0610 && cp <= 0x061A)  // Arabic combining
        || (cp >= 0x064B && cp <= 0x065F)
        || (cp = 0x0670)
        || (cp >= 0x06D6 && cp <= 0x06DC)
        || (cp >= 0x06DF && cp <= 0x06E4)
        || (cp >= 0x06E7 && cp <= 0x06E8)
        || (cp >= 0x06EA && cp <= 0x06ED)
        // Devanagari/Indic combining marks
        || (cp >= 0x0900 && cp <= 0x0903)
        || (cp >= 0x093A && cp <= 0x094F)
        || (cp >= 0x0951 && cp <= 0x0957)
        || (cp >= 0x0962 && cp <= 0x0963)
        // Thai combining marks
        || (cp >= 0x0E31 && cp <= 0x0E3A)
        || (cp >= 0x0E47 && cp <= 0x0E4E)

    /// Check if a codepoint is a ZWJ (Zero Width Joiner).
    let private isZWJ (cp: int) : bool = cp = 0x200D

    /// Check if a codepoint is a Variation Selector (VS1-VS16).
    let private isVariationSelector (cp: int) : bool =
        (cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF)

    /// Check if a codepoint is a Regional Indicator (flag emoji).
    let private isRegionalIndicator (cp: int) : bool =
        cp >= 0x1F1E6 && cp <= 0x1F1FF

    /// Check if a codepoint is an Emoji Modifier (skin tone).
    let private isEmojiModifier (cp: int) : bool =
        cp >= 0x1F3FB && cp <= 0x1F3FF

    /// Check if a codepoint is an Extended_Pictographic (emoji base).
    let private isExtendedPictographic (cp: int) : bool =
        // Common emoji ranges
        (cp >= 0x1F600 && cp <= 0x1F64F)   // Emoticons
        || (cp >= 0x1F300 && cp <= 0x1F5FF) // Misc Symbols and Pictographs
        || (cp >= 0x1F680 && cp <= 0x1F6FF) // Transport and Map
        || (cp >= 0x1F900 && cp <= 0x1F9FF) // Supplemental Symbols
        || (cp >= 0x1FA00 && cp <= 0x1FA6F) // Chess Symbols
        || (cp >= 0x1FA70 && cp <= 0x1FAFF) // Symbols Extended-A
        || (cp >= 0x2600 && cp <= 0x26FF)   // Misc Symbols
        || (cp >= 0x2700 && cp <= 0x27BF)   // Dingbats
        || (cp = 0x200D)                     // ZWJ itself
        || (cp >= 0x2300 && cp <= 0x23FF)   // Misc Technical
        || (cp >= 0x2190 && cp <= 0x21FF)   // Arrows
        || (cp >= 0x2B50 && cp <= 0x2B55)   // Stars
        || cp = 0x203C || cp = 0x2049       // Double exclamation, etc.
        || cp = 0x20E3                       // Combining enclosing keycap
        || (cp >= 0xFE00 && cp <= 0xFE0F)   // Variation selectors

    /// Determine if a codepoint is in a strong RTL script (Arabic, Hebrew, etc.).
    let private isRTL (cp: int) : bool =
        // Hebrew: U+0590–U+05FF
        (cp >= 0x0590 && cp <= 0x05FF)
        // Arabic: U+0600–U+06FF
        || (cp >= 0x0600 && cp <= 0x06FF)
        // Arabic Supplement: U+0750–U+077F
        || (cp >= 0x0750 && cp <= 0x077F)
        // Arabic Extended-A: U+08A0–U+08FF
        || (cp >= 0x08A0 && cp <= 0x08FF)
        // Arabic Presentation Forms-A: U+FB50–U+FDFF
        || (cp >= 0xFB50 && cp <= 0xFDFF)
        // Arabic Presentation Forms-B: U+FE70–U+FEFF
        || (cp >= 0xFE70 && cp <= 0xFEFF)
        // RTL marks
        || cp = 0x200F  // RTL mark
        || cp = 0x202B  // RTL embedding
        || cp = 0x202E  // RTL override
        || cp = 0x2067  // RTL isolate

    /// Check if a codepoint is a Hangul syllable component.
    let private isHangulL (cp: int) = cp >= 0x1100 && cp <= 0x115F   // Leading Jamo
    let private isHangulV (cp: int) = cp >= 0x1160 && cp <= 0x11A7   // Vowel Jamo
    let private isHangulT (cp: int) = cp >= 0x11A8 && cp <= 0x11FF   // Trailing Jamo
    let private isHangulLV (cp: int) =
        // Precomposed LV syllables (SBase + (L*NCount))
        if cp >= 0xAC00 && cp <= 0xD7A3 then
            (cp - 0xAC00) % 28 = 0
        else false
    let private isHangulLVT (cp: int) =
        if cp >= 0xAC00 && cp <= 0xD7A3 then
            (cp - 0xAC00) % 28 <> 0
        else false

    /// Decode a single codepoint from UTF-16 span at given index.
    /// Returns the codepoint and the number of char units consumed (1 or 2).
    let inline decodeCodepoint (text: ReadOnlySpan<char>) (i: int) : struct (int * int) =
        let c = text.[i]
        if Char.IsHighSurrogate(c) && i + 1 < text.Length && Char.IsLowSurrogate(text.[i + 1]) then
            struct (Char.ConvertToUtf32(c, text.[i + 1]), 2)
        else
            struct (int c, 1)

    /// Returns true if index i is a valid grapheme cluster break point.
    /// Implements a subset of Unicode TR#29 Extended Grapheme Cluster rules.
    let isClusterBoundary (text: ReadOnlySpan<char>) (i: int) : bool =
        if i <= 0 || i >= text.Length then true
        else

        // Decode the codepoint before and at position i
        // We need the codepoint ending just before i
        let mutable prevIdx = i - 1
        if prevIdx > 0 && Char.IsLowSurrogate(text.[prevIdx]) && Char.IsHighSurrogate(text.[prevIdx - 1]) then
            prevIdx <- prevIdx - 1
        let struct (prevCp, _) = decodeCodepoint text prevIdx
        let struct (currCp, _) = decodeCodepoint text i

        // Rule GB9: × (Extend | ZWJ)
        // Do not break before Extend characters or ZWJ
        if isCombiningMark currCp || isZWJ currCp || isVariationSelector currCp then false

        // Rule GB9a: × SpacingMark (Mc category)
        elif isEmojiModifier currCp then false

        // Rule GB11: ExtPict × ZWJ (don't break between ExtPict and ZWJ)
        // and ZWJ × ExtPict (handled above by GB9)
        elif isZWJ prevCp && isExtendedPictographic currCp then false

        // Rule GB12/GB13: Regional Indicator pairs
        elif isRegionalIndicator prevCp && isRegionalIndicator currCp then
            // Count preceding RI characters to determine if this is an odd position
            let mutable riCount = 0
            let mutable j = prevIdx
            while j >= 0 do
                let mutable decIdx = j
                if decIdx > 0 && Char.IsLowSurrogate(text.[decIdx]) && Char.IsHighSurrogate(text.[decIdx - 1]) then
                    decIdx <- decIdx - 1
                let struct (cp, len) = decodeCodepoint text decIdx
                if isRegionalIndicator cp then
                    riCount <- riCount + 1
                    j <- decIdx - 1
                else
                    j <- -1  // exit
            riCount % 2 = 1  // Break at even positions only (pair boundaries)

        // Hangul jamo rules (GB6-GB8)
        elif isHangulL prevCp && (isHangulL currCp || isHangulV currCp || isHangulLV currCp || isHangulLVT currCp) then false
        elif (isHangulLV prevCp || isHangulV prevCp) && (isHangulV currCp || isHangulT currCp) then false
        elif (isHangulLVT prevCp || isHangulT prevCp) && isHangulT currCp then false

        // Default: break
        else true

    /// Analyze text into directional runs.
    /// Bidi stub: identifies contiguous LTR and RTL runs based on strong character direction.
    let analyze (text: ReadOnlySpan<char>) (buffer: TextRun array) : int =
        if text.Length = 0 then 0
        else

        let mutable runCount = 0
        let mutable runStart = 0
        let mutable i = 0

        // Determine initial direction from first strong character
        let struct (firstCp, firstLen) = decodeCodepoint text 0
        let mutable currentIsRTL = isRTL firstCp
        i <- firstLen

        while i < text.Length do
            let struct (cp, len) = decodeCodepoint text i

            // Skip weak/neutral characters (marks, ZWJ, etc.)
            let charIsRTL = isRTL cp
            let isStrong = charIsRTL || (cp > 0x40 && not (isCombiningMark cp) && not (isZWJ cp) && not (isVariationSelector cp))

            if isStrong && charIsRTL <> currentIsRTL then
                // Direction change — emit current run
                if runCount < buffer.Length then
                    buffer.[runCount] <- { StartIndex = runStart; Length = i - runStart; IsRTL = currentIsRTL }
                    runCount <- runCount + 1
                runStart <- i
                currentIsRTL <- charIsRTL

            i <- i + len

        // Emit final run
        if runStart < text.Length && runCount < buffer.Length then
            buffer.[runCount] <- { StartIndex = runStart; Length = text.Length - runStart; IsRTL = currentIsRTL }
            runCount <- runCount + 1

        runCount
