using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IoBuild.Api.Iam;
using IoBuild.Api.Persistence;
using IoBuild.Api.Workflows;
using Microsoft.EntityFrameworkCore;

namespace IoBuild.Modules.Tests;

public sealed class IamWorkflowTests
{
    [Fact]
    [Trait("Category", "IAM")]
    public async Task Registration_workflow_implements_the_transactional_workflow_boundary()
    {
        await using var db = CreateDb();
        Assert.IsAssignableFrom<IWorkflow<RegisterUser, int>>(
            new RegisterUserWorkflow(db, new PasswordHasher(), new IntegrationDispatchQueue(db), new WorkflowExecutor(db)));
    }

    [Fact]
    [Trait("Category", "IAM")]
    public async Task Registration_is_idempotent_and_creates_a_durable_dispatch_record()
    {
        await using var db = CreateDb();
        var service = CreateIamService(db);

        await service.RegisterAsync(new RegisterUser("ada@example.test", "secret", "Owner"));
        await service.RegisterAsync(new RegisterUser("ada@example.test", "secret", "Owner"));

        Assert.Single(await db.IamUsers.ToListAsync());
        Assert.Single(await db.IntegrationDispatches.ToListAsync());
    }

    [Fact]
    [Trait("Category", "IAM")]
    public async Task Dispatch_leases_in_order_and_dead_letters_after_retry_limit()
    {
        await using var db = CreateDb();
        var queue = new IntegrationDispatchQueue(db);
        await queue.EnqueueAsync(new DispatchRequest("iam", "event", "key-1", 2, "two", "two"));
        await queue.EnqueueAsync(new DispatchRequest("iam", "event", "key-1", 1, "one", "one"));
        await db.SaveChangesAsync();

        var first = await queue.LeaseDueAsync("worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        Assert.Equal("one", first!.IdempotencyKey);
        await queue.FailAsync(first.Id, "worker", DateTimeOffset.UtcNow, retryable: true, maxAttempts: 1);

        var dead = await db.IntegrationDispatches.SingleAsync(row => row.IdempotencyKey == "one");
        Assert.Equal(DispatchStatus.DeadLetter, dead.Status);
    }

    [Fact]
    [Trait("Category", "IAM")]
    public async Task Expired_leases_are_recovered_and_dead_letters_are_audit_replayed()
    {
        await using var db = CreateDb();
        var queue = new IntegrationDispatchQueue(db);
        await queue.EnqueueAsync(new DispatchRequest("iam", "event", "recovery-key", 1, "payload", "recovery"));
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var leased = await queue.LeaseDueAsync("lost-worker", now, TimeSpan.FromSeconds(1));
        var recovered = await queue.LeaseDueAsync("new-worker", now.AddSeconds(2), TimeSpan.FromMinutes(1));
        Assert.Equal(leased!.Id, recovered!.Id);
        await queue.FailAsync(recovered.Id, "new-worker", now.AddSeconds(2), retryable: false, maxAttempts: 3);
        await queue.ReplayAsync(recovered.Id, now.AddSeconds(3));
        var replayed = await db.IntegrationDispatches.SingleAsync(row => row.Id == recovered.Id);
        Assert.Equal(DispatchStatus.Pending, replayed.Status);
        Assert.Equal("audited replay", replayed.LastError);
        var replayLease = await queue.LeaseDueAsync("finisher", now.AddSeconds(3), TimeSpan.FromMinutes(1));
        await queue.CompleteAsync(replayLease!.Id, "finisher", now.AddSeconds(3));
        Assert.Equal(DispatchStatus.Completed, (await db.IntegrationDispatches.SingleAsync(row => row.Id == replayLease.Id)).Status);
    }

    [Fact]
    [Trait("Category", "IAM")]
    public async Task Expired_lease_is_recovered_before_the_next_worker_selects_due_work()
    {
        await using var db = CreateDb();
        var queue = new IntegrationDispatchQueue(db);
        await queue.EnqueueAsync(new DispatchRequest("iam", "event", "recoverable-key", 1, "payload", "recoverable"));
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;

        var firstLease = await queue.LeaseDueAsync("lost-worker", now, TimeSpan.FromSeconds(1));
        var recoveredLease = await queue.LeaseDueAsync("next-worker", now.AddSeconds(2), TimeSpan.FromMinutes(1));

        Assert.NotNull(firstLease);
        Assert.NotNull(recoveredLease);
        Assert.Equal(firstLease!.Id, recoveredLease!.Id);
        Assert.Equal("next-worker", recoveredLease.LeaseOwner);
    }

    [Fact]
    [Trait("Category", "IAM")]
    public async Task Replay_resets_attempts_so_a_dead_letter_can_receive_a_fresh_retry_budget()
    {
        await using var db = CreateDb();
        var queue = new IntegrationDispatchQueue(db);
        await queue.EnqueueAsync(new DispatchRequest("iam", "event", "replay-key", 1, "payload", "replay"));
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var lease = await queue.LeaseDueAsync("worker", now, TimeSpan.FromMinutes(1));
        await queue.FailAsync(lease!.Id, "worker", now, retryable: false, maxAttempts: 3);

        await queue.ReplayAsync(lease.Id, now.AddSeconds(1));

        var replayed = await db.IntegrationDispatches.SingleAsync(row => row.Id == lease.Id);
        Assert.Equal(0, replayed.Attempts);
        Assert.Equal(DispatchStatus.Pending, replayed.Status);
    }

    [Fact]
    [Trait("Category", "IAM")]
    public async Task MySql_dispatch_rows_survive_storage_and_recover_an_expired_lease()
    {
        var connectionString = Environment.GetEnvironmentVariable("IOBUILD_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var db = new IoBuildDbContext(new DbContextOptionsBuilder<IoBuildDbContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)).Options);
        var queue = new IntegrationDispatchQueue(db);
        var key = $"mysql-recovery-{Guid.NewGuid():N}";
        await queue.EnqueueAsync(new DispatchRequest("iam", "event", key, 1, "payload", key));
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;

        var firstLease = await queue.LeaseDueAsync("lost-mysql-worker", now, TimeSpan.FromSeconds(1));
        var recoveredLease = await queue.LeaseDueAsync("next-mysql-worker", now.AddSeconds(2), TimeSpan.FromMinutes(1));

        Assert.NotNull(firstLease);
        Assert.NotNull(recoveredLease);
        Assert.Equal(firstLease!.Id, recoveredLease!.Id);
        Assert.Equal("next-mysql-worker", recoveredLease.LeaseOwner);

        db.IntegrationDispatches.Remove(recoveredLease);
        await db.SaveChangesAsync();
    }

    [Fact]
    [Trait("Category", "IAM")]
    public async Task Revoked_tokens_are_rejected_from_the_durable_store()
    {
        await using var db = CreateDb();
        var service = CreateIamService(db);
        await service.RegisterAsync(new RegisterUser("lin@example.test", "secret", "Member"));
        var session = await service.SignInAsync(new SignIn("lin@example.test", "secret"));

        await service.RevokeAsync(session.Token);

        Assert.True(await service.IsRevokedAsync(session.Token));
    }

    private static IoBuildDbContext CreateDb() => new(
        new DbContextOptionsBuilder<IoBuildDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IamService CreateIamService(IoBuildDbContext db)
    {
        var passwordHasher = new PasswordHasher();
        var queue = new IntegrationDispatchQueue(db);
        var workflow = new RegisterUserWorkflow(db, passwordHasher, queue, new WorkflowExecutor(db));
        return new IamService(db, passwordHasher, new JwtTokenIssuer("a-test-secret-that-is-long-enough-for-hmac"), workflow);
    }
}

public sealed class IamApiContractTests
{
    [Fact]
    [Trait("Category", "IAM")]
    public async Task Registration_sign_in_and_durable_logout_preserve_the_characterized_contract()
    {
        await using var factory = new IamApiFactory();
        using var client = factory.CreateClient();
        var registration = await client.PostAsync("/api/v1/users", Json("{\"email\":\"api@example.test\",\"password\":\"secret\",\"role\":\"Owner\"}"));
        Assert.Equal(System.Net.HttpStatusCode.Created, registration.StatusCode);
        var session = await client.PostAsync("/api/v1/sessions", Json("{\"email\":\"api@example.test\",\"password\":\"secret\"}"));
        Assert.Equal(System.Net.HttpStatusCode.Created, session.StatusCode);
        var token = (await session.Content.ReadFromJsonAsync<AuthenticatedUser>())!.Token;
        using var logout = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/sessions/current");
        logout.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/users")).StatusCode);
    }

    private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    private sealed class IamApiFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        private readonly string databaseName = Guid.NewGuid().ToString();
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<Microsoft.EntityFrameworkCore.DbContextOptions<IoBuildDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<IoBuildDbContext>>();
            services.AddDbContext<IoBuildDbContext>(options => options.UseInMemoryDatabase(databaseName));
            var readiness = new IoBuild.Api.Readiness.MigrationReadiness(); readiness.RecordMigrationSuccess();
            services.AddSingleton(readiness);
        });
    }
}
