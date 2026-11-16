# Atomic Loan API (C# / .NET / PostgreSQL)

A high-concurrency, ACID-compliant financial ledger backend built with .NET and PostgreSQL. Designed with strict transactional safety, database-enforced idempotency, and double-entry bookkeeping invariants.

---

## Architecture Highlights

* **Strict ACID Transactions & Row Locking:** Uses PostgreSQL transactions with `FOR UPDATE` row-level locks to make balance reads and writes completely atomic under high concurrency.
* **Database-Enforced Idempotency:** Combines a fast-path pre-check with a database `UNIQUE` constraint on `idempotency_key`. Race conditions resulting in unique violations (`SQL State 23505`) gracefully catch, query the winner, and return consistent results.
* **Double-Entry Bookkeeping:** Every transfer enforces equal and opposite `DEBIT` and `CREDIT` ledger entries that always sum to zero, ensuring absolute audibility and reconcilability.
* **Non-Blocking Audit Pipeline:** Post-commit audit events are offloaded to a bounded .NET `Channel` (`TryWrite` with a drop-oldest policy), ensuring that downstream logging never blocks core financial throughput.

---

## System Flow

```text
Client (sends Idempotency-Key header)
  │ 
  ├─► POST /api/v1/ledger/transact
  ▼
Minimal API Endpoint
  ├─► 1. Shape validation (fail fast, no DB yet)
  ▼
LedgerService
  ├─► 2. Fast-path idempotency check (UNIQUE constraint is the true guard)
  ├─► 3. BEGIN transaction; SELECT ... FOR UPDATE (both accounts);
  │      Insert tx header + DEBIT entry + CREDIT entry; Update account balances; COMMIT
  ▼
PostgreSQL (Single Connection, Single Transaction)
  ├─► 4. TryWrite (non-blocking audit dispatch)
  ▼
System.Threading.Channels (Bounded, DropOldest)
  ▼
AuditWorker (BackgroundService)
  ▼
Structured Log = Immutable Audit Trail
```

## Core Invariants

Atomic Execution: One transfer equals one strict database transaction (1 transaction header + 2 entries + 2 balance updates) - all or nothing.

Guaranteed Idempotency: Safe to retry and safe to replay without risk of double-crediting or double-debiting.

No Overdrafts: Business validation rejects transfers if the source balance is insufficient.

Separation of Concerns: The ledger is the absolute source of truth; the audit stream is a derived, non-blocking copy.

## Data Model

``accounts``: The cached point-in-time balance table kept in sync atomically within the same transaction as the ledger entries.

``ledger_transactions``: The transaction header carrying the UNIQUE idempotency key and core metadata.

``ledger_entries``: The immutable double-entry ledger (1 DEBIT + 1 CREDIT per transfer with equal amounts). This table is the foundational source of truth for financial history.

## Prerequisites

[.NET SDK (Latest version)](https://dotnet.microsoft.com/en-us/download)

[Docker Desktop (For running PostgreSQL locally)](https://www.docker.com/products/docker-desktop/)

## Quick Start Guide

### 1. Start PostgreSQL via Docker

Spin up a persistent PostgreSQL container with a single command:

```text
docker run --name postgres-ledger -e POSTGRES_PASSWORD=your_password -e POSTGRES_DB=ledger_db -p 5432:5432 -d postgres:16
```

### 2. Configure Connection String

Ensure your appsettings.json matches your local database credentials:

```text
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=ledger_db;Username=postgres;Password=your_password"
  }
}
```

### 3. Run the Application

On startup, the automated DbInitializer connects, applies PostgresSchema.sql, and seeds initial account balances if empty.

```text
dotnet run
```

## API Reference & Testing Guide

### Endpoint

```POST /api/v1/ledger/transact```

### Headers:

```Content-Type: application/json```

```Idempotency-Key: Required client-generated unique identifier (e.g., a UUID)```

### Request Body:

```json
{
  "fromAccountId": 1,
  "toAccountId": 2,
  "amount": 500.00,
  "purpose": "Loan disbursement L-101"
}
```

### Response Codes:

```201 Created``` - Transfer applied successfully (fresh key)

```200 OK``` - Replayed response (same key, identical payload)

```409 Conflict``` - Same idempotency key used with a different request payload

```400 Bad Request``` - Schema or validation failure (missing fields, amount <= 0)

```422 Unprocessable Entity``` - Business rule violation (unknown account, insufficient funds)

## cURL Test Examples

### 1. Execute a Standard Transfer

```
curl -X POST http://localhost:5101/api/v1/ledger/transact \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: tx-uuid-001" \
  -d '{"fromAccountId":1,"toAccountId":2,"amount":100.00,"purpose":"Loan disbursement"}'
```

### 2. Test Sequential Replay (Idempotent Retry)

Re-running the exact same request with the same Idempotency-Key returns a 200 OK replay response without re-executing the transfer:

```text
curl -X POST http://localhost:5101/api/v1/ledger/transact \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: tx-demo-01" \
  -d '{"fromAccountId":1,"toAccountId":2,"amount":500.00,"purpose":"Loan disbursement L-101"}'
```

### 3. Test Concurrent Race Condition (Unique Constraint / 23505 Recovery)

Fire two identical requests simultaneously to verify that one wins the transaction insert and the other catches the unique constraint violation, queries the winner, and replays successfully.

### Using curl (Bash / WSL / Linux):

```
# Fire two requests in the background with the same idempotency key
curl -s -X POST http://localhost:5101/api/v1/ledger/transact \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: tx-race-key-99" \
  -d '{"fromAccountId":1,"toAccountId":2,"amount":50.00,"purpose":"Concurrent race test"}' &

curl -s -X POST http://localhost:5101/api/v1/ledger/transact \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: tx-race-key-99" \
  -d '{"fromAccountId":1,"toAccountId":2,"amount":50.00,"purpose":"Concurrent race test"}' &

# Wait for background jobs to finish
wait
```
### Using PowerShell (Windows / PowerShell ISE):

```powershell
$raceScript = {
    $body = @{
        fromAccountId = 1
        toAccountId = 2
        amount = 50.00
        purpose = "Concurrent race test"
    } | ConvertTo-Json

    try {
        return Invoke-RestMethod -Uri "http://localhost:5101/api/v1/ledger/transact" `
            -Method Post `
            -Headers @{ "Idempotency-Key" = "tx-race-key-99" } `
            -ContentType "application/json" `
            -Body $body
    } catch {
        $stream = $_.Exception.Response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        return $reader.ReadToEnd()
    }
}

$job1 = Start-Job -ScriptBlock $raceScript
$job2 = Start-Job -ScriptBlock $raceScript

$job1, $job2 | Wait-Job

Write-Host "--- Job 1 Result ---"
Receive-Job $job1
Write-Host "--- Job 2 Result ---"
Receive-Job $job2

Remove-Job $job1, $job2
```