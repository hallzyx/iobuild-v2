using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Persistence;

public interface IMigrationRunner
{
    Task ApplyAsync(CancellationToken cancellationToken);
}

public sealed class EfMigrationRunner(IoBuildDbContext dbContext) : IMigrationRunner
{
    public Task ApplyAsync(CancellationToken cancellationToken) => dbContext.Database.MigrateAsync(cancellationToken);
}
