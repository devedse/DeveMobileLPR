using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DeveMobileLPR.Storage;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without an app host. The data source is
/// never opened for migration scaffolding, so a placeholder file name is enough.
/// </summary>
internal sealed class LprDesignTimeDbContextFactory : IDesignTimeDbContextFactory<LprDbContext>
{
    public LprDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<LprDbContext>()
            .UseSqlite("Data Source=design-time.sqlite")
            .Options);
}
