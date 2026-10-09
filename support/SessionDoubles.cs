using System.Collections.Concurrent;
using Expenses.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Expenses.Test.Support;

internal sealed class SessionIdentityDouble(MutableClock clock, string subject) : IIdentityProvider, IAccessTokenValidator
{
    private readonly ConcurrentDictionary<string, VerifiedAccess> tokens = new();
    public int Authentications;
    public int Refreshes;
    public int Revocations;
    public IdentityProviderFailure? LoginFailure;
    public IdentityProviderFailure? RefreshFailure;
    public bool RevokeFailure;
    public Func<CancellationToken, Task>? BeforeLogin;
    public Func<CancellationToken, Task>? BeforeRefresh;
    public async Task<IdentityTokens> AuthenticateAsync(string email, string password, CancellationToken token)
    {
        Interlocked.Increment(ref Authentications);
        if (BeforeLogin is not null) await BeforeLogin(token);
        if (LoginFailure.HasValue) throw new IdentityProviderException(LoginFailure.Value);
        return Issue();
    }
    public async Task<IdentityTokens> RefreshAsync(string refreshToken, CancellationToken token)
    {
        Interlocked.Increment(ref Refreshes);
        if (BeforeRefresh is not null) await BeforeRefresh(token);
        if (RefreshFailure.HasValue) throw new IdentityProviderException(RefreshFailure.Value);
        return Issue();
    }
    private IdentityTokens Issue()
    {
        var access = Guid.NewGuid().ToString();
        tokens[access] = new(subject, clock.GetUtcNow().AddMinutes(15));
        return new IdentityTokens(access, Guid.NewGuid().ToString(), 900);
    }
    public Task<VerifiedAccess> ValidateAsync(string token, CancellationToken cancellationToken) =>
        tokens.TryGetValue(token, out var value) && value.ExpiresAt > clock.GetUtcNow() ? Task.FromResult(value)
            : Task.FromException<VerifiedAccess>(new SessionException(SessionFailure.InvalidCredentials));
    public Task RevokeAsync(string refreshToken, CancellationToken token)
    {
        Interlocked.Increment(ref Revocations);
        return RevokeFailure ? Task.FromException(new IdentityProviderException(IdentityProviderFailure.Unavailable)) : Task.CompletedTask;
    }
    public Task<ExternalIdentity> CreateUserAsync(string email, string password, CancellationToken token) => throw new NotSupportedException();
    public Task SetPermanentPasswordAsync(string username, string password, CancellationToken token) => throw new NotSupportedException();
    public Task<ExternalIdentity?> FindUserAsync(string username, CancellationToken token) => throw new NotSupportedException();
    public Task DeleteUserAsync(string username, CancellationToken token) => throw new NotSupportedException();
}

