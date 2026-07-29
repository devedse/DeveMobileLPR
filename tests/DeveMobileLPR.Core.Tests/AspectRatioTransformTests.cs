using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.Tests;

public sealed class AspectRatioTransformTests
{
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
