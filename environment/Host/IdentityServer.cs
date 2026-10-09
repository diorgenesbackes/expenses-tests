using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Expenses.Application.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Expenses.Laboratory;

// Deliberately a controlled test double, not a Cognito emulator. Never referenced by production.
public static class IdentityServer
{
    public const string Issuer = "https://identity.expenses.test";
    public const string ClientId = "expenses-local-tests";
    public static async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:" + EntryPoint.Required("LAB_IDENTITY_PORT"));
        var app = builder.Build();
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(EntryPoint.Required("LAB_PRIVATE_KEY")));
        var key = new RsaSecurityKey(rsa) { KeyId = "local-only" };
        var users = new ConcurrentDictionary<string, Principal>(StringComparer.Ordinal);
        var refreshes = new ConcurrentDictionary<string, Refresh>(StringComparer.Ordinal);
        var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-Lab-Key"] != EntryPoint.Required("LAB_CONTROL_KEY"))
            { context.Response.StatusCode = 403; return; }
            await next();
        });
        app.MapGet("/ready", () => Results.Ok());
        app.MapPost("/control", (Command command) =>
        {
            if (command.Value == "clear") failures.TryRemove(command.Email!, out _);
            else failures[command.Email!] = command.Value!;
            return Results.NoContent();
        });
        app.MapPost("/identity/{operation}", (string operation, Command command) =>
        {
            var email = command.Email ?? "";
            Principal? principal;
            switch (operation)
            {
                case "create":
                    principal = new Principal(Guid.NewGuid().ToString(), email, Hash(command.Password!));
                    return users.TryAdd(email, principal) ? Results.Json(new ExternalIdentity(email, principal.Subject, email)) : Failure(IdentityProviderFailure.UserExists);
                case "find":
                    return Results.Json(users.TryGetValue(command.Value!, out principal) ? new ExternalIdentity(principal.Email, principal.Subject, principal.Email) : null);
                case "password":
                    if (users.TryGetValue(command.Value!, out principal))
                    { users[principal.Email] = principal with { PasswordHash = Hash(command.Password!) }; return Results.NoContent(); }
                    return Failure(IdentityProviderFailure.InvalidCredentials);
                case "delete":
                    users.TryRemove(command.Value!, out _);
                    return Results.NoContent();
                case "login":
                    if (failures.TryGetValue(email, out var failure) && failure == "unavailable") return Failure(IdentityProviderFailure.Unavailable);
                    if (!users.TryGetValue(email, out principal) || !CryptographicOperations.FixedTimeEquals(principal.PasswordHash, Hash(command.Password!)))
                        return Failure(IdentityProviderFailure.InvalidCredentials);
                    return Issue(principal, DateTimeOffset.UtcNow.AddDays(7));
                case "refresh":
                    if (!refreshes.TryRemove(command.Value!, out var refresh) || refresh.ExpiresAt <= DateTimeOffset.UtcNow || !users.TryGetValue(refresh.Email, out principal))
                        return Failure(IdentityProviderFailure.InvalidCredentials);
                    if (failures.ContainsKey(principal.Email)) return Failure(IdentityProviderFailure.Unavailable);
                    return Issue(principal, refresh.ExpiresAt);
                case "revoke":
                    refreshes.TryRemove(command.Value!, out _);
                    return Results.NoContent();
                default: return Results.NotFound();
            }
        });
        await app.RunAsync();

        IResult Issue(Principal principal, DateTimeOffset absoluteExpiry)
        {
            var now = DateTime.UtcNow;
            var access = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer, IssuedAt = now, NotBefore = now, Expires = now.AddMinutes(15),
                Claims = new Dictionary<string, object> { ["sub"] = principal.Subject, ["token_use"] = "access", ["client_id"] = ClientId },
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            });
            var refresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            refreshes[refresh] = new Refresh(principal.Email, absoluteExpiry);
            return Results.Json(new TokenWire(access, refresh, 900));
        }
    }
    // Test credentials only, in memory. This is intentionally not a product password store.
    private static byte[] Hash(string password) => SHA256.HashData(Encoding.UTF8.GetBytes(password));
    private static IResult Failure(IdentityProviderFailure failure) => Results.Json(new { failure = failure.ToString() }, statusCode: 400);
    private sealed record Principal(string Subject, string Email, byte[] PasswordHash);
    private sealed record Refresh(string Email, DateTimeOffset ExpiresAt);
}
public sealed record Command(string? Email = null, string? Password = null, string? Value = null);
public sealed record TokenWire(string AccessToken, string RefreshToken, int ExpiresInSeconds);

internal sealed class LocalIdentityClient : IIdentityProvider, IDisposable
{
    private readonly HttpClient client;
    public LocalIdentityClient()
    {
        client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + EntryPoint.Required("LAB_IDENTITY_PORT")), Timeout = TimeSpan.FromSeconds(3) };
        client.DefaultRequestHeaders.Add("X-Lab-Key", EntryPoint.Required("LAB_CONTROL_KEY"));
    }
    private async Task<HttpResponseMessage> SendAsync(string operation, Command command, CancellationToken token)
    {
        HttpResponseMessage response;
        try { response = await client.PostAsJsonAsync("/identity/" + operation, command, token); }
        catch (HttpRequestException) { throw new IdentityProviderException(IdentityProviderFailure.Unavailable); }
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var error = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(token);
            throw new IdentityProviderException(Enum.Parse<IdentityProviderFailure>(error!["failure"]));
        }
    }
    public async Task<ExternalIdentity> CreateUserAsync(string email, string password, CancellationToken token)
    { using var response = await SendAsync("create", new(email, password), token); return (await response.Content.ReadFromJsonAsync<ExternalIdentity>(token))!; }
    public async Task SetPermanentPasswordAsync(string username, string password, CancellationToken token)
    { using var response = await SendAsync("password", new(Password: password, Value: username), token); }
    public async Task<ExternalIdentity?> FindUserAsync(string username, CancellationToken token)
    { using var response = await SendAsync("find", new(Value: username), token); return await response.Content.ReadFromJsonAsync<ExternalIdentity>(token); }
    public async Task DeleteUserAsync(string username, CancellationToken token)
    { using var response = await SendAsync("delete", new(Value: username), token); }
    private async Task<IdentityTokens> TokensAsync(string operation, Command command, CancellationToken token)
    { using var response = await SendAsync(operation, command, token); var result = (await response.Content.ReadFromJsonAsync<TokenWire>(token))!; return new(result.AccessToken, result.RefreshToken, result.ExpiresInSeconds); }
    public Task<IdentityTokens> AuthenticateAsync(string email, string password, CancellationToken token) => TokensAsync("login", new(email, password), token);
    public Task<IdentityTokens> RefreshAsync(string refreshToken, CancellationToken token) => TokensAsync("refresh", new(Value: refreshToken), token);
    public async Task RevokeAsync(string refreshToken, CancellationToken token)
    { using var response = await SendAsync("revoke", new(Value: refreshToken), token); }
    public void Dispose() => client.Dispose();
}
