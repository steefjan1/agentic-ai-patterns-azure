using Microsoft.Data.SqlClient;

namespace OrchestratorFunctions.Services;

/// <summary>Data specialist: structured lookups against Azure SQL Database.</summary>
public class DataToolService
{
    public async Task<string> QueryChurnAsync(string period)
    {
        var connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
            ?? throw new InvalidOperationException("SQL_CONNECTION_STRING is not set");

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.ChurnedAccounts WHERE Period = @period", connection);
            command.Parameters.AddWithValue("@period", period);

            var result = await command.ExecuteScalarAsync();
            var count = result is null or DBNull ? 0 : Convert.ToInt32(result);
            return $"{count} enterprise accounts churned in period '{period}'.";
        }
        catch (SqlException)
        {
            // Sample schema may not be seeded in a fresh environment.
            return $"No churn data available for period '{period}' (sample table not yet seeded).";
        }
    }
}
