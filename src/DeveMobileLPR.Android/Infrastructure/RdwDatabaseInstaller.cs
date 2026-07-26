using Android.Content;
using Android.Net;
using DeveMobileLPR.Storage;

namespace DeveMobileLPR.AndroidApp.Infrastructure;

internal static class RdwDatabaseInstaller
{
    public const string FileName = "rdw.sqlite";

    public static async Task InstallAsync(Context context, global::Android.Net.Uri source, CancellationToken cancellationToken)
    {
        var target = Path.Combine(context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Application files directory is unavailable."), FileName);
        var temporary = target + ".importing";
        await using (var input = context.ContentResolver?.OpenInputStream(source)
            ?? throw new IOException("The selected RDW database could not be opened."))
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var lookup = new SqliteRdwVehicleLookup(temporary);
            await lookup.ValidateAsync(cancellationToken).ConfigureAwait(false);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
