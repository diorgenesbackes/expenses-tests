using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Expenses.Test.Support;

namespace Expenses.Test;

public sealed class RegistrationEndpointTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Registration_is_available_with_safe_response_and_correlation(string environment)
    {
        using var factory = new ApiFactory(environment, identityProvider: new StubIdentityProvider(), registrationStore: new StubRegistrationStore());
        using var client = factory.CreateHttpsClient();
        var correlation = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", correlation);
        using var response = await client.PostAsJsonAsync("/v1/identity/users", new { email = "person@example.test", password = "Example123" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(correlation, response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Example123", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["createdAt", "email", "id"], json.RootElement.GetProperty("user").EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.EndsWith("Z", json.RootElement.GetProperty("user").GetProperty("createdAt").GetString());
    }

    [Theory]
    [InlineData("{\"email\":\"a@example.test\",\"password\":\"Example123\",\"role\":\"admin\"}", 400)]
    [InlineData("{\"email\":\"a@example.test\",\"password\":\"short1\"}", 422)]
    [InlineData("{\"email\":\"a@example.test\",\"password\":123}", 400)]
    [InlineData("{\"email\":\"a@example.test\",\"password\":null}", 400)]
    [InlineData("{", 400)]
    public async Task Invalid_requests_never_call_cognito_and_return_safe_problem(string body, int expected)
    {
        var provider = new StubIdentityProvider();
        using var factory = new ApiFactory(identityProvider: provider, registrationStore: new StubRegistrationStore());
        using var client = factory.CreateHttpsClient();
        using var response = await client.PostAsync("/v1/identity/users", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(expected, (int)response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Example123", error);
        using var json = JsonDocument.Parse(error);
        Assert.Equal(32, json.RootElement.GetProperty("traceId").GetString()!.Length);
        Assert.Equal(response.Headers.GetValues("X-Correlation-Id").Single(), json.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("about:blank", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, provider.Creates);
    }

    [Theory]
    [InlineData("Idempotency-Key", "invalid", 400)]
    [InlineData("Origin", "https://untrusted.example", 403)]
    [InlineData("Origin", "https://untrusted.example", 403, "/v1/identity/users/")]
    public async Task Invalid_headers_prevent_registration(string header, string value, int expected, string path = "/v1/identity/users")
    {
        var provider = new StubIdentityProvider();
        using var factory = new ApiFactory(identityProvider: provider, registrationStore: new StubRegistrationStore());
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add(header, value);
        using var response = await client.PostAsJsonAsync(path, new { email = "a@example.test", password = "Example123" });
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, provider.Creates);
    }

    [Fact]
    public async Task Oversized_body_and_unsupported_content_type_are_rejected()
    {
        var provider = new StubIdentityProvider();
        using var factory = new ApiFactory(identityProvider: provider, registrationStore: new StubRegistrationStore());
        using var client = factory.CreateHttpsClient();
        using var oversized = await client.PostAsync("/v1/identity/users", new StringContent(new string('x', 8193), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var unsupported = await client.PostAsync("/v1/identity/users", new StringContent("email=a&password=Example123"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal(0, provider.Creates);
    }
}
