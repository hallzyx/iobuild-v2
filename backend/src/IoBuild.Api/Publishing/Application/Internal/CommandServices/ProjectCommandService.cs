using IoBuild.Api.Persistence;

namespace IoBuild.Api.CoreBusiness;

/// <summary>
/// Publishing application service (Project). Former part of CoreBusinessService.
/// </summary>
public sealed class ProjectCommandService(IoBuildDbContext dbContext)
{
    public async Task<Project> CreateProjectAsync(string name, string description, string location, int totalUnits, int builderId, string? imageUrl, CancellationToken cancellationToken = default)
    {
        var project = new Project { Name = name, Description = description, Location = location, TotalUnits = totalUnits, BuilderId = builderId, ImageUrl = imageUrl };
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        return project;
    }
}

// Backward compatibility: CoreBusinessService keeps original API surface but delegates to Publishing/Profile services.
public sealed class CoreBusinessService(IoBuildDbContext dbContext)
{
    private readonly ProjectCommandService _projects = new(dbContext);
    private readonly Profiles.Application.Internal.ProfileCommandService _profiles = new(dbContext);

    public Task<Project> CreateProjectAsync(string name, string description, string location, int totalUnits, int builderId, string? imageUrl, CancellationToken cancellationToken = default)
        => _projects.CreateProjectAsync(name, description, location, totalUnits, builderId, imageUrl, cancellationToken);

    public Task<Profile> CreateProfileAsync(int userId, string name, string username, CancellationToken cancellationToken = default)
        => _profiles.CreateProfileAsync(userId, name, username, cancellationToken);
}
