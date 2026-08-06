namespace OrchestratorFunctions.Services;

/// <summary>Analytics specialist: illustrative in-process metric calculation.</summary>
public class AnalyticsToolService
{
    public Task<string> ComputeMetricAsync(string metricName)
    {
        // Stand-in for a call to a metrics store (e.g. Azure Monitor, a data warehouse).
        var value = metricName.ToLowerInvariant() switch
        {
            "churn_rate" => "3.2% quarter-over-quarter",
            "nps" => "42",
            _ => "metric not recognized",
        };

        return Task.FromResult($"{metricName}: {value}");
    }
}
