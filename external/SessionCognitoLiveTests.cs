using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Expenses.Api.Controllers;
using Expenses.Application.Identity;
using Expenses.Infrastructure.Identity;
using Expenses.Test.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Expenses.Test;

[Collection("PostgreSQL")]
public sealed class SessionCognitoLiveTests(ITestOutputHelper output)
{
    [RegistrationCognitoFact]
    [Trait("Category", "CognitoSessions")]
    public async Task Real_http_login_refresh_and_logout_validate_jwt_and_reject_copied_credentials()
    {
        await using var database = new RegistrationDatabase();
        using var factory = new ApiFactory(connectionString: database.ConnectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        var provider = factory.Services.GetRequiredService<IIdentityProvider>();
        Assert.Equal(Environment.GetEnvironmentVariable("EXPENSES_COGNITO_TEST_POOL_ID"), factory.Services.GetRequiredService<CognitoSettings>().UserPoolId);
        Assert.Null(await provider.FindUserAsync(database.Email, default));
        var password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        var cookies = new Dictionary<string, string>();
        string? csrf = null;
        var timings = new Dictionary<string, double>();
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, string operation, HttpContent? body = null, string? replay = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body };
            var cookie = replay ?? string.Join("; ", cookies.Select(x => $"{x.Key}={x.Value}"));
            if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
            if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
            var started = Stopwatch.GetTimestamp();
            var response = await client.SendAsync(request);
            timings[operation] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values)
                {
                    var pair = value.Split(';')[0].Split('=', 2);
                    if (pair[1].Length == 0) cookies.Remove(pair[0]); else cookies[pair[0]] = pair[1];
                }
            return response;
        }
        try
        {
            using var registered = await Send(HttpMethod.Post, "/v1/identity/users", "register", JsonContent.Create(new { email = database.Email, password }));
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            using var csrfResponse = await Send(HttpMethod.Get, "/v1/identity/csrf", "csrf");
            csrf = (await csrfResponse.Content.ReadFromJsonAsync<CsrfResponse>())!.Token;
            using var login = await Send(HttpMethod.Post, "/v1/identity/sessions", "login", JsonContent.Create(new { email = database.Email, password }));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var current = await Send(HttpMethod.Get, "/v1/identity/session/current", "current");
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            // Missing access cookie is a real browser expiry condition; retain the protected refresh ticket.
            cookies.Remove("cc_at");
            using var refresh = await Send(HttpMethod.Get, "/v1/identity/session/current", "refresh");
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            Assert.True((await refresh.Content.ReadFromJsonAsync<SessionResponse>())!.Session.Refreshed);
            var copied = string.Join("; ", cookies.Select(x => $"{x.Key}={x.Value}"));
            using var protectedResponse = await Send(HttpMethod.Get, "/test/session-protected", "authorize");
            Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
            using var logout = await Send(HttpMethod.Delete, "/v1/identity/sessions/current", "logout");
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            using var denied = await Send(HttpMethod.Get, "/test/session-protected", "revoked", replay: copied);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            var evidence = new { measuredAtUtc = DateTime.UtcNow, environment = "TestServer + local PostgreSQL + real Cognito us-east-1",
                samplesPerOperation = 1, durationsMs = timings, targetMs = 150,
                limitation = "Functional smoke timings only; not p95 or production homologation. Login includes cold JWT metadata retrieval." };
            output.WriteLine(JsonSerializer.Serialize(evidence));
            var report = Environment.GetEnvironmentVariable("EXPENSES_SESSION_REPORT_PATH");
            if (!string.IsNullOrEmpty(report)) await File.WriteAllTextAsync(report, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            var external = await provider.FindUserAsync(database.Email, default);
            if (external is not null)
            {
                Assert.Equal(database.Email, external.Email, ignoreCase: true);
                await provider.DeleteUserAsync(external.Username, default);
                Assert.Null(await provider.FindUserAsync(external.Username, default));
            }
            var hash = new EmailHasher(Convert.ToBase64String(new byte[32])).Hash(database.Email);
            await using var db = database.Context();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.login_attempts WHERE email_hash = {hash}");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.login_blocks WHERE key_hash = {hash}");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.login_guards WHERE email_hash = {hash}");
            output.WriteLine("Synthetic account removed; disposable profile and session cleanup follows.");
        }
    }
}
