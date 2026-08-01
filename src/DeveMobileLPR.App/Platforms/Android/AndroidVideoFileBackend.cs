using DeveMobileLPR.Application;
using DeveMobileLPR.App.Camera;
using DeveMobileLPR.Recognition;

namespace DeveMobileLPR.App.Services;

internal sealed class AndroidVideoFileBackend : IVideoFileBackend
{
    private static readonly string StagingDirectory = Path.Combine(FileSystem.AppDataDirectory, "video-sources");

    public async Task<string> StageAsync(SelectedVideoFile file, CancellationToken cancellationToken)
    {
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

    public Task<IVideoFrameSource> OpenFrameSourceAsync(string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IVideoFrameSource>(new AndroidVideoFrameSource(sourcePath));
    }

    public Task<byte[]> GetPreviewAsync(string sourcePath, TimeSpan position, CancellationToken cancellationToken) =>
        AndroidVideoFrameSource.GetPreviewAsync(sourcePath, position, cancellationToken);
}
