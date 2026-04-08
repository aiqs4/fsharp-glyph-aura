namespace GlyphAura

open System

/// Zero-allocation big-endian binary reading utilities for OpenType font parsing.
/// All reads operate on ReadOnlySpan<byte> slices — no byte[] copies.
module BinaryReader =

    /// Read a big-endian unsigned 16-bit integer.
    let inline readU16 (span: ReadOnlySpan<byte>) (offset: int) : uint16 =
        (uint16 span.[offset] <<< 8) ||| uint16 span.[offset + 1]

    /// Read a big-endian signed 16-bit integer.
    let inline readI16 (span: ReadOnlySpan<byte>) (offset: int) : int16 =
        int16 ((uint16 span.[offset] <<< 8) ||| uint16 span.[offset + 1])

    /// Read a big-endian unsigned 32-bit integer.
    let inline readU32 (span: ReadOnlySpan<byte>) (offset: int) : uint32 =
        (uint32 span.[offset] <<< 24)
        ||| (uint32 span.[offset + 1] <<< 16)
        ||| (uint32 span.[offset + 2] <<< 8)
        ||| uint32 span.[offset + 3]

    /// Read a big-endian signed 32-bit integer.
    let inline readI32 (span: ReadOnlySpan<byte>) (offset: int) : int32 =
        int32 (readU32 span offset)

    /// Compare a 4-byte ASCII tag at a given offset.
    let inline tagEquals (span: ReadOnlySpan<byte>) (offset: int) (a: byte) (b: byte) (c: byte) (d: byte) : bool =
        span.[offset] = a && span.[offset + 1] = b && span.[offset + 2] = c && span.[offset + 3] = d

    /// Convert a 4-char string tag to its 4-byte representation for comparison.
    let inline tagToBytes (tag: string) : struct (byte * byte * byte * byte) =
        struct (byte tag.[0], byte tag.[1], byte tag.[2], byte tag.[3])
