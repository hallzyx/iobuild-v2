using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using IoBuild.Api.Persistence;
using IoBuild.Api.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace IoBuild.Api.Iam;

public sealed record RegisterUser(string Email, string Password, string Role);
public sealed record SignIn(string Email, string Password);
public sealed record AuthenticatedUser(int Id, string Email, string Role, string Token);

public sealed class PasswordHasher
{
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);
    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}

public sealed class JwtTokenIssuer(string secret)
{
    private readonly byte[] key = Encoding.UTF8.GetBytes(secret);

    public string Issue(IamUser user)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.Sid, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Role, user.Role)]),
            Expires = DateTime.UtcNow.AddDays(7),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
        };
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}

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

public sealed class IamService(
    IoBuildDbContext dbContext,
    PasswordHasher passwordHasher,
    JwtTokenIssuer tokenIssuer,
    IWorkflow<RegisterUser, int> registrationWorkflow)
{
    public async Task RegisterAsync(RegisterUser request, CancellationToken cancellationToken = default) =>
        await registrationWorkflow.ExecuteAsync(request, cancellationToken);

    public async Task<AuthenticatedUser> SignInAsync(SignIn request, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.IamUsers.SingleOrDefaultAsync(item => item.Email == request.Email.Trim().ToLowerInvariant(), cancellationToken);
        if (user is null || !passwordHasher.Verify(request.Password, user.PasswordHash)) throw new UnauthorizedAccessException("Invalid email or password.");
        return new AuthenticatedUser(user.Id, user.Email, user.Role, tokenIssuer.Issue(user));
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        var hash = HashToken(token);
        if (await dbContext.RevokedTokens.AnyAsync(item => item.TokenHash == hash, cancellationToken)) return;
        dbContext.RevokedTokens.Add(new RevokedToken { TokenHash = hash, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), RevokedAt = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> IsRevokedAsync(string token, CancellationToken cancellationToken = default) =>
        dbContext.RevokedTokens.AnyAsync(item => item.TokenHash == HashToken(token) && item.ExpiresAt > DateTimeOffset.UtcNow, cancellationToken);

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
