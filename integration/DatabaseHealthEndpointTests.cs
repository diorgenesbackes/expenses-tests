using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Expenses.Test.Support;

namespace Expenses.Test;

public sealed class DatabaseHealthEndpointTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Unavailable_database_returns_503_and_does_not_affect_api_health(string environment)
    {
        using var factory = new ApiFactory(environment);
        using var client = factory.CreateHttpsClient();

        using var database = await client.GetAsync("/health/db");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, database.StatusCode);
        Assert.Equal("application/json", database.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"status\":\"down\"}", await database.Content.ReadAsStringAsync());
        Assert.True(database.Headers.CacheControl?.NoStore);

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"up\"}", await health.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-private-connection-details")]
    public async Task Invalid_connection_configuration_returns_only_down_status(string connectionString)
    {
        using var factory = new ApiFactory(connectionString: connectionString);
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/health/db");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"status\":\"down\"}", await response.Content.ReadAsStringAsync());
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Unresponsive_database_is_limited_by_the_health_check_timeout()
    {
        // Accept TCP but never complete the PostgreSQL handshake. The connection's
        // 30-second timeout must be interrupted by the health check's shorter budget.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var factory = new ApiFactory(connectionString:
            $"Host=127.0.0.1;Port={port};Database=unused;Username=unused;Password=unused;Timeout=30;SSL Mode=Disable;Pooling=false");
        using var client = factory.CreateHttpsClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var elapsed = Stopwatch.StartNew();
        var request = client.GetAsync("/health/db", deadline.Token);
        using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
        using var response = await request;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"status\":\"down\"}", await response.Content.ReadAsStringAsync());
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12));
    }
}
