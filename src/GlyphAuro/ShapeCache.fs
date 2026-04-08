namespace GlyphAuro

open System

/// LRU Shape Cache + Dirty Flag System.
/// Prevents re-layout when nothing has changed. Primary mechanism for 60 FPS.
module ShapeCache =

    /// FNV-1a hash over UTF-16 chars — fast, no allocation, good distribution.
    let fnv1aHash (text: string) : uint64 =
        let mutable hash = 14695981039346656037UL  // FNV offset basis
        for i in 0 .. text.Length - 1 do
            let c = uint64 text.[i]
            hash <- hash ^^^ (c &&& 0xFFUL)
            hash <- hash * 1099511628211UL  // FNV prime
            hash <- hash ^^^ ((c >>> 8) &&& 0xFFUL)
            hash <- hash * 1099511628211UL
        hash

    /// Hash a shape by its type tag and parameters.
    let shapeHash (shapeType: int) (param1: float32) (param2: float32) (param3: float32) (param4: float32) : uint64 =
        let mutable hash = 14695981039346656037UL
        hash <- hash ^^^ (uint64 shapeType)
        hash <- hash * 1099511628211UL
        hash <- hash ^^^ (uint64 (BitConverter.SingleToInt32Bits param1))
        hash <- hash * 1099511628211UL
        hash <- hash ^^^ (uint64 (BitConverter.SingleToInt32Bits param2))
        hash <- hash * 1099511628211UL
        hash <- hash ^^^ (uint64 (BitConverter.SingleToInt32Bits param3))
        hash <- hash * 1099511628211UL
        hash <- hash ^^^ (uint64 (BitConverter.SingleToInt32Bits param4))
        hash <- hash * 1099511628211UL
        hash

    /// Cache key: all value-typed, no string allocation.
    [<Struct>]
    type CacheKey = {
        FontId: uint64
        TextHash: uint64
        ShapeId: uint64
        FontSizePx: float32
    }

    /// A cache entry with usage tracking.
    [<Struct>]
    type private CacheEntry = {
        Key: CacheKey
        Value: DrawList.DrawList
        mutable LastUsed: int64  // tick count
    }

    /// LRU cache with fixed capacity. Evicts oldest entry when full.
    type ShapeCache(capacity: int) =
        let entries = Array.zeroCreate<CacheEntry voption> capacity
        let mutable count = 0

        /// Try to get a cached DrawList.
        member _.TryGet(key: CacheKey) : DrawList.DrawList voption =
            let mutable result = ValueNone
            for i in 0 .. count - 1 do
                match entries.[i] with
                | ValueSome entry when entry.Key = key ->
                    entries.[i] <- ValueSome { entry with LastUsed = DateTime.UtcNow.Ticks }
                    result <- ValueSome entry.Value
                | _ -> ()
            result

        /// Insert a DrawList into the cache.
        member _.Put(key: CacheKey, value: DrawList.DrawList) : unit =
            // Check if key already exists
            let mutable existingIdx = -1
            for i in 0 .. count - 1 do
                match entries.[i] with
                | ValueSome entry when entry.Key = key -> existingIdx <- i
                | _ -> ()

            let now = DateTime.UtcNow.Ticks
            if existingIdx >= 0 then
                entries.[existingIdx] <- ValueSome { Key = key; Value = value; LastUsed = now }
            elif count < capacity then
                entries.[count] <- ValueSome { Key = key; Value = value; LastUsed = now }
                count <- count + 1
            else
                // Find LRU entry (oldest LastUsed)
                let mutable oldestIdx = 0
                let mutable oldestTick = Int64.MaxValue
                for i in 0 .. count - 1 do
                    match entries.[i] with
                    | ValueSome entry when entry.LastUsed < oldestTick ->
                        oldestIdx <- i
                        oldestTick <- entry.LastUsed
                    | _ -> ()
                entries.[oldestIdx] <- ValueSome { Key = key; Value = value; LastUsed = now }

        /// Manually evict the oldest entry.
        member _.Evict() : unit =
            if count > 0 then
                let mutable oldestIdx = 0
                let mutable oldestTick = Int64.MaxValue
                for i in 0 .. count - 1 do
                    match entries.[i] with
                    | ValueSome entry when entry.LastUsed < oldestTick ->
                        oldestIdx <- i
                        oldestTick <- entry.LastUsed
                    | _ -> ()
                // Shift entries to fill the gap
                for i in oldestIdx .. count - 2 do
                    entries.[i] <- entries.[i + 1]
                entries.[count - 1] <- ValueNone
                count <- count - 1

        /// Current number of entries.
        member _.Count = count

        /// Clear the entire cache.
        member _.Clear() =
            for i in 0 .. capacity - 1 do
                entries.[i] <- ValueNone
            count <- 0

    /// Default cache with 32 entry capacity.
    let defaultCache () = ShapeCache(32)
