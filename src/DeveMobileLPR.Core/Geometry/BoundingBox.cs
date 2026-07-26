namespace DeveMobileLPR.Geometry;

public readonly record struct BoundingBox(float Left, float Top, float Right, float Bottom)
{
    public float Width => Math.Max(0, Right - Left);
    public float Height => Math.Max(0, Bottom - Top);
    public float Area => Width * Height;
    public float CenterX => (Left + Right) / 2;
    public float CenterY => (Top + Bottom) / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public BoundingBox Clamp(int width, int height) => new(
        Math.Clamp(Left, 0, width),
        Math.Clamp(Top, 0, height),
        Math.Clamp(Right, 0, width),
        Math.Clamp(Bottom, 0, height));

    public BoundingBox Expand(float horizontalFraction, float verticalFraction, int width, int height)
    {
        var dx = Width * horizontalFraction;
        var dy = Height * verticalFraction;
        return new BoundingBox(Left - dx, Top - dy, Right + dx, Bottom + dy).Clamp(width, height);
    }

    public float IntersectionOverUnion(BoundingBox other)
    {
        var intersectionWidth = Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left));
        var intersectionHeight = Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
        var intersection = intersectionWidth * intersectionHeight;
        var union = Area + other.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

public readonly record struct NormalizedRegion(float Left, float Top, float Right, float Bottom)
{
    public static NormalizedRegion RoadDefault { get; } = new(0.03f, 0.18f, 0.97f, 0.94f);

    public BoundingBox ToPixels(int width, int height) => new BoundingBox(
        Math.Clamp(Left, 0, 1) * width,
        Math.Clamp(Top, 0, 1) * height,
        Math.Clamp(Right, 0, 1) * width,
        Math.Clamp(Bottom, 0, 1) * height).Clamp(width, height);
}
