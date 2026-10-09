using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Expenses.Domain.Dto.Response;
using Expenses.Domain.Entity;
using Expenses.Test.Support;
using Npgsql;

namespace Expenses.Test;

public sealed class UsersEndpointTests
{
    [Fact]
    public async Task Empty_database_returns_200_with_pagination_defaults()
    {
        using var factory = new ApiFactory(repository: new StubUserRepository());
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/v1/identity/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<UsersPageResponse>();
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(0, page.TotalCount);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Response_exposes_only_public_user_fields()
    {
        var repository = new StubUserRepository
        {
            Count = _ => Task.FromResult(1L),
            List = (_, _, _) => Task.FromResult<IReadOnlyList<User>>([
                new User { Id = Guid.NewGuid(), Email = "public@example.test", CognitoSub = "internal-principal" }
            ])
        };
        using var factory = new ApiFactory(repository: repository);
        using var client = factory.CreateHttpsClient();
        var json = await client.GetStringAsync("/v1/identity/users?page=1&pageSize=1");
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["createdAt", "email", "id", "updatedAt"], fields);
        Assert.DoesNotContain("internal-principal", json);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=2147483647&pageSize=2")]
    [InlineData("page=2147483648")]
    [InlineData("page=abc")]
    public async Task Invalid_query_returns_400_without_accessing_database(string query)
    {
        var repository = new StubUserRepository();
        using var factory = new ApiFactory(repository: repository);
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/v1/identity/users?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("timeout", HttpStatusCode.ServiceUnavailable)]
    [InlineData("connection", HttpStatusCode.ServiceUnavailable)]
    [InlineData("wrapped-connection", HttpStatusCode.ServiceUnavailable)]
    [InlineData("schema", HttpStatusCode.InternalServerError)]
    [InlineData("wrapped-schema", HttpStatusCode.InternalServerError)]
    [InlineData("unexpected", HttpStatusCode.InternalServerError)]
    public async Task Failures_return_sanitized_problem_details(string failure, HttpStatusCode expected)
    {
        const string sensitiveMessage = "private-connection-and-user-data";
        Exception exception = failure switch
        {
            "timeout" => new TimeoutException(sensitiveMessage),
            "connection" => new NpgsqlException(sensitiveMessage, new IOException(sensitiveMessage)),
            "wrapped-connection" => new InvalidOperationException(sensitiveMessage,
                new NpgsqlException(sensitiveMessage, new IOException(sensitiveMessage))),
            "schema" => new PostgresException(sensitiveMessage, "ERROR", "ERROR", "42P01"),
            "wrapped-schema" => new InvalidOperationException(sensitiveMessage,
                new PostgresException(sensitiveMessage, "ERROR", "ERROR", "42P01")),
            _ => new InvalidOperationException(sensitiveMessage)
        };
        var repository = new StubUserRepository { Count = _ => Task.FromException<long>(exception) };
        using var factory = new ApiFactory(repository: repository);
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/v1/identity/users");
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(sensitiveMessage, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal((int)expected, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Users_route_is_absent_outside_development_and_health_remains_available(string environment)
    {
        var repository = new StubUserRepository();
        using var factory = new ApiFactory(environment, repository);
        using var client = factory.CreateHttpsClient();
        using var users = await client.GetAsync("/v1/identity/users?page=invalid");
        Assert.Equal(HttpStatusCode.NotFound, users.StatusCode);
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"up\"}", await health.Content.ReadAsStringAsync());
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Health_responds_even_when_the_database_is_unavailable()
    {
        using var factory = new ApiFactory(); // Unreachable PostgreSQL port; real repository registration.
        using var client = factory.CreateHttpsClient();
        using var users = await client.GetAsync("/v1/identity/users");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, users.StatusCode);
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
