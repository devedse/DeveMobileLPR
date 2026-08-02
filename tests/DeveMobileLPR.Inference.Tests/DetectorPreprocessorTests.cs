using System.Buffers;
using DeveMobileLPR.Geometry;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Inference.Preprocessing;
using DeveMobileLPR.Inference.Yolo;

namespace DeveMobileLPR.Inference.Tests;

public sealed class DetectorPreprocessorTests
{
    [Fact]
    public void Fill_ProducesEquivalentChannelsFirstAndChannelsLastValues()
    {
        using var frame = CreateFrame();
        var channelsFirst = new float[YoloV9RawPlateDetector.InputValueCount];
        var channelsLast = new float[YoloV9RawPlateDetector.InputValueCount];
        var source = new BoundingBox(0, 0, frame.Width, frame.Height);

        DetectorPreprocessor.Fill(frame, source, channelsFirst, YoloV9InputLayout.ChannelsFirst);
        DetectorPreprocessor.Fill(frame, source, channelsLast, YoloV9InputLayout.ChannelsLast);

        var planeSize = DetectorPreprocessor.InputSize * DetectorPreprocessor.InputSize;
        int[] pixels = [0, 151 * DetectorPreprocessor.InputSize + 223, planeSize - 1];
        foreach (var pixel in pixels)
        {
            Assert.Equal(channelsFirst[pixel], channelsLast[pixel * 3]);
            Assert.Equal(channelsFirst[planeSize + pixel], channelsLast[pixel * 3 + 1]);
            Assert.Equal(channelsFirst[2 * planeSize + pixel], channelsLast[pixel * 3 + 2]);
        }
    }

    private static Yuv420Frame CreateFrame()
    {
        const int width = 2;
        const int height = 2;
        var y = MemoryPool<byte>.Shared.Rent(width * height);
        var u = MemoryPool<byte>.Shared.Rent(1);
        var v = MemoryPool<byte>.Shared.Rent(1);
        y.Memory.Span[0] = 32;
        y.Memory.Span[1] = 96;
        y.Memory.Span[2] = 160;
        y.Memory.Span[3] = 224;
        u.Memory.Span[0] = 100;
        v.Memory.Span[0] = 180;
        return new Yuv420Frame(
            1,
            DateTimeOffset.UnixEpoch,
            width,
            height,
            0,
            y,
            width * height,
            width,
            1,
            u,
            1,
            1,
            1,
            v,
            1,
            1,
            1);
    }
}
