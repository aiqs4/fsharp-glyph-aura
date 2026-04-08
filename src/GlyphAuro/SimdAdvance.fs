namespace GlyphAuro

open System
open System.Runtime.Intrinsics

/// SIMD-accelerated glyph width summation using Vector128<float32>.
/// Falls back to scalar on non-SIMD environments.
module SimdAdvance =

    /// Sum an array of float32 advance values using SIMD Vector128.
    /// Processes 4 floats per iteration; handles non-multiple-of-4 tails with scalar loop.
    let sumAdvances (advances: ReadOnlySpan<float32>) : float32 =
        let len = advances.Length
        if len = 0 then 0.0f
        elif len < 4 then
            let mutable sum = 0.0f
            for i in 0 .. len - 1 do
                sum <- sum + advances.[i]
            sum
        else
        let mutable acc = Vector128<float32>.Zero
        let mutable i = 0
        let chunks = len / 4

        for _ in 1 .. chunks do
            let v = Vector128.Create(advances.[i], advances.[i + 1], advances.[i + 2], advances.[i + 3])
            acc <- Vector128.Add(acc, v)
            i <- i + 4

        let mutable sum = Vector128.Sum(acc)
        while i < len do
            sum <- sum + advances.[i]
            i <- i + 1
        sum

    /// Scalar reference implementation for validation.
    let sumAdvancesScalar (advances: ReadOnlySpan<float32>) : float32 =
        let mutable sum = 0.0f
        for i in 0 .. advances.Length - 1 do
            sum <- sum + advances.[i]
        sum

    /// Sum advances from a ShapedGlyph buffer (extracting XAdvance fields).
    let sumGlyphAdvances (glyphs: ShapedGlyph array) (count: int) (scale: float32) : float32 =
        if count = 0 then 0.0f
        else
        let mutable sum = 0.0f
        for i in 0 .. count - 1 do
            sum <- sum + glyphs.[i].XAdvance * scale
        sum
