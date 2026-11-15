using Atomic_Loan_Ledger.Db;
using Atomic_Loan_Ledger.DTOs;
using Atomic_Loan_Ledger.Services;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

// Configure Dapper globally to automatically map snake_case database columns 
// (e.g., from_account_id) to PascalCase C# properties (e.g., FromAccountId), 
// eliminating the need for manual 'AS' aliases in every SQL query.
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

// ---- Swagger ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ---- Service graph ----

// The audit channel: one bounded in-memory buffer shared by writer (LedgerService)
// and reader (AuditWorker). Bounded on purpose: unbounded = memory could grow
// without bound if the worker ever falls behind.
builder.Services.AddSingleton<Channel<TransactionAuditEvent>>(
    sp => Channel.CreateBounded<TransactionAuditEvent>(
        new BoundedChannelOptions(1024)
        {
            // FullMode = Wait would make TryWrite BLOCK. We never block the
            // client on audit. DropOldest sacrifices the oldest events first
            // (the newest are the most important), so we pick this. Production
            // would add a drop metric + sizing so this never happens.
            FullMode = BoundedChannelFullMode.DropOldest
        }));

// Stateless => safe and cheap as a singleton.
builder.Services.AddSingleton<LedgerService>();

// Our audit consumer — lifecycle managed by the host.
builder.Services.AddHostedService<AuditWorker>();

var app = builder.Build();

// ---- Db Init ----
// Bootstrap database on startup
try
{
    var connString = app.Configuration.GetConnectionString("DefaultConnection")!;
    var dbPath = Path.Combine(app.Environment.ContentRootPath, "Db");

    await DbInitializer.InitializeAsync(connString, dbPath, app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Application startup failed due to a fatal database initialization error.");
    throw;
}

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Ledger API V1");
    options.RoutePrefix = string.Empty;
});

// ---- Core endpoint ----
app.MapPost("/api/v1/ledger/transact", async (
    TransferRequest body,                                  // JSON body -> TransferRequest
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, // per-request header
    LedgerService service,
    CancellationToken ct) =>
{
    // Fail fast on shape problems BEFORE touching the database.
    if (string.IsNullOrWhiteSpace(idempotencyKey))
        return Results.BadRequest(new ApiError("The 'Idempotency-Key' header is required — it is what makes retries safe."));
    if (body.Amount <= 0)
        return Results.BadRequest(new ApiError("Amount must be greater than zero."));
    if (string.IsNullOrWhiteSpace(body.Purpose))
        return Results.BadRequest(new ApiError("'Purpose' must not be empty."));
    if (body.FromAccountId == body.ToAccountId)
        return Results.BadRequest(new ApiError("A transfer must move money between two different accounts."));

    try
    {
        var result = await service.TransactAsync(body, idempotencyKey, ct);
        // 201 on first execution, 200 on replay
        return result.IdempotentReplay
            ? Results.Ok(result)
            : Results.Created("/api/v1/ledger/transact", result);
    }
    // Map domain exceptions -> clean status codes
    catch (AccountNotFoundException ex)
    {
        return Results.UnprocessableEntity(new ApiError(ex.Message));
    }
    catch (InsufficientFundsException ex)
    {
        return Results.UnprocessableEntity(new ApiError(ex.Message));
    }
    catch (IdempotencyConflictException ex)
    {
        return Results.Conflict(new ApiError(ex.Message));
    }
});

app.Run();