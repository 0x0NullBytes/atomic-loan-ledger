using Atomic_Loan_Ledger.DTOs;
using System.Threading.Channels;

namespace Atomic_Loan_Ledger.Services;

/// <summary>
/// Background consumer of the audit channel.
/// Why Channel + BackgroundService instead of Task.Run:
///   * A Channel has defined capacity and a defined backpressure policy.
///   * A BackgroundService starts after app.Run() and stops WITH the host —
///     graceful shutdown via the token, no work leaking across restarts.
///   * The writer never blocks on this: client response latency is independent
///     of audit throughput.
/// </summary>
public sealed class AuditWorker(Channel<TransactionAuditEvent> channel, ILogger<AuditWorker> logger) : BackgroundService
{
    private readonly Channel<TransactionAuditEvent> _channel = channel;
    private readonly ILogger<AuditWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Audit worker started.");
        try
        {
            // ReadAllAsync yields every event until the writer completes or the
            // cancellation token fires. Each event becomes one audit-trail line.
            await foreach (var audit in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                // The actual audit-trail write: structured, time-ordered,
                // append-only. In production this would be a sink to a table or
                // a log pipeline (Serilog, Seq, ...): same contract, different sink.
                _logger.LogInformation(
                    "AUDIT | tx={TransactionId} {FromAccountId} -> {ToAccountId} amount={Amount:0.00} purpose={Purpose} time={OccurredAtUtc:yyyy-MM-dd HH:mm:ss}",
                    audit.TransactionId, audit.FromAccountId, audit.ToAccountId,
                    audit.Amount, audit.Purpose, audit.OccurredAtUtc);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal graceful shutdown: the host sent the token. Not an error.
        }
        finally
        {
            _logger.LogInformation("Audit worker stopped.");
        }
    }
}