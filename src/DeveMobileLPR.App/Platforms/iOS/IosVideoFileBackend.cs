using AVFoundation;
using CoreGraphics;
using CoreMedia;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;
using DeveMobileLPR.Recognition;
using Foundation;
using UIKit;

namespace DeveMobileLPR.App.Services;

internal sealed class IosVideoFileBackend : IVideoFileBackend
{
    private static readonly string StagingDirectory = Path.Combine(FileSystem.AppDataDirectory, "video-sources");

    public async Task<string> StageAsync(SelectedVideoFile file, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StagingDirectory);
        var target = Path.Combine(StagingDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        await using var source = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return target;
    }

    public Task<IVideoFrameSource> OpenFrameSourceAsync(string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IVideoFrameSource>(new IosVideoFrameSource(sourcePath));
    }

    public Task<byte[]> GetPreviewAsync(string sourcePath, TimeSpan position, CancellationToken cancellationToken) =>
        Task.Run(() => IosVideoFrameSource.RenderJpeg(sourcePath, position), cancellationToken);
}

internal sealed class IosVideoFrameSource : IVideoFrameSource
{
    private readonly AVUrlAsset _asset;
    private readonly AVAssetImageGenerator _generator;

    public IosVideoFrameSource(string sourcePath)
    {
        _asset = AVUrlAsset.FromUrl(NSUrl.FromFilename(sourcePath));
        _generator = new AVAssetImageGenerator(_asset)
        {
            AppliesPreferredTrackTransform = true,
            RequestedTimeToleranceBefore = CMTime.Zero,
            RequestedTimeToleranceAfter = CMTime.Zero
        };
        var duration = TimeSpan.FromSeconds(Math.Max(0.001, _asset.Duration.Seconds));
        var track = _asset.TracksWithMediaType(AVMediaTypes.Video).FirstOrDefault();
        var frameRate = track?.NominalFrameRate is > 0 ? track.NominalFrameRate : 30;
        Timeline = VideoFrameTimeline.Create(duration, frameRate);
    }

    public VideoFrameTimeline Timeline { get; }

    public ValueTask<Yuv420Frame?> DecodeAsync(long sourceFrameIndex, TimeSpan position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = CopyImage(_generator, position);
        return ValueTask.FromResult<Yuv420Frame?>(ToFrame(image, sourceFrameIndex + 1, position));
    }

    internal static byte[] RenderJpeg(string sourcePath, TimeSpan position)
    {
        using var asset = AVUrlAsset.FromUrl(NSUrl.FromFilename(sourcePath));
        using var generator = new AVAssetImageGenerator(asset) { AppliesPreferredTrackTransform = true };
        using var image = CopyImage(generator, position);
        using var uiImage = UIImage.FromImage(image);
        using var data = uiImage.AsJPEG(0.88f) ?? throw new InvalidDataException("The video preview could not be encoded.");
        return data.ToArray();
    }

    private static CGImage CopyImage(AVAssetImageGenerator generator, TimeSpan position)
    {
        var requested = CMTime.FromSeconds(position.TotalSeconds, 600);
        var image = generator.CopyCGImageAtTime(requested, out _, out var error);
        if (image is null) throw new InvalidDataException($"The selected video frame could not be decoded: {error?.LocalizedDescription}");
        return image;
    }

    private static Yuv420Frame ToFrame(CGImage image, long sequence, TimeSpan position)
    {
        var width = checked((int)image.Width);
        var height = checked((int)image.Height);
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        using var colorSpace = CGColorSpace.CreateDeviceRGB();
        using var context = new CGBitmapContext(
            pixels, width, height, 8, stride, colorSpace,
            CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.PremultipliedFirst);
        context.DrawImage(new CGRect(0, 0, width, height), image);
        return BgraFrameFactory.Create(
            pixels, width, height, stride, sequence, DateTimeOffset.UnixEpoch + position);
    }

    public void Dispose()
    {
        _generator.Dispose();
        _asset.Dispose();
    }
}
