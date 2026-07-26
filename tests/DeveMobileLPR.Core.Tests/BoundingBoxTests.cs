using DeveMobileLPR.Geometry;

namespace DeveMobileLPR.Tests;

public sealed class BoundingBoxTests
{
    [Fact]
    public void IntersectionOverUnion_ComputesExpectedOverlap()
    {
        var left = new BoundingBox(0, 0, 10, 10);
        var right = new BoundingBox(5, 0, 15, 10);
        Assert.Equal(1f / 3f, left.IntersectionOverUnion(right), 5);
    }

    [Fact]
    public void Expand_ClampsToFrame()
    {
        var result = new BoundingBox(0, 0, 10, 10).Expand(0.5f, 0.5f, 12, 12);
        Assert.Equal(new BoundingBox(0, 0, 12, 12), result);
    }
}
