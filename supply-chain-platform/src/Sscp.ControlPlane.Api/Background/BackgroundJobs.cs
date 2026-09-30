// Periodic work of the Control Plane.
//
// - Exception expiry: marks exceptions whose end date has passed as Expired and audits it.
//   (Expired exceptions already stop counting at that moment because the evaluator checks
//   the clock; this job makes the change visible and audited.)
// - State gauges: refreshes the numbers behind the sscp_artifacts and sscp_exceptions
//   gauges so dashboards show how many artifacts are in each trust state.
// - Run watcher: fails builds and releases whose pipeline run ended without finishing them.
// Failures are logged and retried on the next tick; a database outage never stops the host.
using System.Diagnostics.Metrics;
using Sscp.ControlPlane.Application.Exceptions;
using Sscp.ControlPlane.Application.Metrics;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Infrastructure.Queries;

namespace Sscp.ControlPlane.Api.Background;

public sealed class BackgroundOptions
{
    public const string SectionName = "Background";
    public TimeSpan ExceptionExpiryInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan StateGaugeInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RunWatchInterval { get; set; } = TimeSpan.FromSeconds(30);
}

// Fails builds and releases whose pipeline run ended without finishing them (crash,
// cancellation, timeout), so nothing waits forever and the commit status shows failure.
public sealed partial class RunWatcherJob(IServiceScopeFactory scopes, BackgroundOptions options, ILogger<RunWatcherJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RunWatchInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var reconciled = await scope.ServiceProvider.GetRequiredService<OrchestrationService>().ReconcileRunsAsync(stoppingToken);
                if (reconciled > 0)
                {
                    LogReconciled(logger, reconciled);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogFailed(logger, error);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed {Count} builds or releases whose pipeline run ended without finishing them")]
    private static partial void LogReconciled(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipeline run reconciliation failed; retrying on the next tick")]
    private static partial void LogFailed(ILogger logger, Exception error);
}

public sealed partial class ExceptionExpiryJob(IServiceScopeFactory scopes, BackgroundOptions options, ILogger<ExceptionExpiryJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ExceptionExpiryInterval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var expired = await scope.ServiceProvider.GetRequiredService<ExceptionService>().ExpireDueAsync(stoppingToken);
                if (expired > 0)
                {
                    LogExpired(logger, expired);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogFailed(logger, error);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked {Count} risk exceptions as expired")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception expiry run failed; retrying on the next tick")]
    private static partial void LogFailed(ILogger logger, Exception error);
}

public sealed partial class StateGaugeJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BackgroundOptions _options;
    private readonly ILogger<StateGaugeJob> _logger;
    private StateCounts? _latest;

    public StateGaugeJob(IServiceScopeFactory scopes, BackgroundOptions options, ILogger<StateGaugeJob> logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
        ControlPlaneMetrics.Meter.CreateObservableGauge("sscp_artifacts", ObserveArtifacts, description: "Artifacts per trust state");
        ControlPlaneMetrics.Meter.CreateObservableGauge("sscp_exceptions", ObserveExceptions, description: "Risk exceptions per status");
        ControlPlaneMetrics.Meter.CreateObservableGauge("sscp_exceptions_expiring_7d", () => _latest?.ExceptionsExpiringWithinWeek ?? 0,
            description: "Approved risk exceptions that expire within seven days");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.StateGaugeInterval);
        do
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                _latest = await scope.ServiceProvider.GetRequiredService<ControlPlaneQueries>().CountsAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogFailed(_logger, error);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private IEnumerable<Measurement<int>> ObserveArtifacts() =>
        _latest?.Artifacts.Select(pair => new Measurement<int>(pair.Value, new KeyValuePair<string, object?>("state", pair.Key.ToString())))
        ?? Enumerable.Empty<Measurement<int>>();

    private IEnumerable<Measurement<int>> ObserveExceptions() =>
        _latest?.Exceptions.Select(pair => new Measurement<int>(pair.Value, new KeyValuePair<string, object?>("status", pair.Key.ToString())))
        ?? Enumerable.Empty<Measurement<int>>();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing trust-state gauges failed; retrying on the next tick")]
    private static partial void LogFailed(ILogger logger, Exception error);
}
