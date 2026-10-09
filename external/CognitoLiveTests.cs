using System.Diagnostics;
using System.Security.Cryptography;
using Expenses.Api.Configuration;
using Expenses.Application.Identity;
using Expenses.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Expenses.Test;

public sealed class CognitoFactAttribute : FactAttribute
{
    public CognitoFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EXPENSES_RUN_COGNITO_TESTS") != "1")
            Skip = "Requires explicit opt-in, a development Cognito pool and private API configuration; see README.";
    }
}

public sealed class CognitoLiveTests(ITestOutputHelper output)
{
    [CognitoFact]
    [Trait("Category", "Cognito")]
    public async Task Real_cognito_creates_authenticates_rotates_revokes_and_removes_synthetic_user()
    {
        var apiDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Expenses.Api"));
        var configuration = new ConfigurationBuilder().SetBasePath(apiDirectory)
            .AddJsonFile("appsettings.Development.json")
            .AddUserSecrets(typeof(Program).Assembly, optional: true)
            .AddEnvironmentVariables().Build();
        using var services = new ServiceCollection().AddIdentityProvider(configuration).BuildServiceProvider();
        var settings = services.GetRequiredService<CognitoSettings>();
        // An explicit target prevents an accidental run against a different pool from configuration.
        Assert.True(settings.UserPoolId == Environment.GetEnvironmentVariable("EXPENSES_COGNITO_TEST_POOL_ID"),
            "Set EXPENSES_COGNITO_TEST_POOL_ID to the intended development pool before running this test.");
        var provider = services.GetRequiredService<IIdentityProvider>();
        var email = $"expenses-check-{Guid.NewGuid():N}@example.invalid";
        var password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        ExternalIdentity? created = null;
        var createAttempted = false;
        var stage = "preflight";
        try
        {
            Assert.Null(await provider.FindUserAsync(email, default));
            stage = "create_user";
            createAttempted = true;
            created = await MeasureAsync(stage, () => provider.CreateUserAsync(email, password, default));
            Assert.Equal(email, created.Email, ignoreCase: true);
            stage = "set_password";
            await MeasureAsync(stage, async () =>
            {
                await provider.SetPermanentPasswordAsync(created.Username, password, default);
                return true;
            });
            stage = "authenticate";
            var tokens = await MeasureAsync(stage, () => provider.AuthenticateAsync(email, password, default));
            Assert.Equal(900, tokens.ExpiresInSeconds);
            stage = "refresh";
            var renewed = await MeasureAsync(stage, () => provider.RefreshAsync(tokens.RefreshToken, default));
            Assert.True(renewed.RefreshToken != tokens.RefreshToken, "Refresh token rotation must be enabled.");
            stage = "revoke";
            await MeasureAsync(stage, async () =>
            {
                await provider.RevokeAsync(renewed.RefreshToken, default);
                return true;
            });
            stage = "verify_revocation";
            var error = await Assert.ThrowsAsync<IdentityProviderException>(() => provider.RefreshAsync(renewed.RefreshToken, default));
            Assert.Equal(IdentityProviderFailure.InvalidCredentials, error.Failure);
            output.WriteLine("Cognito lifecycle verified. This does not test HTTP sessions or immediate API revocation.");
        }
        catch (IdentityProviderException exception)
        {
            // Never send SDK payloads, credentials, or tokens to the test reporter.
            Assert.Fail($"Cognito stage {stage} failed: {exception.Failure}; uncertain outcome: {exception.OutcomeMayHaveChanged}.");
        }
        finally
        {
            if (createAttempted)
            {
                // Independent cancellation budget: cleanup must still run after a failed operation.
                try
                {
                    var found = await provider.FindUserAsync(created?.Username ?? email, default);
                    if (found is not null)
                    {
                        Assert.True(string.Equals(found.Email, email, StringComparison.OrdinalIgnoreCase),
                            "Cleanup ownership check failed; no user was removed.");
                        if (created is not null) Assert.True(found.Subject == created.Subject);
                        await provider.DeleteUserAsync(found.Username, default);
                        Assert.Null(await provider.FindUserAsync(found.Username, default));
                    }
                    output.WriteLine("Synthetic user cleanup verified.");
                }
                catch (IdentityProviderException exception)
                {
                    // Synthetic identifier is intentionally available for manual recovery, never a real user's email.
                    Assert.Fail($"Cleanup failed: {exception.Failure}. Remove only synthetic user {email} from the test pool.");
                }
            }
        }
    }

    private async Task<T> MeasureAsync<T>(string operation, Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await action();
        output.WriteLine($"{operation}: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms (single sample, not p95).");
        return result;
    }
}
