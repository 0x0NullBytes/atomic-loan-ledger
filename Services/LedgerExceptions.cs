using Atomic_Loan_Ledger.DTOs;

namespace Atomic_Loan_Ledger.Services;

/// <summary>Domain exceptions = "the business rejected this". The endpoint maps
/// them to clean 4xx responses. A raw Exception would surface as a 500 + stack trace.</summary>

/// <summary>Account id doesn't exist. (The foreign key would reject the write too,
/// but we'd get a 500 instead of a friendly 422 — we surface it early instead.)</summary>
public sealed class AccountNotFoundException(long accountId)
    : Exception($"Account {accountId} was not found.");

/// <summary>Can't move more than the source account holds. A legitimate request
/// with a negative outcome (like a card decline) — not a bug.</summary>
public sealed class InsufficientFundsException(long accountId, decimal available, decimal requested)
    : Exception($"Account {accountId} has {available:0.00} available, {requested:0.00} was requested. Transfer rejected.")
{
    public decimal Available { get; } = available;
    public decimal Requested { get; } = requested;
}

/// <summary>Same idempotency key, DIFFERENT request. We refuse and return 409 —
/// a key must map to exactly one logical transfer.</summary>
public sealed class IdempotencyConflictException(LedgerTransactionDto existing) : Exception(
    $"Idempotency key was previously used with a different request: {existing.FromAccountId} -> {existing.ToAccountId}, {existing.Amount:0.00} ({existing.Purpose})")
{
    public LedgerTransactionDto ExistingRequest { get; } = existing;
}