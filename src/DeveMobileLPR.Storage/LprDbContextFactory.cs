using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeveMobileLPR.Storage;

/// <summary>Creates short-lived <see cref="LprDbContext"/> instances over one SQLite file.</summary>
public sealed class LprDbContextFactory : IDbContextFactory<LprDbContext>
{
    private readonly DbContextOptions<LprDbContext> _options;

    public LprDbContextFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A private cache keeps concurrent readers from tripping over the writer; WAL, enabled once
        // the database exists, is what actually makes those readers cheap.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
        _options = new DbContextOptionsBuilder<LprDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public string DatabasePath { get; }

    public LprDbContext CreateDbContext() => new(_options);
}
