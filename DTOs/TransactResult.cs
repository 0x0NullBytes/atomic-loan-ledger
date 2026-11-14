namespace Atomic_Loan_Ledger.DTOs;

/// <summary>Response for POST /api/v1/ledger/transact.
/// init-only properties: the service fills this once, then it can never change.</summary>
public sealed record TransactResult
{
    public Guid TransactionId { get; init; }

    /// <summary>Balances AFTER the transfer — the client cares about "what do I have now".</summary>
    public decimal FromAccountBalance { get; init; }
    public decimal ToAccountBalance { get; init; }

    /// <summary>true = this idempotency key was ALREADY processed and we served the
    /// stored result without re-executing. We send HTTP 200 (not 201) on replay.</summary>
    public bool IdempotentReplay { get; init; }
}

/// <summary>Consistent 4xx body shape: {"message": "..."}.
/// One consistent shape makes the API easy for any client to program against.</summary>
public sealed record ApiError(string Message);