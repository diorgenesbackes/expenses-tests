using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Expenses.Application.Identity;
using Expenses.Domain.Entity;
using Expenses.Infrastructure.Identity;
using Expenses.Test.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Expenses.Test;

[Collection("PostgreSQL")]
public sealed class SessionPostgresTests
{
    [PostgresFact]
    public async Task Csrf_protects_login_current_logout_and_rejects_untrusted_origins()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        foreach (var (method, path) in new[] {
            (HttpMethod.Post, "/v1/identity/sessions"), (HttpMethod.Get, "/v1/identity/session/current"),
            (HttpMethod.Delete, "/v1/identity/sessions/current") })
        {
            using var denied = await fixture.SendAsync(method, path, csrf: false);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var origin = await fixture.SendAsync(HttpMethod.Get, "/v1/identity/session/current", origin: "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        Assert.Equal(0, fixture.Provider.Authentications);
        Assert.Equal(0, fixture.Provider.Refreshes);
    }

    [PostgresFact]
    public async Task Logout_database_failure_never_confirms_revocation_and_still_expires_browser_cookies()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        using var login = await fixture.LoginAsync();
        await using var db = fixture.Database.Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION identity.test_reject_session_revoke() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic persistence failure'; END $$;
            CREATE TRIGGER test_reject_session_revoke BEFORE UPDATE ON identity.sessions
            FOR EACH ROW EXECUTE FUNCTION identity.test_reject_session_revoke();
            """);
        try
        {
            using var logout = await fixture.SendAsync(HttpMethod.Delete, "/v1/identity/sessions/current");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, logout.StatusCode);
            Assert.Contains(logout.Headers.GetValues("Set-Cookie"), x => x.StartsWith("cc_rt=;"));
            Assert.DoesNotContain("synthetic persistence failure", await logout.Content.ReadAsStringAsync());
            Assert.Equal(0, fixture.Provider.Revocations);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER test_reject_session_revoke ON identity.sessions;
                DROP FUNCTION identity.test_reject_session_revoke();
                """);
        }
    }

    [PostgresFact]
    public async Task Absolute_expiry_and_abandoned_refresh_never_restore_session()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        using var login = await fixture.LoginAsync();
        var copied = fixture.CookieHeader;
        await using var db = fixture.Database.Context();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE identity.sessions SET state = 'refreshing', refresh_owner = {Guid.NewGuid()}
            WHERE user_id = {fixture.UserId}
            """);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(6);
        using var abandoned = await fixture.SendAsync(HttpMethod.Get, "/v1/identity/session/current");
        Assert.Equal(HttpStatusCode.Unauthorized, abandoned.StatusCode);
        using var restored = await fixture.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(7);
        using var expired = await fixture.SendAsync(HttpMethod.Get, "/v1/identity/session/current");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(0, fixture.Provider.Refreshes);
    }

    [PostgresFact]
    public async Task Login_current_and_logout_reject_copied_cookies_immediately_across_instances()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        using var login = await fixture.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var text = await login.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Example123", text);
        Assert.DoesNotContain("Token\"", text);
        Assert.All(login.Headers.GetValues("Set-Cookie"), value =>
        {
            Assert.Contains("secure", value); Assert.Contains("httponly", value); Assert.Contains("samesite=strict", value);
        });
        var copied = fixture.CookieHeader;
        using var protectedResponse = await fixture.SendAsync(HttpMethod.Get, "/test/session-protected");
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
        using var current = await fixture.SendAsync(HttpMethod.Get, "/v1/identity/session/current");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        fixture.Provider.RevokeFailure = true;
        using var logout = await fixture.SendAsync(HttpMethod.Delete, "/v1/identity/sessions/current");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Empty(await logout.Content.ReadAsStringAsync());
        using var otherFactory = new ApiFactory(connectionString: fixture.Database.ConnectionString,
            identityProvider: fixture.Provider, tokenValidator: fixture.Provider, clock: fixture.Clock);
        using var other = otherFactory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test/session-protected");
        request.Headers.Add("Cookie", copied);
        using var replay = await other.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [PostgresFact]
    public async Task Ten_failures_block_email_only_warn_last_three_and_expire_after_fifteen_minutes()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        fixture.Provider.LoginFailure = IdentityProviderFailure.InvalidCredentials;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var response = await fixture.LoginAsync(attempt % 2 == 0 ? fixture.Database.Email.ToUpperInvariant() : fixture.Database.Email);
            Assert.Equal(attempt == 10 ? 423 : 401, (int)response.StatusCode);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (attempt is >= 7 and <= 9) Assert.Equal(10 - attempt, json.RootElement.GetProperty("remainingAttempts").GetInt32());
            else Assert.False(json.RootElement.TryGetProperty("remainingAttempts", out _));
        }
        using var blocked = await fixture.LoginAsync();
        Assert.Equal(423, (int)blocked.StatusCode);
        Assert.Equal(10, fixture.Provider.Authentications);
        Assert.Equal(TimeSpan.FromMinutes(15), blocked.Headers.RetryAfter!.Delta);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(15);
        fixture.Provider.LoginFailure = null;
        using var success = await fixture.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        await using var db = fixture.Database.Context();
        var hash = SessionFixture.Hasher.Hash(fixture.Database.Email);
        Assert.Equal(0, await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM identity.login_attempts WHERE email_hash = {hash}").SingleAsync());
    }

    [PostgresFact]
    public async Task Operational_failures_do_not_count_and_login_lease_prevents_parallel_cognito_calls()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        fixture.Provider.LoginFailure = IdentityProviderFailure.Unavailable;
        for (var i = 0; i < 11; i++)
        {
            using var failed = await fixture.LoginAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }
        fixture.Provider.LoginFailure = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeLogin = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var first = fixture.LoginAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            using var busy = await fixture.LoginAsync();
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            Assert.Equal(12, fixture.Provider.Authentications);
        }
        finally { release.TrySetResult(); }
        using var success = await first;
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [PostgresFact]
    public async Task Refresh_rotates_cookies_without_extending_absolute_expiry_and_logout_wins_race()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        using var login = await fixture.LoginAsync();
        var original = fixture.CookieHeader;
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(16);
        using var refreshed = await fixture.SendAsync(HttpMethod.Get, "/v1/identity/session/current");
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var payload = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        Assert.True(payload.RootElement.GetProperty("session").GetProperty("refreshed").GetBoolean());
        Assert.Equal(604800 - 960, payload.RootElement.GetProperty("session").GetProperty("refreshTokenExpiresInSeconds").GetInt32());
        Assert.NotEqual(original, fixture.CookieHeader);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(16);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeRefresh = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var renewing = fixture.SendAsync(HttpMethod.Get, "/v1/identity/session/current");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            using var competing = await fixture.SendAsync(HttpMethod.Get, "/v1/identity/session/current");
            Assert.Equal(HttpStatusCode.Conflict, competing.StatusCode);
            Assert.False(competing.Headers.Contains("Set-Cookie"));
            using var logout = await fixture.SendAsync(HttpMethod.Delete, "/v1/identity/sessions/current");
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
        finally { release.TrySetResult(); }
        using var rejected = await renewing;
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        await using var db = fixture.Database.Context();
        Assert.Equal("revoked", await db.Database.SqlQuery<string>($"SELECT state AS \"Value\" FROM identity.sessions WHERE user_id = {fixture.UserId}").SingleAsync());
    }

    [PostgresFact]
    public async Task Refresh_outage_ends_session_and_csrf_is_required_even_for_get()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        using var login = await fixture.LoginAsync();
        using var noCsrf = await fixture.SendAsync(HttpMethod.Get, "/v1/identity/session/current", csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        fixture.Provider.RefreshFailure = IdentityProviderFailure.Unavailable;
        var copied = fixture.CookieHeader;
        fixture.ForgetAccessCookie();
        using var failed = await fixture.SendAsync(HttpMethod.Get, "/v1/identity/session/current");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Contains(failed.Headers.GetValues("Set-Cookie"), x => x.StartsWith("cc_rt=;"));
        using var replay = await fixture.SendAsync(HttpMethod.Get, "/test/session-protected", cookieOverride: copied);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }
}

internal sealed class SessionFixture : IAsyncDisposable
{
    public RegistrationDatabase Database { get; } = new();
    public MutableClock Clock { get; } = new();
    public SessionIdentityDouble Provider { get; private set; } = null!;
    public Guid UserId { get; } = Guid.NewGuid();
    public static EmailHasher Hasher { get; } = new(Convert.ToBase64String(new byte[32]));
    private ApiFactory factory = null!;
    private HttpClient client = null!;
    private readonly Dictionary<string, string> cookies = new();
    private string csrfToken = "";
    public void ForgetAccessCookie() => cookies.Remove("cc_at");
    public string CookieHeader => string.Join("; ", cookies.Select(x => $"{x.Key}={x.Value}"));
    public static async Task<SessionFixture> CreateAsync()
    {
        var fixture = new SessionFixture();
        var subject = Guid.NewGuid().ToString();
        fixture.Provider = new SessionIdentityDouble(fixture.Clock, subject);
        await using var db = fixture.Database.Context();
        db.Users.Add(new User { Id = fixture.UserId, Email = fixture.Database.Email, CognitoSub = subject, CreatedAt = fixture.Clock.Now });
        await db.SaveChangesAsync();
        fixture.factory = new ApiFactory(connectionString: fixture.Database.ConnectionString,
            identityProvider: fixture.Provider, tokenValidator: fixture.Provider, clock: fixture.Clock);
        fixture.client = fixture.factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        using var response = await fixture.SendAsync(HttpMethod.Get, "/v1/identity/csrf", csrf: false);
        fixture.csrfToken = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        return fixture;
    }
    public Task<HttpResponseMessage> LoginAsync(string? email = null) => SendAsync(HttpMethod.Post, "/v1/identity/sessions",
        JsonContent.Create(new { email = email ?? Database.Email, password = "Example123" }));
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null,
        bool csrf = true, string? cookieOverride = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        var cookie = cookieOverride ?? CookieHeader;
        if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (csrf) request.Headers.Add("X-CSRF-Token", csrfToken);
        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values)
            {
                var pair = value.Split(';')[0].Split('=', 2);
                if (pair[1].Length == 0) cookies.Remove(pair[0]); else cookies[pair[0]] = pair[1];
            }
        return response;
    }
    public async ValueTask DisposeAsync()
    {
        client?.Dispose(); factory?.Dispose();
        await using var db = Database.Context();
        var hash = Hasher.Hash(Database.Email);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.login_attempts WHERE email_hash = {hash}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.login_blocks WHERE key_hash = {hash}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM identity.login_guards WHERE email_hash = {hash}");
        await Database.DisposeAsync();
    }
}
