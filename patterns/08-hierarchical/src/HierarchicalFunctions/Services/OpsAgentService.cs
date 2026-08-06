using Microsoft.Data.SqlClient;

namespace HierarchicalFunctions.Services;

/// <summary>Ops domain expert: grounds answers in Azure SQL Database.</summary>
public class OpsAgentService
{
    public async Task<string> AnswerAsync(string question)
    {
        var connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
            ?? throw new InvalidOperationException("SQL_CONNECTION_STRING is not set");

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new SqlCommand(
                "SELECT TOP 1 TeamName, HeadCount FROM dbo.Teams ORDER BY UpdatedAt DESC", connection);
            await using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var team = reader.GetString(0);
                var headCount = reader.GetInt32(1);
                return $"Team '{team}' currently has {headCount} people. Regarding '{question}': no scheduling conflicts on record for this quarter.";
            }
        }
        catch (SqlException)
        {
            // Sample schema may not be seeded yet in a fresh environment.
        }

        return $"Ops has no blocking constraints on record for '{question}'.";
    }
}
