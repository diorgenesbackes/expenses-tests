namespace Expenses.Test;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXPENSES_TEST_CONNECTION_STRING")))
        {
            Skip = "Run python3 ../expenses-infrastructure/scripts/test-postgres.py to provision an isolated PostgreSQL database.";
        }
    }
}

