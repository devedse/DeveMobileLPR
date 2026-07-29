using System.Runtime.InteropServices.WindowsRuntime;
using DeveMobileLPR.Imaging;
using Vortice.MediaFoundation;
using Windows.Graphics.Imaging;

namespace DeveMobileLPR.App.Platforms.Windows;

/// <summary>
/// Reads timestamped NV12 frames from a completed fragmented MP4 segment. The decoder output matches
/// the video-analysis pipeline so recognition does not make an RGB round trip.
/// </summary>
internal sealed class WindowsMediaFoundationLiveFrameReader : IDisposable
{
    private static readonly HttpClient Client = new();
    private readonly IMFSourceReader _reader;
    private readonly string? _temporaryPath;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _rotationDegrees;
    private readonly byte[] _packedNv12;
    private int _disposed;

    private WindowsMediaFoundationLiveFrameReader(Uri mediaUri, string? temporaryPath)
    {
        _temporaryPath = temporaryPath;
        MediaFactory.MFStartup().CheckError();
        IMFSourceReader? reader = null;
        try
        {
            // Keep this in sync with WindowsMediaFoundationVideoFrameSource. NV12 is the decoder's
            // natural output and avoids Media Foundation's software YUV-to-RGB video processor.
            using var attributes = MediaFactory.MFCreateAttributes(2);
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            attributes.Set(SourceReaderAttributeKeys.DisableDxva, 0u);

            try
            {
                reader = MediaFactory.MFCreateSourceReaderFromURL(mediaUri.AbsoluteUri, attributes);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"Opening the completed MP4 fragment failed: {exception.Message}", exception);
            }
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using var outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            try
            {
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"Selecting NV12 decoder output failed: {exception.Message}", exception);
            }

            using var actualType = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            var packedSize = actualType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            _width = checked((int)(packedSize >> 32));
            _height = checked((int)(packedSize & uint.MaxValue));
            var strideResult = actualType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var rawStride);
            _stride = strideResult.Success ? checked((int)rawStride) : _width;
            var rotationResult = actualType.GetUInt32(MediaTypeAttributeKeys.VideoRotation, out var rawRotation);
            _rotationDegrees = rotationResult.Success ? checked((int)rawRotation) : 0;
            if (_width <= 0 || _height <= 0 || _stride < _width || (_width & 1) != 0 || (_height & 1) != 0)
            {
                throw new InvalidDataException("Media Foundation returned invalid NV12 frame geometry.");
            }

            _packedNv12 = GC.AllocateUninitializedArray<byte>(checked(_width * (_height + _height / 2)));
            _reader = reader;
            reader = null;
        }
        catch
        {
            reader?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    public static async Task<WindowsMediaFoundationLiveFrameReader> CreateForSegmentAsync(
        Uri initializationSegment,
        Uri mediaSegment,
        CancellationToken cancellationToken)
    {
        var initBytesTask = Client.GetByteArrayAsync(initializationSegment, cancellationToken);
        var mediaBytesTask = Client.GetByteArrayAsync(mediaSegment, cancellationToken);
        await Task.WhenAll(initBytesTask, mediaBytesTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.Combine(Path.GetTempPath(), "DeveMobileLPR", "native-hls");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.mp4");
        try
        {
            await using (var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(await initBytesTask.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(await mediaBytesTask.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new WindowsMediaFoundationLiveFrameReader(new Uri(path), path);
        }
        catch
        {
            TryDeleteTemporaryFile(path);
            throw;
        }
    }

    public WindowsMediaFoundationLiveFrame? ReadNext(
        bool includePreview,
        FrameRateGate analysisGate,
        int maximumAnalysisFramesPerSecond,
        long sequence,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sample = _reader.ReadSample(
                SourceReaderIndex.FirstVideoStream,
                SourceReaderControlFlag.None,
                out _,
                out var flags,
                out var timestamp);
            if ((flags & SourceReaderFlag.Error) != 0)
            {
                throw new InvalidDataException("Media Foundation failed while decoding the HLS video fragment.");
            }
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                return null;
            }
            if (sample is null)
            {
                continue;
            }

            var includeAnalysis = analysisGate.TryAcquire(timestamp, maximumAnalysisFramesPerSecond);
            if (!includePreview && !includeAnalysis)
            {
                return new WindowsMediaFoundationLiveFrame(timestamp, null, null);
            }

            return CreateFrame(
                sample,
                timestamp,
                includePreview,
                includeAnalysis,
                sequence,
                capturedAt);
        }
    }

    private unsafe WindowsMediaFoundationLiveFrame CreateFrame(
        IMFSample sample,
        long timestamp,
        bool includePreview,
        bool includeAnalysis,
        long sequence,
        DateTimeOffset capturedAt)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var currentLength);
        try
        {
            var source = new ReadOnlySpan<byte>((void*)pointer, currentLength);
            var requiredLength = checked(_stride * (_height + _height / 2));
            if (source.Length < requiredLength)
            {
                throw new InvalidDataException(
                    $"Decoded NV12 sample is too small: {source.Length} bytes; expected at least {requiredLength}.");
            }

            Yuv420Frame? analysisFrame = null;
            SoftwareBitmap? previewBitmap = null;
            try
            {
                if (includeAnalysis)
                {
                    analysisFrame = Nv12FrameFactory.Create(
                        source,
                        _stride,
                        _width,
                        _height,
                        sequence,
                        capturedAt,
                        _rotationDegrees);
                }

                if (includePreview)
                {
                    CopyToPackedNv12(source);
                    using var nv12Bitmap = new SoftwareBitmap(
                        BitmapPixelFormat.Nv12,
                        _width,
                        _height,
                        BitmapAlphaMode.Ignore);
                    nv12Bitmap.CopyFromBuffer(_packedNv12.AsBuffer());
                    previewBitmap = SoftwareBitmap.Convert(
                        nv12Bitmap,
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied);
                }

                return new WindowsMediaFoundationLiveFrame(timestamp, previewBitmap, analysisFrame);
            }
            catch
            {
                previewBitmap?.Dispose();
                analysisFrame?.Dispose();
                throw;
            }
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private void CopyToPackedNv12(ReadOnlySpan<byte> source)
    {
        for (var row = 0; row < _height; row++)
        {
            source.Slice(row * _stride, _width)
                .CopyTo(_packedNv12.AsSpan(row * _width, _width));
        }

        var sourceChromaOffset = _stride * _height;
        var destinationChromaOffset = _width * _height;
        for (var row = 0; row < _height / 2; row++)
        {
            source.Slice(sourceChromaOffset + row * _stride, _width)
                .CopyTo(_packedNv12.AsSpan(destinationChromaOffset + row * _width, _width));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _reader.Dispose();
        MediaFactory.MFShutdown();
        if (_temporaryPath is not null)
        {
            TryDeleteTemporaryFile(_temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class WindowsMediaFoundationLiveFrame(
    long timestamp,
    SoftwareBitmap? previewBitmap,
    Yuv420Frame? analysisFrame) : IDisposable
{
    private SoftwareBitmap? _previewBitmap = previewBitmap;
    private Yuv420Frame? _analysisFrame = analysisFrame;

    public long Timestamp { get; } = timestamp;

    public SoftwareBitmap? DetachPreviewBitmap() => Interlocked.Exchange(ref _previewBitmap, null);

    public Yuv420Frame? DetachAnalysisFrame() => Interlocked.Exchange(ref _analysisFrame, null);

    public void Dispose()
    {
        Interlocked.Exchange(ref _previewBitmap, null)?.Dispose();
        Interlocked.Exchange(ref _analysisFrame, null)?.Dispose();
    }
}
