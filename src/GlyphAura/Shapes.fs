namespace GlyphAura

open System

/// SDF Geometry Engine — Signed Distance Field boundary shapes for text layout.
/// Each shape provides GetDistance (point query) and GetInterval (scanline query).
module Shapes =

    /// A 2D point.
    [<Struct>]
    type Point = { X: float32; Y: float32 }

    /// An axis-aligned rectangle.
    [<Struct>]
    type Rectangle = {
        Left: float32
        Top: float32
        Right: float32
        Bottom: float32
    }
    with
        member r.Width = r.Right - r.Left
        member r.Height = r.Bottom - r.Top

    /// Interface for SDF shapes. Drives the layout loop.
    type ISdfShape =
        /// Signed distance from point to shape boundary.
        /// Negative = inside, 0 = boundary, positive = outside.
        abstract GetDistance : Point -> float32
        /// Returns the (xStart, xEnd) interval for a horizontal scanline at y.
        /// ValueNone if the scanline misses the shape entirely.
        abstract GetInterval : y: float32 -> struct (float32 * float32) voption
        /// Axis-aligned bounding box of the shape.
        abstract BoundingBox : Rectangle

    // ---- SDF primitives (inline, no virtual dispatch) ----

    /// Circle SDF: distance from point to circle boundary.
    let inline circleSdf (radius: float32) (p: Point) : float32 =
        sqrt (p.X * p.X + p.Y * p.Y) - radius

    /// Diamond (Raute) SDF: distance from point to diamond boundary.
    let inline diamondSdf (hw: float32) (hh: float32) (p: Point) : float32 =
        (abs p.X) / hw + (abs p.Y) / hh - 1.0f

    /// Triangle SDF: isoceles triangle centered at origin, base along bottom.
    let inline triangleSdf (halfBase: float32) (halfHeight: float32) (p: Point) : float32 =
        let py = p.Y + halfHeight  // shift so bottom is at y=0
        if py < 0.0f then
            // Below the triangle
            abs p.Y - halfHeight
        else
            let widthAtY = halfBase * (1.0f - py / (2.0f * halfHeight))
            if widthAtY <= 0.0f then
                // Above the apex
                abs p.Y - halfHeight
            else
                let dx = (abs p.X) - widthAtY
                let dy = py - 2.0f * halfHeight
                if dx <= 0.0f && dy <= 0.0f then
                    max dx dy
                elif dx > 0.0f && dy > 0.0f then
                    sqrt (dx * dx + dy * dy)
                else
                    max dx dy

    /// Parallelogram SDF: centered at origin with shear.
    let inline parallelogramSdf (hw: float32) (hh: float32) (shear: float32) (p: Point) : float32 =
        let effectiveX = p.X - shear * p.Y
        let dx = (abs effectiveX) - hw
        let dy = (abs p.Y) - hh
        if dx <= 0.0f && dy <= 0.0f then
            max dx dy
        elif dx > 0.0f && dy > 0.0f then
            sqrt (dx * dx + dy * dy)
        else
            max dx dy

    // ---- Binary search edge finder ----

    /// Find the edge crossing using binary search. 16 iterations for sub-pixel precision.
    let private findEdge (sdf: float32 -> float32) (lo: float32) (hi: float32) : float32 =
        let mutable lo = lo
        let mutable hi = hi
        for _ in 1 .. 16 do
            let mid = (lo + hi) * 0.5f
            if sdf mid <= 0.0f then hi <- mid else lo <- mid
        (lo + hi) * 0.5f

    // ---- Concrete shape implementations ----

    /// Create a circle SDF shape centered at the given point.
    let circle (center: Point) (radius: float32) : ISdfShape =
        { new ISdfShape with
            member _.GetDistance(p) =
                circleSdf radius { X = p.X - center.X; Y = p.Y - center.Y }

            member _.GetInterval(y) =
                let dy = y - center.Y
                if abs dy >= radius then ValueNone
                else
                // Analytical: x = ±sqrt(r² - dy²)
                let dx = sqrt (radius * radius - dy * dy)
                ValueSome(struct (center.X - dx, center.X + dx))

            member _.BoundingBox =
                { Left = center.X - radius; Top = center.Y - radius
                  Right = center.X + radius; Bottom = center.Y + radius }
        }

    /// Create a diamond (Raute) SDF shape.
    /// hw = half-width, hh = half-height.
    let diamond (center: Point) (hw: float32) (hh: float32) : ISdfShape =
        { new ISdfShape with
            member _.GetDistance(p) =
                diamondSdf hw hh { X = p.X - center.X; Y = p.Y - center.Y }

            member _.GetInterval(y) =
                let dy = y - center.Y
                if abs dy >= hh then ValueNone
                else
                // Analytical: |x|/hw + |dy|/hh = 1 => |x| = hw * (1 - |dy|/hh)
                let halfWidth = hw * (1.0f - abs dy / hh)
                ValueSome(struct (center.X - halfWidth, center.X + halfWidth))

            member _.BoundingBox =
                { Left = center.X - hw; Top = center.Y - hh
                  Right = center.X + hw; Bottom = center.Y + hh }
        }

    /// Create a triangle SDF shape (isosceles, apex at top, base at bottom).
    /// base' = full base width, height = full height.
    let triangle (center: Point) (base': float32) (height: float32) : ISdfShape =
        let halfBase = base' * 0.5f
        let halfHeight = height * 0.5f
        { new ISdfShape with
            member _.GetDistance(p) =
                let lp = { X = p.X - center.X; Y = p.Y - center.Y }
                // Triangle with apex at top (-halfHeight) and base at bottom (+halfHeight)
                let normY = (lp.Y + halfHeight) / height  // 0 at top, 1 at bottom
                if normY < 0.0f || normY > 1.0f then
                    // Outside vertical range
                    if normY < 0.0f then abs lp.Y - halfHeight
                    else lp.Y - halfHeight
                else
                    let widthAtY = halfBase * normY
                    let dx = abs lp.X - widthAtY
                    if dx <= 0.0f then
                        // Inside horizontally, compute approx distance
                        let distBottom = halfHeight - lp.Y
                        let distSide = -dx  // negative = inside
                        -(min distBottom distSide)
                    else dx

            member _.GetInterval(y) =
                let dy = y - center.Y
                // Apex at top: y = center.Y - halfHeight
                // Base at bottom: y = center.Y + halfHeight
                if dy < -halfHeight || dy > halfHeight then ValueNone
                else
                let normY = (dy + halfHeight) / height  // 0 at apex, 1 at base
                let halfWidth = halfBase * normY
                if halfWidth <= 0.0f then ValueNone
                else
                ValueSome(struct (center.X - halfWidth, center.X + halfWidth))

            member _.BoundingBox =
                { Left = center.X - halfBase; Top = center.Y - halfHeight
                  Right = center.X + halfBase; Bottom = center.Y + halfHeight }
        }

    /// Create a parallelogram SDF shape.
    /// w = full width, h = full height, shear = horizontal shift per unit y.
    let parallelogram (center: Point) (w: float32) (h: float32) (shear: float32) : ISdfShape =
        let hw = w * 0.5f
        let hh = h * 0.5f
        { new ISdfShape with
            member _.GetDistance(p) =
                parallelogramSdf hw hh shear { X = p.X - center.X; Y = p.Y - center.Y }

            member _.GetInterval(y) =
                let dy = y - center.Y
                if abs dy > hh then ValueNone
                else
                let shearOffset = shear * (y - center.Y)
                ValueSome(struct (center.X - hw + shearOffset, center.X + hw + shearOffset))

            member _.BoundingBox =
                let shearExtent = abs shear * hh
                { Left = center.X - hw - shearExtent; Top = center.Y - hh
                  Right = center.X + hw + shearExtent; Bottom = center.Y + hh }
        }
