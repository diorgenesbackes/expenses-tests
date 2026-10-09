using System.Net;
using System.Net.Http.Json;
using Expenses.Domain.Dto.Response;
using Expenses.Domain.Repository;
using Expenses.Infrastructure.Persistence;
using Expenses.Test.Support;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Expenses.Test;

[Collection("PostgreSQL")]
public sealed class UsersPostgresTests
{
    [PostgresFact]
    [Trait("Category", "PostgreSQL")]
    public async Task Real_postgres_mapping_pagination_and_read_only_behavior()
    {
        var connectionString = Environment.GetEnvironmentVariable("EXPENSES_TEST_CONNECTION_STRING")!;
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        Assert.StartsWith("expenses_users_check_", settings.Database);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        using var factory = new ApiFactory(connectionString: connectionString);
        using var client = factory.CreateHttpsClient();

        using var health = await client.GetAsync("/health/db");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"up\"}", await health.Content.ReadAsStringAsync());
        Assert.True(health.Headers.CacheControl?.NoStore);

        var empty = await client.GetFromJsonAsync<UsersPageResponse>("/v1/identity/users");
        Assert.NotNull(empty);
        Assert.Equal(0, empty.TotalCount);
        Assert.Empty(empty.Items);

        // Deliberately insert out of order to verify SQL ordering and page boundaries.
        Guid[] ids = [Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002")];
        foreach (var id in ids)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO identity.users(id,cognito_sub,email) VALUES ($1,$2,$3)", connection);
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue("synthetic-" + id);
            insert.Parameters.AddWithValue($"USER-{id}@example.test");
            await insert.ExecuteNonQueryAsync();
        }

        var first = await client.GetFromJsonAsync<UsersPageResponse>("/v1/identity/users?page=1&pageSize=2");
        var second = await client.GetFromJsonAsync<UsersPageResponse>("/v1/identity/users?page=2&pageSize=2");
        var pastEnd = await client.GetFromJsonAsync<UsersPageResponse>("/v1/identity/users?page=3&pageSize=2");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(pastEnd);
        Assert.Equal(3, first.TotalCount);
        Assert.Equal([ids[1], ids[2]], first.Items.Select(user => user.Id).ToArray());
        Assert.Equal(ids[0], Assert.Single(second.Items).Id);
        Assert.Equal(2, second.Page);
        Assert.Equal(2, second.PageSize);
        Assert.Equal(3, pastEnd.TotalCount);
        Assert.Empty(pastEnd.Items);
        Assert.All(first.Items, user =>
        {
            Assert.Equal(TimeSpan.Zero, user.CreatedAt.Offset);
            Assert.True(user.CreatedAt > DateTimeOffset.UnixEpoch);
            Assert.Equal(user.CreatedAt, user.UpdatedAt);
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var mapped = await repository.ListAsync(0, 10, default);
        Assert.All(mapped, user => Assert.Equal(user.Email.ToLowerInvariant(), user.NormalizedEmail));
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ExpensesDbContext>().ChangeTracker.Entries());
        await using var count = new NpgsqlCommand("SELECT count(*) FROM identity.users", connection);
        Assert.Equal(3L, await count.ExecuteScalarAsync());
    }
}
