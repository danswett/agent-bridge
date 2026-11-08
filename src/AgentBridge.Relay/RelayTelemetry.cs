using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AgentBridge.Relay;

// Exports the relay's meter (RelayMetrics) to Application Insights, which nothing did before:
// the counters existed but never left the process. Registered only when a connection string is
// configured, so a relay without one exports nothing. Metrics only, no traces or logs, and the
// meter carries no ids (RelayMetrics.AllowedTags), so nothing exported identifies a person.
public sealed class RelayTelemetry(string connectionString, ILogger<RelayTelemetry> log) : IHostedService
{
    MeterProvider? provider;

    public Task StartAsync(CancellationToken ct)
    {
        // A telemetry setting must never stop the relay from serving: a malformed string makes the
        // exporter throw here, so the relay starts without one. The exception is not logged, as
        // its message can quote the connection string.
        try
        {
            provider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(RelayMetrics.MeterName)
                .AddAzureMonitorMetricExporter(o => o.ConnectionString = connectionString)
                .Build();
        }
        catch (Exception e)
        {
            provider = null;
            log.LogWarning("Application Insights export is off: the connection string setting is not valid ({Error})", e.GetType().Name);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        // Disposing flushes the last interval, which a replica shutting down would otherwise lose.
        provider?.Dispose();
        provider = null;
        return Task.CompletedTask;
    }
}
