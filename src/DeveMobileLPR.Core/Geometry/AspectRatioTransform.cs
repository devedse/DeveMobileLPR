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

/// <summary>
/// Corrects the non-uniform stretch that a texture surface applies when its buffer and viewport
/// have different aspect ratios. Coordinates passed to <see cref="Project"/> are coordinates in
/// that already-stretched viewport.
/// </summary>
public readonly record struct AspectRatioCorrection(
    float ScaleX,
    float SkewX,
    float TranslateX,
    float SkewY,
    float ScaleY,
    float TranslateY)
{
    public static AspectRatioCorrection Create(
        int bufferWidth,
        int bufferHeight,
        float viewportWidth,
        float viewportHeight,
        int clockwiseRotationDegrees,
        AspectScaleMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferHeight);
        if (!float.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        }
        if (!float.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        }

        var rotation = ((clockwiseRotationDegrees % 360) + 360) % 360;
        if (rotation % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clockwiseRotationDegrees));
        }

        var swapsAxes = rotation is 90 or 270;
        var orientedWidth = swapsAxes ? bufferHeight : bufferWidth;
        var orientedHeight = swapsAxes ? bufferWidth : bufferHeight;
        var uniformScale = AspectRatioTransform.Create(
            orientedWidth,
            orientedHeight,
            viewportWidth,
            viewportHeight,
            mode).Scale;
        var radians = rotation * MathF.PI / 180f;
        var cosine = MathF.Round(MathF.Cos(radians));
        var sine = MathF.Round(MathF.Sin(radians));

        // TextureView first stretches the raw buffer to the entire viewport. Multiplying by the
        // inverse of that stretch and then by one uniform scale preserves circles and faces.
        var scaleX = uniformScale * cosine * bufferWidth / viewportWidth;
        var skewX = -uniformScale * sine * bufferHeight / viewportHeight;
        var skewY = uniformScale * sine * bufferWidth / viewportWidth;
        var scaleY = uniformScale * cosine * bufferHeight / viewportHeight;
        var centerX = viewportWidth / 2;
        var centerY = viewportHeight / 2;
        return new AspectRatioCorrection(
            scaleX,
            skewX,
            centerX - scaleX * centerX - skewX * centerY,
            skewY,
            scaleY,
            centerY - skewY * centerX - scaleY * centerY);
    }

    public (float X, float Y) Project(float x, float y) => (
        ScaleX * x + SkewX * y + TranslateX,
        SkewY * x + ScaleY * y + TranslateY);
}
