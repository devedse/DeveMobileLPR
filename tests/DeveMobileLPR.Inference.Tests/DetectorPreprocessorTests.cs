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

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Fill_VectorizedPathMatchesScalarPathExactly(int rotationDegrees)
    {
        var originalSetting = DetectorPreprocessor.UseVectorizedResampler;
        try
        {
            foreach (var paddedPlanes in new[] { false, true })
            {
                using var frame = CreateDetailedFrame(rotationDegrees, paddedPlanes);
                var source = new BoundingBox(0, 0, frame.OrientedWidth, frame.OrientedHeight);
                foreach (var layout in new[] { YoloV9InputLayout.ChannelsFirst, YoloV9InputLayout.ChannelsLast })
                {
                    var scalarTensor = new float[YoloV9RawPlateDetector.InputValueCount];
                    var vectorTensor = new float[YoloV9RawPlateDetector.InputValueCount];

                    DetectorPreprocessor.UseVectorizedResampler = false;
                    DetectorPreprocessor.Fill(frame, source, scalarTensor, layout);
                    DetectorPreprocessor.UseVectorizedResampler = true;
                    DetectorPreprocessor.Fill(frame, source, vectorTensor, layout);

                    Assert.Equal(scalarTensor, vectorTensor);
                }
            }
        }
        finally
        {
            DetectorPreprocessor.UseVectorizedResampler = originalSetting;
        }
    }

    [Fact]
    public void FillMeasured_ReportsPreparationStages()
    {
        using var frame = CreateFrame();
        var tensor = new float[YoloV9RawPlateDetector.InputValueCount];
        var source = new BoundingBox(0, 0, frame.Width, frame.Height);

        var result = DetectorPreprocessor.FillMeasured(frame, source, tensor);

        Assert.True(result.Timing.SetupMilliseconds >= 0);
        Assert.True(result.Timing.TensorFillMilliseconds >= 0);
        Assert.True(result.Timing.ResampleMilliseconds > 0);
        Assert.True(result.Timing.TotalMilliseconds > 0);
    }

    private static Yuv420Frame CreateDetailedFrame(int rotationDegrees, bool paddedPlanes)
    {
        const int width = 96;
        const int height = 64;
        var yRowStride = paddedPlanes ? width + 32 : width;
        const int yPixelStride = 1;
        var chromaRowStride = paddedPlanes ? width + 16 : width / 2;
        var chromaPixelStride = paddedPlanes ? 2 : 1;
        var yLength = yRowStride * height;
        var chromaLength = chromaRowStride * (height / 2);
        var y = MemoryPool<byte>.Shared.Rent(yLength);
        var u = MemoryPool<byte>.Shared.Rent(chromaLength);
        var v = MemoryPool<byte>.Shared.Rent(chromaLength);
        y.Memory.Span[..yLength].Clear();
        u.Memory.Span[..chromaLength].Clear();
        v.Memory.Span[..chromaLength].Clear();
        for (var rawY = 0; rawY < height; rawY++)
        {
            for (var rawX = 0; rawX < width; rawX++)
            {
                y.Memory.Span[rawY * yRowStride + rawX * yPixelStride] = (byte)((rawX * 31 + rawY * 17) % 256);
            }
        }

        for (var chromaY = 0; chromaY < height / 2; chromaY++)
        {
            for (var chromaX = 0; chromaX < width / 2; chromaX++)
            {
                var offset = chromaY * chromaRowStride + chromaX * chromaPixelStride;
                u.Memory.Span[offset] = (byte)((chromaX * 13 + chromaY * 29) % 256);
                v.Memory.Span[offset] = (byte)((chromaX * 41 + chromaY * 7) % 256);
            }
        }

        return new Yuv420Frame(
            1,
            DateTimeOffset.UnixEpoch,
            width,
            height,
            rotationDegrees,
            y,
            yLength,
            yRowStride,
            yPixelStride,
            u,
            chromaLength,
            chromaRowStride,
            chromaPixelStride,
            v,
            chromaLength,
            chromaRowStride,
            chromaPixelStride);
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
