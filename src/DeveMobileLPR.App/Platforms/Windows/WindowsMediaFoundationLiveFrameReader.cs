using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Vortice.MediaFoundation;
using Windows.Graphics.Imaging;

namespace DeveMobileLPR.App.Platforms.Windows;

/// <summary>Reads CPU-decoded BGRA frames through the native Media Foundation Source Reader.</summary>
internal sealed class WindowsMediaFoundationLiveFrameReader : IDisposable
{
    private static readonly HttpClient Client = new();
    private readonly IMFSourceReader _reader;
    private readonly string? _temporaryPath;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    public WindowsMediaFoundationLiveFrameReader(Uri playlistUri) : this(playlistUri, null)
    {
    }

    private WindowsMediaFoundationLiveFrameReader(Uri playlistUri, string? temporaryPath)
    {
        _temporaryPath = temporaryPath;
        MediaFactory.MFStartup();
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(1);
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1);

            try
            {
                _reader = MediaFactory.MFCreateSourceReaderFromURL(playlistUri.AbsoluteUri, attributes);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"Opening the completed MP4 fragment failed: {exception.Message}", exception);
            }
            _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using var outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            try
            {
                _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"Selecting CPU RGB32 output failed: {exception.Message}", exception);
            }

            using var actualType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            var packedSize = actualType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            _width = (int)(packedSize >> 32);
            _height = (int)(packedSize & uint.MaxValue);
        }
        catch
        {
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
        var directory = Path.Combine(Path.GetTempPath(), "DeveMobileLPR", "native-hls");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.mp4");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous))
        {
            await output.WriteAsync(await initBytesTask.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(await mediaBytesTask.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        try
        {
            return new WindowsMediaFoundationLiveFrameReader(new Uri(path), path);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public SoftwareBitmap? ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            using var sample = _reader.ReadSample(
                SourceReaderIndex.FirstVideoStream,
                SourceReaderControlFlag.None,
                out _,
                out var flags,
                out _);
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                return null;
            }
            if (sample is null)
            {
                continue;
            }

            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var pointer, out _, out var currentLength);
            try
            {
                var pixels = new byte[currentLength];
                Marshal.Copy(pointer, pixels, 0, pixels.Length);
                var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, _width, _height, BitmapAlphaMode.Ignore);
                bitmap.CopyFromBuffer(pixels.AsBuffer());
                return bitmap;
            }
            finally
            {
                buffer.Unlock();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _reader.Dispose();
        MediaFactory.MFShutdown();
        if (_temporaryPath is not null)
        {
            try { File.Delete(_temporaryPath); } catch (IOException) { }
        }
    }
}
