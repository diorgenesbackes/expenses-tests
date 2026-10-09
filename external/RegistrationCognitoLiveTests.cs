using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Expenses.Api.Controllers;
using Expenses.Application.Identity;
using Expenses.Infrastructure.Identity;
using Expenses.Test.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Expenses.Test;

public sealed class RegistrationCognitoFactAttribute : FactAttribute
{
    public RegistrationCognitoFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EXPENSES_RUN_COGNITO_TESTS") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXPENSES_TEST_CONNECTION_STRING")))
            Skip = "Requires explicit Cognito opt-in and a disposable Liquibase database; see README.";
    }
}

[Collection("PostgreSQL")]
public sealed class RegistrationCognitoLiveTests(ITestOutputHelper output)
{
    [RegistrationCognitoFact]
    [Trait("Category", "CognitoRegistration")]
    public async Task Http_registration_with_real_postgres_and_cognito_and_latency_samples()
    {
        var sampleCount = int.TryParse(Environment.GetEnvironmentVariable("EXPENSES_COGNITO_REGISTRATION_SAMPLES"), out var requested) ? requested : 1;
        Assert.InRange(sampleCount, 1, 30);
        var durations = new List<double>();
        for (var index = 0; index < sampleCount; index++)
        {
            await using var database = new RegistrationDatabase();
            using var factory = new ApiFactory(connectionString: database.ConnectionString);
            using var client = factory.CreateHttpsClient();
            var settings = factory.Services.GetRequiredService<CognitoSettings>();
            Assert.True(settings.UserPoolId == Environment.GetEnvironmentVariable("EXPENSES_COGNITO_TEST_POOL_ID"),
                "The configured pool must match the explicitly selected development pool.");
            var provider = factory.Services.GetRequiredService<IIdentityProvider>();
            var password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
            Assert.Null(await provider.FindUserAsync(database.Email, default));
            var attempted = false;
            try
            {
                client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
                attempted = true;
                var started = Stopwatch.GetTimestamp();
                using var response = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password });
                durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var result = await response.Content.ReadFromJsonAsync<RegisterUserResponse>();
                Assert.NotNull(result);
                await using var db = database.Context();
                var profile = await db.Users.SingleAsync(x => x.Id == result.User.Id);
                var external = await provider.FindUserAsync(database.Email, default);
                Assert.NotNull(external);
                Assert.Equal(external.Subject, profile.CognitoSub);
                var tokens = await provider.AuthenticateAsync(database.Email, password, default);
                Assert.True(tokens.ExpiresInSeconds == 900);
                await provider.RevokeAsync(tokens.RefreshToken, default);
                using var replay = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password });
                Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
                var repeated = await replay.Content.ReadFromJsonAsync<RegisterUserResponse>();
                Assert.Equal(result.User.Id, repeated!.User.Id);
            }
            catch (IdentityProviderException exception)
            {
                Assert.Fail($"Real integration failed: {exception.Failure}; no provider payload was logged.");
            }
            finally
            {
                if (attempted)
                {
                    try
                    {
                        var external = await provider.FindUserAsync(database.Email, default);
                        if (external is not null)
                        {
                            Assert.Equal(database.Email, external.Email, ignoreCase: true);
                            await provider.DeleteUserAsync(external.Username, default);
                            Assert.Null(await provider.FindUserAsync(external.Username, default));
                        }
                        output.WriteLine($"Sample {index + 1}: synthetic Cognito account removed; local fixture cleanup follows.");
                    }
                    catch (IdentityProviderException exception)
                    {
                        Assert.Fail($"Cleanup failed: {exception.Failure}. Remove only synthetic user {database.Email}.");
                    }
                }
            }
        }
        var ordered = durations.Order().ToArray();
        var p95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
        var evidence = new
        {
            measuredAtUtc = DateTime.UtcNow,
            environment = "Local .NET in-process HTTP host + local PostgreSQL + Cognito us-east-1",
            operation = "POST /v1/identity/users",
            concurrency = 1, samples = sampleCount, durationsMs = durations,
            p95Ms = p95, targetMs = 150, targetMetInThisSample = p95 < 150,
            limitation = "Exploratory local sample, excludes external ingress/TLS and is not production homologation. Provider credentials warmed by preflight."
        };
        output.WriteLine(JsonSerializer.Serialize(evidence));
        var reportPath = Environment.GetEnvironmentVariable("EXPENSES_REGISTRATION_REPORT_PATH");
        if (!string.IsNullOrWhiteSpace(reportPath))
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }
}
