using Dapper;
using Npgsql;
using System.Threading.Channels;
using Atomic_Loan_Ledger.DTOs;

namespace Atomic_Loan_Ledger.Services;

/// <summary>
/// Owns the ledger rules:
///   1. One transfer = one DB transaction (1 tx row + 2 entries + 2 balance updates).
///   2. Every transfer is idempotent — safe to retry, safe to replay.
///   3. No overdraft, ever.
///   4. The audit stream is a non-blocking, out-of-band *copy* of committed
///      activity — the ledger itself is the source of truth.
///
/// Stateless (no mutable instance state) => safe to register as a singleton.
/// </summary>
public sealed class LedgerService(
    IConfiguration configuration,
    Channel<TransactionAuditEvent> auditChannel,
    ILogger<LedgerService> logger)
{
    /// <summary>Postgres SQL state code for unique constraint violation.</summary>
    private const string UniqueViolationSqlState = "23505";

    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection missing from appsettings.json");

    private readonly Channel<TransactionAuditEvent> _auditChannel = auditChannel;
    private readonly ILogger<LedgerService> _logger = logger;

    public async Task<TransactResult> TransactAsync(TransferRequest request, string idempotencyKey, CancellationToken ct)
    {
        // One connection for the whole operation; 'await using' disposes it on exit
        // and it returns to the pool. Every call is async (OpenAsync / ...Async) —
        // nothing ever blocks a thread-pool thread.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        // ---- 1. FAST-PATH idempotency: has this key already been used? ----
        // This SELECT is NOT the guarantee. The guarantee is the UNIQUE constraint
        // on ledger_transactions.idempotency_key, enforced by Postgres and race-
        // safe. The pre-check is just the common case, so a retry doesn't pay
        // the cost of a full transaction.
        var existing = await connection.QueryFirstOrDefaultAsync<LedgerTransactionDto>(
            """
            SELECT id, from_account_id, to_account_id, amount, purpose
            FROM ledger_transactions
            WHERE idempotency_key = @key
            """,
            new { key = idempotencyKey });
        if (existing is not null)
            return await HandleReplay(existing, request);

        // ---- 2. THE ATOMIC PART: everything below is ONE database transaction. ----
        // Any step throws => the whole block rolls back. There is no partial
        // state where one account was debited and the other was not.
        await using var tx = await connection.BeginTransactionAsync(ct);

        try
        {
            // 2a. Lock both account rows for the duration of this transaction.
            //     FOR UPDATE = exclusive row lock, held until we commit. THIS is
            //     what makes read-balance -> write-balance atomic under concurrency:
            //     a competing transfer on the same account waits on the lock and
            //     then sees the fresh balance.
            //     Alternative: optimistic UPDATE ... WHERE balance >= @amount and
            //     check rows-affected (less lock pressure at very high contention,
            //     more retry logic). I picked the lock because it's easier to
            //     reason about and explain at this scale.
            var fromBalance = await connection.QueryFirstOrDefaultAsync<decimal?>(
                "SELECT balance FROM accounts WHERE id = @fromId FOR UPDATE",
                new { fromId = request.FromAccountId }, tx);
            var toBalance = await connection.QueryFirstOrDefaultAsync<decimal?>(
                "SELECT balance FROM accounts WHERE id = @toId FOR UPDATE",
                new { toId = request.ToAccountId }, tx);

            // null means "row doesn't exist"; 0m is a valid balance.
            if (fromBalance is null) throw new AccountNotFoundException(request.FromAccountId);
            if (toBalance is null) throw new AccountNotFoundException(request.ToAccountId);

            // 2b. Business rule: no overdrafts. Rejecting here rolls the txn back
            //     (no change happened) — clean 422 for the client.
            if (fromBalance < request.Amount)
                throw new InsufficientFundsException(request.FromAccountId, fromBalance.Value, request.Amount);

            // 2c. Persist the transaction row FIRST — it carries the idempotency
            //     key (UNIQUE constraint = the real idempotency guarantee).
            //     RETURNING id so we can attach the two entries below.
            var transactionId = await connection.QueryFirstOrDefaultAsync<Guid>(
                """
                INSERT INTO ledger_transactions
                    (idempotency_key, from_account_id, to_account_id, amount, purpose)
                VALUES (@key, @fromId, @toId, @amount, @purpose)
                RETURNING id
                """,
                new
                {
                    key = idempotencyKey,
                    fromId = request.FromAccountId,
                    toId = request.ToAccountId,
                    amount = request.Amount,
                    purpose = request.Purpose
                }, tx);

            // 2d. DOUBLE-ENTRY BOOKKEEPING: every transfer has exactly two entries —
            //     one DEBIT (money leaves source) and one CREDIT (money enters
            //     destination), equal amounts, summing to zero. This invariant is
            //     what makes a ledger reconcilable and is the core of any lending/
            //     banking system (disbursements, repayments, interest accruals are
            //     all just entries).
            await connection.ExecuteAsync(
                """
                INSERT INTO ledger_entries (transaction_id, account_id, entry_type, amount)
                VALUES (@txId, @fromId, 'DEBIT', @amount)
                """,
                new { txId = transactionId, fromId = request.FromAccountId, amount = request.Amount },
                tx);

            await connection.ExecuteAsync(
                """
                INSERT INTO ledger_entries (transaction_id, account_id, entry_type, amount)
                VALUES (@txId, @toId, 'CREDIT', @amount)
                """,
                new { txId = transactionId, toId = request.ToAccountId, amount = request.Amount },
                tx);

            await tx.CommitAsync(ct);

            // ---- 3. AUDIT: non-blocking, and AFTER the commit. ----
            //   * After, not inside: the ledger is the source of truth. If we
            //     published before commit, a crash could leave audit events for a
            //     transaction that never exists.
            //   * TryWrite, not WriteAsync: the client response must never wait on
            //     audit throughput. Bounded channel + DropOldest = a defined
            //     backpressure policy (log drops; in production we'd add metrics
            //     so drops never actually happen).
            var audit = new TransactionAuditEvent(
                transactionId, request.FromAccountId, request.ToAccountId,
                request.Amount, request.Purpose, DateTime.UtcNow);
            if (!_auditChannel.Writer.TryWrite(audit))
                _logger.LogWarning("Audit event dropped (channel full), tx={TransactionId}", transactionId);

            // Post-transfer balances, for a truthful response.
            return new TransactResult
            {
                TransactionId = transactionId,
                FromAccountBalance = fromBalance.Value - request.Amount,
                ToAccountBalance = toBalance.Value + request.Amount,
                IdempotentReplay = false
            };

        }
        catch (NpgsqlException ex) when (ex.SqlState == UniqueViolationSqlState)
        {
            // RACE: two identical requests both passed the fast path and hit the
            // same key at once. One INSERT won; this one hit the UNIQUE constraint
            // (SQL state 23505). 

            // We open a fresh connection because the current transaction 'tx' is aborted.
            LedgerTransactionDto? winner;
            using (var freshConnection = new NpgsqlConnection(_connectionString))
            {
                await freshConnection.OpenAsync();
                winner = await freshConnection.QueryFirstOrDefaultAsync<LedgerTransactionDto>(
                    """
                SELECT id, from_account_id, to_account_id, amount, purpose
                FROM ledger_transactions
                WHERE idempotency_key = @key
                """,
                    new { key = idempotencyKey });
            }

            return await HandleReplay(winner, request);
        }
        // Any other exception escapes: the txn rolls back (await-using disposal)
        // and the caller maps it to a 4xx.

    }

    /// <summary>Serves a previously-processed request — a normal replay or the race winner.</summary>
    private async Task<TransactResult> HandleReplay(LedgerTransactionDto? stored, TransferRequest request)
    {
        if (stored is null)
            throw new InvalidOperationException("Could not re-read the stored transaction — please retry.");

        if (stored.FromAccountId != request.FromAccountId
            || stored.ToAccountId != request.ToAccountId
            || stored.Amount != request.Amount
            || stored.Purpose != request.Purpose)
            throw new IdempotencyConflictException(stored);

        await using var c = new NpgsqlConnection(_connectionString);
        await c.OpenAsync();

        var from = await c.QueryFirstOrDefaultAsync<decimal?>(
            "SELECT balance FROM accounts WHERE id = @id", new { id = stored.FromAccountId });
        var to = await c.QueryFirstOrDefaultAsync<decimal?>(
            "SELECT balance FROM accounts WHERE id = @id", new { id = stored.ToAccountId });

        return new TransactResult
        {
            TransactionId = stored.Id,
            FromAccountBalance = from ?? 0m,
            ToAccountBalance = to ?? 0m,
            IdempotentReplay = true
        };
    }
}