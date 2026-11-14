namespace Atomic_Loan_Ledger.DTOs;

/// <summary>Shape Dapper reads back from ledger_transactions when we look a
/// transaction up by its idempotency key. Dapper maps columns to properties by
/// name (case-insensitive, ignores underscores), so from_account_id -> FromAccountId.</summary>
public sealed record LedgerTransactionDto
{
    public required Guid Id { get; init; }
    public required long FromAccountId { get; init; }
    public required long ToAccountId { get; init; }
    public required decimal Amount { get; init; }
    public required string Purpose { get; init; }
}