using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Expenses.Application.Identity;
using Expenses.Infrastructure.Identity;
using Expenses.Infrastructure.Persistence;
using Expenses.Test.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Expenses.Test;

[Collection("PostgreSQL")]
public sealed class RegistrationPostgresTests
{
    [PostgresFact]
    public async Task Audit_failure_rolls_back_profile_completion_and_idempotency_together()
    {
        await using var database = new RegistrationDatabase();
        await using var db = database.Context();
        var store = new RegistrationStore(db);
        var now = DateTimeOffset.UtcNow;
        var reservation = await store.ReserveAsync(database.Email, Guid.NewGuid(), Guid.NewGuid(), now, default);
        await store.RecordPrincipalAsync(reservation.OperationId,
            new ExternalIdentity(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), database.Email), now, default);
        await store.RecordPasswordAsync(reservation.OperationId, now, default);
        // This DDL runs only in the explicitly guarded disposable database, with the PG collection serialized.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION identity.test_reject_registration_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic audit failure'; END $$;
            CREATE TRIGGER test_reject_registration_audit BEFORE INSERT ON identity.audit_logs
            FOR EACH ROW EXECUTE FUNCTION identity.test_reject_registration_audit();
            """);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => store.CompleteAsync(reservation.OperationId, now, default));
            Assert.False(await db.Users.AnyAsync(x => x.NormalizedEmail == database.Email));
            var operation = await db.Registrations.AsNoTracking().SingleAsync(x => x.Id == reservation.OperationId);
            Assert.Equal("password_ready", operation.Stage);
            Assert.Null(operation.UserId);
            Assert.Equal("in_progress", await database.TextAsync(
                "SELECT status::text FROM identity.idempotency_records WHERE id = @id", operation.IdempotencyRecordId));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER test_reject_registration_audit ON identity.audit_logs;
                DROP FUNCTION identity.test_reject_registration_audit();
                """);
        }
    }

    [PostgresFact]
    public async Task Http_registration_replays_public_result_and_persists_profile_audit_and_journal()
    {
        await using var database = new RegistrationDatabase();
        var provider = new StubIdentityProvider();
        using var factory = new ApiFactory(connectionString: database.ConnectionString, identityProvider: provider);
        using var client = factory.CreateHttpsClient();
        var key = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key.ToString());
        using var first = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email.ToUpperInvariant(), password = "Example123" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await first.Content.ReadAsStringAsync();
        using var replay = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Different456" });
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.PasswordChanges);
        using var changed = await client.PostAsJsonAsync("/v1/identity/users", new { email = "different@example.test", password = "Example123" });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        using var duplicate = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Example123" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await using var db = database.Context();
        var operation = await db.Registrations.SingleAsync(x => x.NormalizedEmail == database.Email);
        Assert.Equal("completed", operation.Stage);
        Assert.Equal(3, operation.Version);
        var user = await db.Users.SingleAsync(x => x.NormalizedEmail == database.Email);
        Assert.Equal(provider.Principal!.Subject, user.CognitoSub);
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM identity.audit_logs WHERE entity_id = @id", user.Id));
        var saved = await database.TextAsync("SELECT response_payload::text FROM identity.idempotency_records WHERE id = @id", operation.IdempotencyRecordId);
        Assert.DoesNotContain("Example123", saved);
        Assert.DoesNotContain("Different456", saved);
        Assert.DoesNotContain(provider.Principal.Subject, saved);
    }

    [PostgresFact]
    public async Task Concurrent_reservations_with_same_key_have_one_owner_and_no_open_transaction_during_cognito()
    {
        await using var database = new RegistrationDatabase();
        var reachedProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StubIdentityProvider { BeforeCreate = async token =>
        {
            reachedProvider.TrySetResult();
            await release.Task.WaitAsync(token);
        } };
        using var factory = new ApiFactory(connectionString: database.ConnectionString, identityProvider: provider);
        using var client = factory.CreateHttpsClient();
        var key = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key.ToString());
        var first = client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Example123" });
        await reachedProvider.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            using var second = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Example123" });
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Contains("IDEMPOTENCY_REQUEST_IN_PROGRESS", await second.Content.ReadAsStringAsync());
            // A different key cannot claim the same email while the external call is suspended.
            using var otherClient = factory.CreateHttpsClient();
            using var competing = await otherClient.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Example123" });
            Assert.Equal(HttpStatusCode.Conflict, competing.StatusCode);
            Assert.Equal(1, provider.Creates);
        }
        finally { release.TrySetResult(); }
        using var completed = await first;
        Assert.Equal(HttpStatusCode.Created, completed.StatusCode);
    }

    [PostgresFact]
    public async Task Uncertain_creation_stays_reserved_and_does_not_repeat_external_mutation()
    {
        await using var database = new RegistrationDatabase();
        var provider = new StubIdentityProvider { CreateFailure = new IdentityProviderException(IdentityProviderFailure.Unavailable, true) };
        using var factory = new ApiFactory(connectionString: database.ConnectionString, identityProvider: provider);
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var first = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Example123" });
        using var second = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Example123" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(1, provider.Creates);
        await using var db = database.Context();
        var operation = await db.Registrations.SingleAsync(x => x.NormalizedEmail == database.Email);
        Assert.Equal("reconciliation_required", operation.Stage);
        Assert.Equal(0, await db.Users.CountAsync(x => x.NormalizedEmail == database.Email));
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM identity.identity_reconciliations WHERE operation_key = @id::text", operation.Id));
    }

    [PostgresFact]
    public async Task Known_compensation_allows_new_key_but_preserves_failed_replay_and_stale_reservations()
    {
        await using var database = new RegistrationDatabase();
        var provider = new StubIdentityProvider { PasswordFailure = new IdentityProviderException(IdentityProviderFailure.PasswordPolicy) };
        using var factory = new ApiFactory(connectionString: database.ConnectionString, identityProvider: provider);
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var failed = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Example123" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        provider.PasswordFailure = null;
        using var replay = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Example123" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode);
        Assert.Equal(1, provider.Creates);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        using var success = await client.PostAsJsonAsync("/v1/identity/users", new { email = database.Email, password = "Example123" });
        Assert.Equal(HttpStatusCode.Created, success.StatusCode);

        await using var db = database.Context();
        var store = new RegistrationStore(db);
        var staleKey = Guid.NewGuid();
        await store.ReserveAsync(database.Email + ".stale", staleKey, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), default);
        var error = await Assert.ThrowsAsync<RegistrationException>(() => store.ReserveAsync(database.Email + ".stale", staleKey,
            Guid.NewGuid(), DateTimeOffset.UtcNow, default));
        Assert.Equal(RegistrationFailure.ReconciliationRequired, error.Failure);
    }
}

