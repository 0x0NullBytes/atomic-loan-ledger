namespace Atomic_Loan_Ledger.DTOs;

/// <summary>
/// What the client sends.
/// 'record' => immutable, value-based, and Equals/GetHashCode come for free.
/// The web framework converts JSON names to PascalCase for us
/// ({"fromAccountId": 1} binds to FromAccountId).
/// 'required' => if a field is missing from the JSON, the framework returns
/// 400 automatically (model validation). No hand check needed for SHAPE;
/// I check business RULES separately in the endpoint.
/// </summary>
public sealed record TransferRequest
{
    /// <summary>Source account id (e.g. 1 = 'Loan Portfolio').</summary>
    public required long FromAccountId { get; init; }

    /// <summary>Destination account id (e.g. 2 = 'Borrower Account').</summary>
    public required long ToAccountId { get; init; }

    /// <summary>Amount to move. Always decimal — money is never floating point
    /// (double can't represent 0.07 exactly).</summary>
    public required decimal Amount { get; init; }

    /// <summary>Human reason, stored verbatim on the transaction row.</summary>
    public required string Purpose { get; init; }
}