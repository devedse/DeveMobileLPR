using DeveMobileLPR.Application;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Video.Windows;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeveMobileLPR.App.Platforms.Windows.Video;

internal sealed class WindowsVideoFileBackend : IVideoFileBackend
{
    private const int DecodeWidth = 1280;
    private static readonly string StagingDirectory = Path.Combine(FileSystem.CacheDirectory, "video-analysis");

    public async Task<string> StageAsync(SelectedVideoFile file, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(file.FullPath) && File.Exists(file.FullPath))
        {
            return file.FullPath;
        }

        Directory.CreateDirectory(StagingDirectory);
        var target = Path.Combine(StagingDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        await using var source = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return target;
    }

    public async Task<IVideoFrameSource> OpenFrameSourceAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var (_, timeline) = await OpenCompositionAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return WindowsMediaFoundationVideoFrameSource.Create(sourcePath, timeline);
    }

    public async Task<byte[]> GetPreviewAsync(string sourcePath, TimeSpan position, CancellationToken cancellationToken)
    {
        var (composition, _) = await OpenCompositionAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        using var thumbnail = await composition.GetThumbnailAsync(position, DecodeWidth, 0, VideoFramePrecision.NearestFrame);
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new DataReader(thumbnail.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)thumbnail.Size));
        var bytes = new byte[checked((int)thumbnail.Size)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<(MediaComposition Composition, VideoFrameTimeline Timeline)> OpenCompositionAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(sourcePath);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var properties = clip.GetVideoEncodingProperties();
        var timeline = WindowsVideoMetadataReader.CreateTimeline(
            clip.OriginalDuration,
            properties.FrameRate.Numerator,
            properties.FrameRate.Denominator);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        return (composition, timeline);
    }
}
