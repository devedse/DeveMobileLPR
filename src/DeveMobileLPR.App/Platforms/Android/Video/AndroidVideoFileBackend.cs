using DeveMobileLPR.Application;
using DeveMobileLPR.App.Services;
using DeveMobileLPR.Recognition;
using DeveMobileLPR.Video.Android;

namespace DeveMobileLPR.App.Platforms.Android.Video;

internal sealed class AndroidVideoFileBackend : IVideoFileBackend
{
    private static readonly string StagingDirectory = Path.Combine(FileSystem.AppDataDirectory, "video-sources");

    public Task<string> StageAsync(SelectedVideoFile file, CancellationToken cancellationToken) =>
        SelectedVideoFileStager.CopyToPrivateStorageAsync(file, StagingDirectory, cancellationToken);

    public Task<IVideoFrameSource> OpenFrameSourceAsync(string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IVideoFrameSource>(new AndroidVideoFrameSource(sourcePath));
    }

    public Task<byte[]> GetPreviewAsync(string sourcePath, TimeSpan position, CancellationToken cancellationToken) =>
        AndroidVideoFrameSource.GetPreviewAsync(sourcePath, position, cancellationToken);
}
