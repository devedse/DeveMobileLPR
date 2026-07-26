using Microsoft.Data.Sqlite;

namespace DeveMobileLPR.Storage;

internal sealed class SqliteConnectionFactory(string databasePath)
{
    public string DatabasePath { get; } = Path.GetFullPath(databasePath);

    public SqliteConnection Create(bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        return new SqliteConnection(builder.ToString());
    }
}
