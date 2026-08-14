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
        AspectScaleMode mode,
        bool mirrorHorizontally = false)
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
        var correction = new AspectRatioCorrection(
            scaleX,
            skewX,
            centerX - scaleX * centerX - skewX * centerY,
            skewY,
            scaleY,
            centerY - skewY * centerX - scaleY * centerY);
        return mirrorHorizontally
            ? correction.MirrorHorizontally(viewportWidth)
            : correction;
    }

    public (float X, float Y) Project(float x, float y) => (
        ScaleX * x + SkewX * y + TranslateX,
        SkewY * x + ScaleY * y + TranslateY);

    public BoundingBox ProjectBounds(float width, float height)
    {
        var points = new[]
        {
            Project(0, 0),
            Project(width, 0),
            Project(0, height),
            Project(width, height)
        };
        return new BoundingBox(
            points.Min(static point => point.X),
            points.Min(static point => point.Y),
            points.Max(static point => point.X),
            points.Max(static point => point.Y));
    }

    private AspectRatioCorrection MirrorHorizontally(float viewportWidth) => new(
        -ScaleX,
        -SkewX,
        viewportWidth - TranslateX,
        SkewY,
        ScaleY,
        TranslateY);
}

/// <summary>
/// Keeps preview-surface orientation separate from the rotation required to read a raw AI frame.
/// Android's preview producer and ImageReader do not promise the same transform.
/// </summary>
public readonly record struct CameraOrientationContract(
    int SensorOrientationDegrees,
    int DisplayRotationDegrees,
    int PreviewRotationDegrees,
    int AiRotationDegrees,
    bool PreviewMirrored)
{
    public static CameraOrientationContract Create(
        int sensorOrientationDegrees,
        int displayRotationDegrees,
        bool isFrontFacing)
    {
        var sensor = NormalizeRightAngle(sensorOrientationDegrees, nameof(sensorOrientationDegrees));
        var display = NormalizeRightAngle(displayRotationDegrees, nameof(displayRotationDegrees));

        // Camera2 preview SurfaceTexture has its own producer/display transform. The last
        // device-proven upright path corrects that surface by the inverse display rotation.
        // ImageReader receives raw sensor-oriented YUV and needs the standard relative rotation.
        var preview = NormalizeDegrees(-display);
        var ai = isFrontFacing
            ? NormalizeDegrees(sensor + display)
            : NormalizeDegrees(sensor - display);
        return new CameraOrientationContract(sensor, display, preview, ai, isFrontFacing);
    }

    private static int NormalizeRightAngle(int value, string parameterName)
    {
        var normalized = NormalizeDegrees(value);
        return normalized % 90 == 0
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static int NormalizeDegrees(int value) => ((value % 360) + 360) % 360;
}

/// <summary>Actual native panel occupied by one source, expressed relative to the preview host.</summary>
public sealed record PreviewSourceViewport(
    string SourceId,
    BoundingBox NormalizedBounds,
    AspectScaleMode ScaleMode,
    bool MirrorHorizontally)
{
    public BoundingBox Resolve(float viewportWidth, float viewportHeight) => new(
        NormalizedBounds.Left * viewportWidth,
        NormalizedBounds.Top * viewportHeight,
        NormalizedBounds.Right * viewportWidth,
        NormalizedBounds.Bottom * viewportHeight);
}
