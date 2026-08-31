using IoBuild.Api.Persistence;

namespace IoBuild.Api.CoreBusiness;

/// <summary>
/// Publishing repository contract (Project). Shared UoW.
/// </summary>
public interface IProjectRepository
{
    Task<Project?> FindAsync(int id, CancellationToken ct = default);
    Task AddAsync(Project project, CancellationToken ct = default);
}
