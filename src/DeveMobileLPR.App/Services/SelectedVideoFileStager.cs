using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.Services;

internal static class SelectedVideoFileStager
{
    public static async Task<string> CopyToPrivateStorageAsync(
        SelectedVideoFile file,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        var target = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
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
}
