using IoBuild.Api.Persistence;
using IoBuild.Api.Workflows;
using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Iam;

/// <summary>
/// IAM Application: registration workflow (transactional outbox via IntegrationDispatch).
/// </summary>
public sealed class RegisterUserWorkflow(
    IoBuildDbContext dbContext,
    PasswordHasher passwordHasher,
    IIntegrationDispatchQueue queue,
    WorkflowExecutor workflowExecutor) : IWorkflow<RegisterUser, int>
{
    public Task<int> ExecuteAsync(RegisterUser request, CancellationToken cancellationToken = default) =>
        workflowExecutor.ExecuteAsync(async cancellationToken =>
        {
            var email = request.Email.Trim().ToLowerInvariant();
            var existing = await dbContext.IamUsers.SingleOrDefaultAsync(user => user.Email == email, cancellationToken);
            if (existing is not null) return 0;
            dbContext.IamUsers.Add(new IamUser { Email = email, PasswordHash = passwordHasher.Hash(request.Password), Role = request.Role });
            await queue.EnqueueAsync(new DispatchRequest("iam", "domain-event", $"iam-user:{email}", 1, $"{{\"email\":\"{email}\",\"role\":\"{request.Role}\"}}", $"iam.user-registered:{email}"), cancellationToken);
            return 0;
        }, cancellationToken);
}
