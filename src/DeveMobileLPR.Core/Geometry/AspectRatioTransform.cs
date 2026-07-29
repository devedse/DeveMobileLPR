namespace DeveMobileLPR.Geometry;

public enum AspectScaleMode
{
    Fit,
    Fill
}

public readonly record struct AspectRatioTransform(float Scale, float OffsetX, float OffsetY)
{
    public static AspectRatioTransform Create(
        int sourceWidth,
        int sourceHeight,
        float viewportWidth,
        float viewportHeight,
        AspectScaleMode mode,
        float viewportLeft = 0,
        float viewportTop = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        if (!float.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        }
        if (!float.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        }
        if (!float.IsFinite(viewportLeft))
        {
            throw new ArgumentOutOfRangeException(nameof(viewportLeft));
        }
        if (!float.IsFinite(viewportTop))
        {
            throw new ArgumentOutOfRangeException(nameof(viewportTop));
        }

        var horizontalScale = viewportWidth / sourceWidth;
        var verticalScale = viewportHeight / sourceHeight;
        var scale = mode switch
        {
            AspectScaleMode.Fit => Math.Min(horizontalScale, verticalScale),
            AspectScaleMode.Fill => Math.Max(horizontalScale, verticalScale),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return new AspectRatioTransform(
            scale,
            viewportLeft + (viewportWidth - sourceWidth * scale) / 2,
            viewportTop + (viewportHeight - sourceHeight * scale) / 2);
    }

    public BoundingBox Project(BoundingBox source) => new(
        source.Left * Scale + OffsetX,
        source.Top * Scale + OffsetY,
        source.Right * Scale + OffsetX,
        source.Bottom * Scale + OffsetY);
}
