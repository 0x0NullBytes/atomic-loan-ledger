namespace Atomic_Loan_Ledger.DTOs;

/// <summary>Payload that travels from LedgerService (writer) to the AuditWorker
/// (reader) through the channel. An immutable one-way message — nothing mutates it.</summary>
public sealed record TransactionAuditEvent(
    Guid TransactionId,
    long FromAccountId,
    long ToAccountId,
    decimal Amount,
    string Purpose,
    DateTime OccurredAtUtc);