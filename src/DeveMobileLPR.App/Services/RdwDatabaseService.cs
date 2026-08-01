using DeveMobileLPR.Storage;
using DeveMobileLPR.Application;

namespace DeveMobileLPR.App.Services;

internal sealed class RdwDatabaseService : IVehicleDataStatus
{
    public const string FileName = "rdw.sqlite";
    public string DatabasePath { get; } = Path.Combine(FileSystem.AppDataDirectory, FileName);
    public bool IsInstalled => File.Exists(DatabasePath);
    public bool IsAvailable => IsInstalled;
    public long SizeBytes => IsInstalled ? new FileInfo(DatabasePath).Length : 0;
    public DateTimeOffset? UpdatedAt => IsInstalled ? new FileInfo(DatabasePath).LastWriteTimeUtc : null;

    public async Task ImportAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var temporary = DatabasePath + ".importing";
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var lookup = new SqliteRdwVehicleLookup(temporary);
            await lookup.ValidateAsync(cancellationToken).ConfigureAwait(false);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Move(temporary, DatabasePath, true);
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
