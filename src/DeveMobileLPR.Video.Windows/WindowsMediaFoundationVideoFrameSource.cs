using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace DeveMobileLPR.Video.Windows;

/// <summary>
/// Sequential Windows video decoder shared by the app and real-video replay tests.
/// Frames are decoded at source resolution so OCR can crop from the original pixels.
/// </summary>
public sealed class WindowsMediaFoundationVideoFrameSource : IVideoFrameSource
{
    private readonly IMFSourceReader _reader;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _rotationDegrees;
    private long _nextSourceFrameIndex;
    private bool _disposed;

    private WindowsMediaFoundationVideoFrameSource(string sourcePath, VideoFrameTimeline timeline)
    {
        MFStartup().CheckError();
        try
        {
            using var attributes = MFCreateAttributes(2);
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            attributes.Set(SourceReaderAttributeKeys.DisableDxva, 0u);
            _reader = MFCreateSourceReaderFromURL(sourcePath, attributes);
            _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using var outputType = MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);

            using var configuredType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            MFGetAttributeSize(configuredType, MediaTypeAttributeKeys.FrameSize, out var width, out var height).CheckError();
            _width = checked((int)width);
            _height = checked((int)height);
            _stride = GetStride(configuredType, _width);
            _rotationDegrees = GetRotation(configuredType);
            Timeline = timeline;
        }
        catch
        {
            MFShutdown();
            throw;
        }
    }

    public VideoFrameTimeline Timeline { get; }

    public static WindowsMediaFoundationVideoFrameSource Create(string sourcePath, VideoFrameTimeline timeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return new WindowsMediaFoundationVideoFrameSource(sourcePath, timeline);
    }

    public ValueTask<Yuv420Frame?> DecodeAsync(
        long sourceFrameIndex,
        TimeSpan position,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceFrameIndex < _nextSourceFrameIndex)
        {
            throw new InvalidOperationException("Sequential video decoding cannot move backwards.");
        }

        while (_nextSourceFrameIndex <= sourceFrameIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sample = ReadNextSample(out var timestamp);
            if (sample is null)
            {
                return ValueTask.FromResult<Yuv420Frame?>(null);
            }

            var decodedFrameIndex = _nextSourceFrameIndex++;
            if (decodedFrameIndex == sourceFrameIndex)
            {
                return ValueTask.FromResult<Yuv420Frame?>(CreateFrame(sample, sourceFrameIndex + 1, timestamp));
            }
        }

        return ValueTask.FromResult<Yuv420Frame?>(null);
    }

    private IMFSample? ReadNextSample(out long timestamp)
    {
        while (true)
        {
            var sample = _reader.ReadSample(
                SourceReaderIndex.FirstVideoStream,
                SourceReaderControlFlag.None,
                out _,
                out var flags,
                out timestamp);
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                sample?.Dispose();
                return null;
            }

            if ((flags & SourceReaderFlag.Error) != 0)
            {
                sample?.Dispose();
                throw new InvalidDataException("Media Foundation failed while decoding the video stream.");
            }

            if (sample is not null)
            {
                return sample;
            }
        }
    }

    private unsafe Yuv420Frame CreateFrame(IMFSample sample, long sequence, long timestamp)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var data, out _, out var currentLength);
        try
        {
            return Nv12FrameFactory.Create(
                new ReadOnlySpan<byte>((void*)data, currentLength),
                _stride,
                _width,
                _height,
                sequence,
                DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(timestamp),
                _rotationDegrees);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static int GetStride(IMFMediaType mediaType, int width)
    {
        var result = mediaType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var stride);
        return result.Success ? checked((int)stride) : width;
    }

    private static int GetRotation(IMFMediaType mediaType)
    {
        var result = mediaType.GetUInt32(MediaTypeAttributeKeys.VideoRotation, out var rotation);
        return result.Success ? checked((int)rotation) : 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader.Dispose();
        MFShutdown();
    }
}
