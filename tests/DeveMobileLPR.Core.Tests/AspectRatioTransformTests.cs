using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.Tests;

public sealed class AspectRatioTransformTests
{
    [Fact]
    public void CameraOrientation_SeparatesPixelPreviewRegressionFromAiRotation()
    {
        var orientation = CameraOrientationContract.Create(
            sensorOrientationDegrees: 90,
            displayRotationDegrees: 0,
            isFrontFacing: false);

        Assert.Equal(0, orientation.PreviewRotationDegrees);
        Assert.Equal(90, orientation.AiRotationDegrees);
        Assert.False(orientation.PreviewMirrored);
    }

    [Fact]
    public void CameraOrientation_LandscapeDisplayKeepsSeparateTransforms()
    {
        var orientation = CameraOrientationContract.Create(90, 90, false);

        Assert.Equal(270, orientation.PreviewRotationDegrees);
        Assert.Equal(0, orientation.AiRotationDegrees);
    }

    [Fact]
    public void Correction_MirrorFlipsHorizontalCoordinatesOnly()
    {
        var correction = AspectRatioCorrection.Create(
            100, 100, 200, 100, 0, AspectScaleMode.Fit, mirrorHorizontally: true);

        Assert.Equal((150f, 0f), correction.Project(0, 0));
        Assert.Equal((50f, 100f), correction.Project(200, 100));
    }

    [Fact]
    public void PreviewViewport_ResolvesActualNativePanelOffset()
    {
        var viewport = new PreviewSourceViewport(
            "tele",
            new BoundingBox(0.5f, 0, 1, 1),
            AspectScaleMode.Fit,
            false);

        Assert.Equal(new BoundingBox(600, 0, 1200, 600), viewport.Resolve(1200, 600));
    }

    [Fact]
    public void Correction_Fit_UndoesTextureStretchWithoutChangingProportions()
    {
        var correction = AspectRatioCorrection.Create(
            1280, 720, 562, 440, 0, AspectScaleMode.Fit);

        var topLeft = correction.Project(0, 0);
        var bottomRight = correction.Project(562, 440);

        Assert.Equal(0, topLeft.X, 3);
        Assert.Equal(61.9375f, topLeft.Y, 3);
        Assert.Equal(562, bottomRight.X, 3);
        Assert.Equal(378.0625f, bottomRight.Y, 3);
        Assert.Equal(
            16f / 9f,
            (bottomRight.X - topLeft.X) / (bottomRight.Y - topLeft.Y),
            3);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void Correction_QuarterTurn_CentresTheRotatedAspect(int rotation)
    {
        var correction = AspectRatioCorrection.Create(
            1280, 720, 440, 562, rotation, AspectScaleMode.Fit);

        var corners = new[]
        {
            correction.Project(0, 0),
            correction.Project(440, 0),
            correction.Project(0, 562),
            correction.Project(440, 562)
        };

        Assert.Equal(61.9375f, corners.Min(static point => point.X), 3);
        Assert.Equal(378.0625f, corners.Max(static point => point.X), 3);
        Assert.Equal(0, corners.Min(static point => point.Y), 3);
        Assert.Equal(562, corners.Max(static point => point.Y), 3);
    }

    [Fact]
    public void Create_Fit_LetterboxesAndProjectsSourceBounds()
    {
        var transform = AspectRatioTransform.Create(1920, 1080, 1000, 1000, AspectScaleMode.Fit);

        var projected = transform.Project(new BoundingBox(0, 0, 1920, 1080));

        Assert.Equal(0, projected.Left, 3);
        Assert.Equal(218.75f, projected.Top, 3);
        Assert.Equal(1000, projected.Right, 3);
        Assert.Equal(781.25f, projected.Bottom, 3);
    }

    [Fact]
    public void Create_Fill_CropsAndProjectsSourceBounds()
    {
        var transform = AspectRatioTransform.Create(1920, 1080, 1000, 1000, AspectScaleMode.Fill);

        var projected = transform.Project(new BoundingBox(0, 0, 1920, 1080));

        Assert.Equal(-388.889f, projected.Left, 3);
        Assert.Equal(0, projected.Top, 3);
        Assert.Equal(1388.889f, projected.Right, 3);
        Assert.Equal(1000, projected.Bottom, 3);
    }

    [Fact]
    public void Create_PreservesViewportOrigin()
    {
        var transform = AspectRatioTransform.Create(100, 100, 200, 100, AspectScaleMode.Fit, 10, 20);

        var projected = transform.Project(new BoundingBox(0, 0, 100, 100));

        Assert.Equal(new BoundingBox(60, 20, 160, 120), projected);
    }

    [Theory]
    [InlineData(0, 1080, 1000, 1000)]
    [InlineData(1920, 0, 1000, 1000)]
    [InlineData(1920, 1080, 0, 1000)]
    [InlineData(1920, 1080, 1000, 0)]
    public void Create_RejectsEmptyDimensions(int sourceWidth, int sourceHeight, float viewportWidth, float viewportHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AspectRatioTransform.Create(sourceWidth, sourceHeight, viewportWidth, viewportHeight, AspectScaleMode.Fit));
    }
}
