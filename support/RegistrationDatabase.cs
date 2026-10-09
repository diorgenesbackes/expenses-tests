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

internal sealed class RegistrationDatabase : IAsyncDisposable
{
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("EXPENSES_TEST_CONNECTION_STRING")!;
    public string Email { get; } = $"registration-test-{Guid.NewGuid():N}@example.test";
    public RegistrationDatabase() => Assert.StartsWith("expenses_users_check_", new NpgsqlConnectionStringBuilder(ConnectionString).Database);
    public ExpensesDbContext Context() => new(new DbContextOptionsBuilder<ExpensesDbContext>().UseNpgsql(ConnectionString).Options);
    public async Task<long> ScalarAsync(string sql, Guid id) => (long)(await QueryAsync(sql, id))!;
    public async Task<string> TextAsync(string sql, Guid id) => (string)(await QueryAsync(sql, id))!;
    private async Task<object?> QueryAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync();
    }
    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        // Cleanup only this fixture's email and its explicit stale-reservation variant in the disposable DB.
        await using var command = new NpgsqlCommand("""
            CREATE TEMP TABLE cleanup_registration_ids AS
                SELECT id, idempotency_record_id FROM identity.registration_operations WHERE normalized_email IN (@email, @stale);
            DELETE FROM identity.identity_reconciliations WHERE operation_key IN (SELECT id::text FROM cleanup_registration_ids);
            DELETE FROM identity.registration_operations WHERE id IN (SELECT id FROM cleanup_registration_ids);
            DELETE FROM identity.idempotency_records WHERE id IN (SELECT idempotency_record_id FROM cleanup_registration_ids);
            DELETE FROM identity.audit_logs WHERE actor_user_id IN (SELECT id FROM identity.users WHERE normalized_email = @email);
            DELETE FROM identity.sessions WHERE user_id IN (SELECT id FROM identity.users WHERE normalized_email = @email);
            DELETE FROM identity.users WHERE normalized_email = @email;
            """, connection);
        command.Parameters.AddWithValue("email", Email);
        command.Parameters.AddWithValue("stale", Email + ".stale");
        await command.ExecuteNonQueryAsync();
    }
}
