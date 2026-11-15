using Npgsql;

namespace Atomic_Loan_Ledger.Db;

using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// Handles automated database bootstrapping, schema migration, and initial data seeding 
/// for the PostgreSQL ledger store on application startup.
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// Asynchronously establishes a connection, executes the schema definition script, 
    /// and seeds initial account records if the database is uninitialized.
    /// </summary>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="dbRootPath">The absolute or relative path to the directory containing the SQL script files.</param>
    /// <param name="logger">Optional logger instance for structured startup diagnostics.</param>
    /// <exception cref="ArgumentException">Thrown if the connection string or root path is null or whitespace.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown if the specified database root directory does not exist.</exception>
    /// <exception cref="FileNotFoundException">Thrown if the required schema SQL script is missing.</exception>
    /// <exception cref="InvalidOperationException">Thrown if a database connectivity or execution failure occurs.</exception>
    public static async Task InitializeAsync(string connectionString, string dbRootPath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbRootPath);

        if (!Directory.Exists(dbRootPath))
        {
            throw new DirectoryNotFoundException($"The database root directory could not be found at path: {dbRootPath}");
        }

        var schemaPath = Path.Combine(dbRootPath, "PostgresSchema.sql");
        var seedPath = Path.Combine(dbRootPath, "SeedData.sql");

        try
        {
            logger?.LogInformation("Initializing PostgreSQL database connection...");
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Check if the primary table already exists
            var checkCmd = new NpgsqlCommand("SELECT TO_REGCLASS('public.accounts') IS NOT NULL;", connection);
            var tableExists = (bool)(await checkCmd.ExecuteScalarAsync() ?? false);

            if (!tableExists)
            {
                if (!File.Exists(schemaPath))
                {
                    throw new FileNotFoundException("Required database schema script was not found.", schemaPath);
                }

                logger?.LogInformation("Applying database schema from {SchemaPath}...", schemaPath);
                var schemaSql = await File.ReadAllTextAsync(schemaPath);
                await using var schemaCmd = new NpgsqlCommand(schemaSql, connection);
                await schemaCmd.ExecuteNonQueryAsync();
                logger?.LogInformation("Database schema applied successfully.");
            }
            else
            {
                logger?.LogInformation("Database schema already exists. Skipping schema creation.");
            }

            // 2. Run Seed Data (only if accounts table is empty)
            var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM accounts", connection);
            var accountCount = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);

            if (accountCount == 0)
            {
                if (File.Exists(seedPath))
                {
                    logger?.LogInformation("Accounts table is empty. Seeding initial data from {SeedPath}...", seedPath);
                    var seedSql = await File.ReadAllTextAsync(seedPath);
                    await using var seedCmd = new NpgsqlCommand(seedSql, connection);
                    await seedCmd.ExecuteNonQueryAsync();
                    logger?.LogInformation("Database seeded successfully.");
                }
                else
                {
                    logger?.LogWarning("Seed data script not found at {SeedPath}; skipping seed step.", seedPath);
                }
            }
            else
            {
                logger?.LogInformation("Accounts table already contains {Count} records. Skipping seed step.", accountCount);
            }
        }
        catch (NpgsqlException ex)
        {
            logger?.LogError(ex, "A PostgreSQL error occurred during database initialization: {Message}", ex.Message);
            throw new InvalidOperationException("Failed to initialize the database due to a PostgreSQL error.", ex);
        }
    }
}