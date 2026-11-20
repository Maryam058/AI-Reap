using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using AiReap.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiReap.Tests;

// SQL integration-test safety guard. Every test database is created, verified and dropped here, against
// one explicitly configured disposable SQL Server, and nothing is ever adopted or cleaned up on a guess:
// whenever the guard cannot establish what happened, the database becomes Orphaned (reported, never
// dropped, never reused).
//
// Identity checks are a guard against configuration mistakes, not cryptographic proof:
// - The marker database (AiReapDisposableTestInstance) only records that someone declared the instance
//   disposable. It can be created on any instance or carried across by a restore.
// - AIREAP_TEST_SQL_EXPECTED_SERVER is compared with SERVERPROPERTY('ServerName'), which two instances
//   can share (for example two containers with the same hostname).
//
// Not verified offline (needs a separately approved SQL-backed run): THROW propagation out of nested
// CATCH and under XACT_ABORT, the scope of SET inside sp_executesql, SQL Server refusing database DDL
// inside a transaction, USE inside dynamic SQL, DB_ID/metadata visibility and HAS_PERMS_BY_NAME with
// DENY, SqlClient folding warnings into SqlException.Errors, StateChange firing on close, server-side
// session cleanup after a non-pooled close, the SINGLE_USER/DROP race, 4060 timing right after CREATE,
// and EF Core 8 Migrate() using only the supplied open connection.
public sealed class TestSqlGuardException : Exception
{
    public TestSqlGuardException(string message) : base(message)
    {
    }
}

// Validated server-level settings. A class rather than a record: a record's generated ToString would
// print the builder, password included.
public sealed class TestSqlSettings
{
    public const string ConnectionVariable = "AIREAP_TEST_SQL_CONNECTION";
    public const string ExpectedServerVariable = "AIREAP_TEST_SQL_EXPECTED_SERVER";
    public const string MarkerDatabase = "AiReapDisposableTestInstance";

    private readonly string _server; // InitialCatalog = master, after the caller's options were checked
    private readonly string _password;

    public string DataSource { get; }
    public string ExpectedServer { get; }

    private TestSqlSettings(SqlConnectionStringBuilder server, string expectedServer)
    {
        _server = server.ConnectionString;
        _password = server.Password ?? "";
        DataSource = server.DataSource;
        ExpectedServer = expectedServer;
    }

    // Reads only these two values. There is deliberately no fallback to user-secrets, appsettings or
    // ConnectionStrings__Default: a developer's own database must never be reachable by accident.
    public static TestSqlSettings Resolve(Func<string, string?> getEnvironmentVariable)
    {
        var raw = getEnvironmentVariable(ConnectionVariable);
        var expected = getEnvironmentVariable(ExpectedServerVariable);
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(expected))
        {
            throw new TestSqlGuardException(
                $"SQL-backed tests need both {ConnectionVariable} (a server-level connection string to a disposable SQL Server, " +
                $"without Database=) and {ExpectedServerVariable} (that instance's SERVERPROPERTY('ServerName')), and a database " +
                $"named {MarkerDatabase} on that instance. There is no fallback to user-secrets or any other configuration.");
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(raw);
        }
        catch
        {
            // No inner exception and no echo of the input: a parse error could quote the password.
            throw new TestSqlGuardException($"{ConnectionVariable} is not a valid SQL Server connection string.");
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new TestSqlGuardException($"{ConnectionVariable} must name a server (Data Source/Server).");
        }

        // Each of these could put a connection on a database or server other than the verified one.
        if (!string.IsNullOrEmpty(builder.InitialCatalog) || builder.ShouldSerialize("Initial Catalog"))
        {
            throw new TestSqlGuardException($"{ConnectionVariable} must not set Database/Initial Catalog; the guard chooses every database.");
        }

        if (!string.IsNullOrEmpty(builder.AttachDBFilename) || builder.ShouldSerialize("AttachDbFilename"))
        {
            throw new TestSqlGuardException($"{ConnectionVariable} must not set AttachDBFilename.");
        }

        if (!string.IsNullOrEmpty(builder.FailoverPartner) || builder.ShouldSerialize("Failover Partner"))
        {
            throw new TestSqlGuardException($"{ConnectionVariable} must not set Failover Partner.");
        }

        if (builder.ApplicationIntent == ApplicationIntent.ReadOnly)
        {
            throw new TestSqlGuardException($"{ConnectionVariable} must not set ApplicationIntent=ReadOnly (read-only routing can redirect to another replica).");
        }

        if (expected.Length > 256)
        {
            throw new TestSqlGuardException($"{ExpectedServerVariable} is longer than 256 characters.");
        }

        builder.InitialCatalog = "master";
        return new TestSqlSettings(builder, expected);
    }

    // For the guard's own sessions (master batches and verified opens). Non-pooled, so disposing the
    // connection ends the session instead of parking it in a pool; no connection-resiliency reconnects;
    // never enlisted in an ambient transaction.
    internal string GuardConnectionString(string catalog) =>
        new SqlConnectionStringBuilder(_server)
        {
            InitialCatalog = catalog,
            Pooling = false,
            ConnectRetryCount = 0,
            Enlist = false,
            PersistSecurityInfo = false,
            ApplicationName = "AiReap.Tests.Guard",
        }.ConnectionString;

    // For the API host and the existing raw-connection tests. Otherwise exactly the caller's settings.
    internal string HandOutConnectionString(string database) =>
        new SqlConnectionStringBuilder(_server)
        {
            InitialCatalog = database,
            ApplicationName = "AiReap.Tests",
        }.ConnectionString;

    internal string Redact(string text) =>
        _password.Length == 0 ? text : text.Replace(_password, "***", StringComparison.Ordinal);

    public override string ToString() => $"DataSource={DataSource}; ExpectedServer={ExpectedServer}";
}

public enum TestDatabaseState
{
    Creating,
    Ready,
    Dropping,
    Dropped,
    Orphaned,
}

public sealed record DropOutcome(TestDatabaseState State, string Detail);

internal enum CreateOutcome
{
    Created,
    NotCreated,
    CleanedUpBySameBatch,
    Unclear,
}

internal enum VerifyOutcome
{
    Verified,
    Mismatch,
    Failed,
}

internal sealed record GuardSqlError(int Number, byte Class, byte State, string? Message);

internal sealed record GuardParameter(string Name, SqlDbType Type, int Size, string Value);

internal sealed record GuardRow(IReadOnlyList<(string Name, object? Value)> Columns);

internal sealed record GuardResultSet(IReadOnlyList<GuardRow> Rows);

// What a batch produced. The runner never throws for a SQL outcome; classification happens afterwards,
// in pure functions, so no outer catch can reclassify an error.
internal abstract record BatchResult
{
    internal sealed record Completed(IReadOnlyList<GuardResultSet> ResultSets) : BatchResult;

    // RowsSeen: rows had already arrived when the error was raised.
    internal sealed record SqlFailed(IReadOnlyList<GuardSqlError> Errors, bool RowsSeen) : BatchResult;

    // Timeout outside SqlException, transport/IO, cancellation, or any other non-SQL exception.
    internal sealed record Faulted(string Description) : BatchResult;
}

internal abstract record OpenResult
{
    internal sealed record Opened(SqlConnection Connection) : OpenResult;

    // DisposeFailed: the half-opened connection could not be disposed, so a session may still be live.
    internal sealed record Failed(IReadOnlyList<GuardSqlError> Errors, string Description, bool DisposeFailed) : OpenResult;
}

// The only place real SQL Server calls happen. Offline tests substitute a recording fake.
internal interface ITestSqlBatchRunner
{
    Task<OpenResult> OpenAsync(string connectionString);

    Task<BatchResult> ExecuteAsync(SqlConnection connection, string sql, IReadOnlyList<GuardParameter> parameters);

    void Close(SqlConnection connection);

    void ClearPool(string connectionString);
}

internal sealed class SqlBatchRunner : ITestSqlBatchRunner
{
    public async Task<OpenResult> OpenAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString) { FireInfoMessageEventOnUserErrors = false };
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            return new OpenResult.Opened(connection);
        }
        catch (Exception ex)
        {
            var disposeFailed = false;
            try
            {
                connection.Dispose();
            }
            catch
            {
                disposeFailed = true;
            }

            return new OpenResult.Failed(ErrorsOf(ex), $"{ex.GetType().Name}: {ex.Message}", disposeFailed);
        }
    }

    public async Task<BatchResult> ExecuteAsync(SqlConnection connection, string sql, IReadOnlyList<GuardParameter> parameters)
    {
        // If this were true, errors of severity <= 16 (every guard THROW) would arrive as info messages
        // and the batch would look Completed.
        if (connection.FireInfoMessageEventOnUserErrors)
        {
            return new BatchResult.Faulted("FireInfoMessageEventOnUserErrors is enabled on a guard connection.");
        }

        var sets = new List<GuardResultSet>();
        var rowsSeen = false;
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = TestSqlServer.CommandTimeoutSeconds };
            foreach (var p in parameters)
            {
                command.Parameters.Add(new SqlParameter(p.Name, p.Type, p.Size) { Value = p.Value });
            }

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            do
            {
                var rows = new List<GuardRow>();
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    rowsSeen = true;
                    var columns = new List<(string, object?)>(reader.FieldCount);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        columns.Add((reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i)));
                    }

                    rows.Add(new GuardRow(columns));
                }

                if (reader.FieldCount > 0)
                {
                    sets.Add(new GuardResultSet(rows));
                }
            }
            while (await reader.NextResultAsync().ConfigureAwait(false));

            return new BatchResult.Completed(sets);
        }
        catch (SqlException ex)
        {
            return new BatchResult.SqlFailed(ErrorsOf(ex), rowsSeen);
        }
        catch (Exception ex)
        {
            return new BatchResult.Faulted($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Close(SqlConnection connection) => connection.Dispose();

    public void ClearPool(string connectionString)
    {
        using var connection = new SqlConnection(connectionString); // never opened
        SqlConnection.ClearPool(connection);
    }

    private static IReadOnlyList<GuardSqlError> ErrorsOf(Exception ex) =>
        ex is SqlException sql
            ? sql.Errors.Cast<SqlError>().Select(e => new GuardSqlError(e.Number, e.Class, e.State, e.Message)).ToList()
            : [];
}

// One registry entry per database name this process issued. Only TestSqlServer changes it, under its lock.
internal sealed class GuardEntry
{
    public GuardEntry(string name) => Name = name;

    public string Name { get; }
    public TestDatabaseState State { get; set; } = TestDatabaseState.Creating;
    public int Leases { get; set; }
    public int Reservations { get; set; }
    public string? Reason { get; set; }
    public List<string> Notes { get; } = new();
}

public sealed class TestSqlServer
{
    internal const int MaxOpenAttempts = 3;
    internal const int CommandTimeoutSeconds = 120;
    internal const int LoginDatabaseUnavailable = 4060;

    // Raised before any DDL: nothing was created.
    internal static readonly int[] NotCreatedCodes = [51000, 51001, 51002, 51003, 51004, 51010, 51013, 51014];
    internal static readonly int[] CreateCodes = [.. NotCreatedCodes, 51006, 51007, 51008, 51009];
    internal static readonly int[] DropCodes = [51000, 51001, 51002, 51003, 51005, 51010, 51013, 51014];

    // A verified open that hits one of these has found a different target: the entry becomes Orphaned.
    internal static readonly int[] VerifyMismatchCodes = [51001, 51002, 51005, 51012];
    internal static readonly int[] VerifyCodes = [.. VerifyMismatchCodes, 51003, 51010, 51013, 51014];

    private static readonly Lazy<TestSqlServer> LazyDefault = new(
        () => FromEnvironment(Environment.GetEnvironmentVariable, new SqlBatchRunner()),
        LazyThreadSafetyMode.ExecutionAndPublication); // keeps the first failure; never "recovers" onto another target

    private readonly object _gate = new();
    private readonly Dictionary<string, GuardEntry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _everIssued = new(StringComparer.Ordinal); // never cleared: names are never reused
    private readonly TestSqlSettings _settings;
    private readonly ITestSqlBatchRunner _runner;
    private readonly string _token;
    private readonly Func<string> _nameSource;
    private readonly TimeSpan _retryDelay;

    internal TestSqlServer(TestSqlSettings settings, ITestSqlBatchRunner runner, string runToken,
        Func<string>? nameSource = null, TimeSpan? retryDelay = null)
    {
        if (string.IsNullOrEmpty(runToken) || runToken.Length > 64)
        {
            throw new ArgumentException("The run token must be 1-64 characters.", nameof(runToken));
        }

        _settings = settings;
        _runner = runner;
        _token = runToken;
        _nameSource = nameSource ?? (() => $"AiReapTests_{Guid.NewGuid():N}");
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public static TestSqlServer Default => LazyDefault.Value;

    // Settings are resolved before the runner is ever used, so missing or unsafe configuration fails
    // before any connection is attempted.
    internal static TestSqlServer FromEnvironment(Func<string, string?> getEnvironmentVariable, ITestSqlBatchRunner runner) =>
        new(TestSqlSettings.Resolve(getEnvironmentVariable), runner, Guid.NewGuid().ToString("N"));

    public static bool IsAllowedDatabaseName(string? name) =>
        name is not null && Regex.IsMatch(name, @"^AiReapTests_[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

    // Databases this run could not establish as safe to remove. Never dropped automatically.
    public IReadOnlyList<(string Name, string Reason)> Orphans
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values
                    .Where(e => e.State == TestDatabaseState.Orphaned)
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .Select(e => (e.Name, _settings.Redact(Describe(e))))
                    .ToList();
            }
        }
    }

    // ---------------- batches ----------------
    // Every value is a typed parameter. The only identifier that varies is the test database name, and it
    // only ever reaches SQL text as QUOTENAME(@name) inside sp_executesql. Every comparison is written in
    // positive form (IF NOT EXISTS (SELECT 1 WHERE <expected>)) so that a NULL fails the check. Every
    // THROW message starts with "AIREAP-GUARD <nonce> <code> " and every success row returns @nonce.

    private static string Refuse(int code, string text) =>
        $"BEGIN SET @m = CONCAT(@tag, N'{code} {text}'); THROW {code}, @m, 1; END;";

    private static readonly string Header = string.Join("\n",
        "SET NOCOUNT ON;",
        "SET XACT_ABORT ON;",
        "SET IMPLICIT_TRANSACTIONS OFF;",
        "",
        "DECLARE @tag nvarchar(64)  = CONCAT(N'AIREAP-GUARD ', @nonce, N' ');",
        "DECLARE @m   nvarchar(2048);",
        "DECLARE @db  nvarchar(258) = QUOTENAME(@name);",
        "DECLARE @sql nvarchar(max);",
        "",
        "IF @name IS NULL OR @expected IS NULL OR @marker IS NULL OR @token IS NULL OR @nonce IS NULL",
        "    " + Refuse(51014, "A guard parameter is NULL."),
        "IF NOT EXISTS (SELECT 1 WHERE @@TRANCOUNT = 0)",
        "    " + Refuse(51010, "An open transaction exists. Refusing to run guard SQL."),
        "");

    private static readonly string OnMaster = string.Join("\n",
        "IF NOT EXISTS (SELECT 1 WHERE DB_NAME() = N'master')",
        "    " + Refuse(51000, "Guard batch is not running in master."),
        "");

    private static readonly string Identity = string.Join("\n",
        "IF NOT EXISTS (SELECT 1 WHERE CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) COLLATE Latin1_General_100_CI_AS = @expected)",
        "    " + Refuse(51001, "Server identity does not match AIREAP_TEST_SQL_EXPECTED_SERVER."),
        "");

    private static string Permissions(bool create) => string.Join("\n",
        "IF NOT EXISTS (SELECT 1 WHERE HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE') = 1" +
        (create ? " AND HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE') = 1)" : ")"),
        "    " + Refuse(51013, "The login lacks a permission the guard relies on."),
        "");

    private static readonly string MarkerAndName = string.Join("\n",
        "IF NOT EXISTS (SELECT 1 WHERE DB_ID(@marker) IS NOT NULL)",
        "    " + Refuse(51002, "Disposable-instance marker database is missing."),
        "IF NOT EXISTS (SELECT 1 WHERE @db IS NOT NULL AND DATALENGTH(@name) = 88",
        "                   AND @name COLLATE Latin1_General_BIN2 LIKE N'AiReapTests[_]' + REPLICATE(N'[0-9a-f]', 32))",
        "    " + Refuse(51003, "Database name is not an allowed test database name."),
        "");

    // On master. Same-batch cleanup ([D]) only when this batch's own CREATE DATABASE completed (@created = 1).
    internal static readonly string CreateBatch = Header + OnMaster + Identity + Permissions(create: true) + MarkerAndName + string.Join("\n",
        "IF EXISTS (SELECT 1 WHERE DB_ID(@name) IS NOT NULL)",
        "    " + Refuse(51004, "Database already exists. Refusing to adopt it."),
        "",
        "DECLARE @created bit = 0;",
        "DECLARE @err nvarchar(2048);",
        "BEGIN TRY",
        "    IF NOT EXISTS (SELECT 1 WHERE @@TRANCOUNT = 0)",
        "        " + Refuse(51010, "An open transaction exists. Refusing to run guard DDL."),
        "    SET @sql = N'CREATE DATABASE ' + @db + N';';",
        "    EXEC sys.sp_executesql @sql;",
        "    SET @created = 1;",
        "    SET @sql = N'USE ' + @db + N'; EXEC sys.sp_addextendedproperty @name = N''AiReap.TestRunToken'', @value = @t;';",
        "    EXEC sys.sp_executesql @sql, N'@t nvarchar(64)', @t = @token;",
        "END TRY",
        "BEGIN CATCH",
        "    SET @err = CONCAT(N'Error ', ERROR_NUMBER(), N': ', ERROR_MESSAGE());",
        "    IF @created = 0",
        "    BEGIN",
        "        IF DB_ID(@name) IS NOT NULL",
        "            BEGIN SET @m = CONCAT(@tag, N'51009 CREATE reported failure but the database exists. Not touching it. ', @err); THROW 51009, @m, 1; END;",
        "        SET @m = CONCAT(@tag, N'51008 CREATE DATABASE failed. ', @err); THROW 51008, @m, 1;",
        "    END;",
        "    BEGIN TRY",
        "        SET @sql = N'DROP DATABASE ' + @db + N';';",
        "        EXEC sys.sp_executesql @sql;",
        "    END TRY",
        "    BEGIN CATCH",
        "        SET @m = CONCAT(@tag, N'51007 Token step failed and same-batch cleanup failed. Database left in place. ', @err, N' | cleanup: ', ERROR_MESSAGE()); THROW 51007, @m, 1;",
        "    END CATCH;",
        "    SET @m = CONCAT(@tag, N'51006 Token step failed. The database this batch created was dropped. ', @err); THROW 51006, @m, 1;",
        "END CATCH;",
        "",
        "SELECT N'Created' AS Outcome, @nonce AS Nonce;");

    // On master. Absent is reported, never treated as Dropped. Exactly one attempt; never retried.
    internal static readonly string DropBatch = Header + OnMaster + Identity + Permissions(create: false) + MarkerAndName + string.Join("\n",
        "DECLARE @found nvarchar(64);",
        "IF NOT EXISTS (SELECT 1 WHERE DB_ID(@name) IS NOT NULL)",
        "BEGIN",
        "    SELECT N'Absent' AS Outcome, @nonce AS Nonce;",
        "    RETURN;",
        "END;",
        "",
        "SET @sql = N'USE ' + @db + N'; SELECT @f = CAST(ep.value AS nvarchar(64)) FROM sys.extended_properties AS ep '",
        "         + N'WHERE ep.class = 0 AND ep.major_id = 0 AND ep.minor_id = 0 AND ep.name = N''AiReap.TestRunToken'';';",
        "EXEC sys.sp_executesql @sql, N'@f nvarchar(64) OUTPUT', @f = @found OUTPUT;",
        "IF NOT EXISTS (SELECT 1 WHERE @found COLLATE Latin1_General_BIN2 = @token)",
        "    " + Refuse(51005, "Database run token is missing or does not match this run. Refusing to drop."),
        "",
        "IF NOT EXISTS (SELECT 1 WHERE @@TRANCOUNT = 0)",
        "    " + Refuse(51010, "An open transaction exists. Refusing to run guard DDL."),
        "SET @sql = N'ALTER DATABASE ' + @db + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + @db + N';';",
        "EXEC sys.sp_executesql @sql;",
        "",
        "SELECT N'Dropped' AS Outcome, @nonce AS Nonce;");

    // On the test database's own connection, before that connection is handed out.
    internal static readonly string VerifyBatch = Header + Identity + Permissions(create: false) + MarkerAndName + string.Join("\n",
        "DECLARE @found nvarchar(64) =",
        "    (SELECT CAST(ep.value AS nvarchar(64)) FROM sys.extended_properties AS ep",
        "     WHERE ep.class = 0 AND ep.major_id = 0 AND ep.minor_id = 0 AND ep.name = N'AiReap.TestRunToken');",
        "IF NOT EXISTS (SELECT 1 WHERE DB_NAME() COLLATE Latin1_General_BIN2 = @name)",
        "    " + Refuse(51012, "Connection is not on the requested test database."),
        "IF NOT EXISTS (SELECT 1 WHERE @found COLLATE Latin1_General_BIN2 = @token)",
        "    " + Refuse(51005, "Database run token is missing or does not match this run."),
        "",
        "SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) AS ServerName, DB_NAME() AS DatabaseName,",
        "       @found AS RunToken, @nonce AS Nonce;");

    // ---------------- classification (pure) ----------------

    // A guard THROW is recognised only when it is the single error of class >= 11 (lower classes are
    // informational and can never be a guard THROW), its number is in this batch's own set, it has
    // Class 16 and State 1, and its message starts with this batch's exact nonce prefix and code.
    internal static int? MatchGuardError(BatchResult result, string nonce, IReadOnlyCollection<int> allowed)
    {
        if (result is not BatchResult.SqlFailed { RowsSeen: false } failed)
        {
            return null;
        }

        var qualifying = failed.Errors.Where(e => e.Class >= 11).ToList();
        if (qualifying.Count != 1)
        {
            return null;
        }

        var error = qualifying[0];
        if (!allowed.Contains(error.Number) || error.Class != 16 || error.State != 1)
        {
            return null;
        }

        var prefix = $"AIREAP-GUARD {nonce} {error.Number.ToString(CultureInfo.InvariantCulture)} ";
        return error.Message is not null && error.Message.StartsWith(prefix, StringComparison.Ordinal) ? error.Number : null;
    }

    internal static (CreateOutcome Outcome, string Detail) ClassifyCreate(BatchResult result, string nonce)
    {
        if (SingleRow(result, "Outcome", "Nonce") is [string outcome, string rowNonce]
            && outcome == "Created" && rowNonce == nonce)
        {
            return (CreateOutcome.Created, "created");
        }

        switch (MatchGuardError(result, nonce, CreateCodes))
        {
            case int code when NotCreatedCodes.Contains(code):
                return (CreateOutcome.NotCreated, Describe(result));
            case 51006:
                return (CreateOutcome.CleanedUpBySameBatch, Describe(result));
            default:
                return (CreateOutcome.Unclear, Describe(result)); // includes 51007, 51008, 51009
        }
    }

    internal static (TestDatabaseState State, string Detail) ClassifyDrop(BatchResult result, string nonce)
    {
        if (SingleRow(result, "Outcome", "Nonce") is [string outcome, string rowNonce] && rowNonce == nonce)
        {
            if (outcome == "Dropped")
            {
                return (TestDatabaseState.Dropped, "dropped");
            }

            if (outcome == "Absent")
            {
                // DB_ID can return NULL for a database that exists but is not visible to this login.
                return (TestDatabaseState.Orphaned,
                    "DB_ID returned NULL at DROP for a database this run created. It may still exist, so it is reported, not assumed dropped.");
            }
        }

        return (TestDatabaseState.Orphaned, "DROP outcome was refused or unclear: " + Describe(result));
    }

    internal (VerifyOutcome Outcome, string Detail) ClassifyVerify(BatchResult result, string nonce, string name)
    {
        if (SingleRow(result, "ServerName", "DatabaseName", "RunToken", "Nonce") is [var server, var database, var token, string rowNonce]
            && rowNonce == nonce)
        {
            var matches = server is string s && string.Equals(s, _settings.ExpectedServer, StringComparison.OrdinalIgnoreCase)
                && database is string d && d == name
                && token is string t && t == _token;
            return matches
                ? (VerifyOutcome.Verified, "verified")
                : (VerifyOutcome.Mismatch, "the verify row named a different server, database or run token");
        }

        return MatchGuardError(result, nonce, VerifyCodes) is int code && VerifyMismatchCodes.Contains(code)
            ? (VerifyOutcome.Mismatch, Describe(result))
            : (VerifyOutcome.Failed, Describe(result));
    }

    private static object?[]? SingleRow(BatchResult result, params string[] columns)
    {
        if (result is not BatchResult.Completed { ResultSets: [{ Rows: [var row] }] } || row.Columns.Count != columns.Length)
        {
            return null;
        }

        var values = new object?[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            if (!string.Equals(row.Columns[i].Name, columns[i], StringComparison.Ordinal))
            {
                return null;
            }

            values[i] = row.Columns[i].Value;
        }

        return values;
    }

    // ---------------- create ----------------

    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = _nameSource();
        if (!IsAllowedDatabaseName(name))
        {
            throw Guard("The generated database name is not an allowed test database name; nothing was sent.");
        }

        lock (_gate)
        {
            if (!_everIssued.Add(name))
            {
                throw Guard($"Refusing to reuse database name '{name}'; nothing was sent.");
            }
        }

        var nonce = NewNonce();
        var parameters = Parameters(name, nonce);
        var open = await _runner.OpenAsync(_settings.GuardConnectionString("master")).ConfigureAwait(false);
        if (open is not OpenResult.Opened opened)
        {
            // The batch was never sent: nothing to register. The name stays tombstoned.
            throw Guard($"Could not open a guard connection to master on {_settings.DataSource}; '{name}' was not created. {Describe(open)}");
        }

        var entry = new GuardEntry(name);
        lock (_gate)
        {
            _entries.Add(name, entry);
        }

        (CreateOutcome Outcome, string Detail) verdict;
        try
        {
            try
            {
                verdict = ClassifyCreate(
                    await _runner.ExecuteAsync(opened.Connection, CreateBatch, parameters).ConfigureAwait(false), nonce);
            }
            finally
            {
                CloseQuietly(opened.Connection);
            }

            RecordCreate(entry, verdict.Outcome, verdict.Detail);
        }
        catch (Exception ex)
        {
            verdict = (CreateOutcome.Unclear, "guard fault: " + Describe(ex));
            ForceOrphanIfStill(entry, TestDatabaseState.Creating, verdict.Detail);
        }

        return verdict.Outcome switch
        {
            CreateOutcome.Created => new TestDatabase(this, entry),
            CreateOutcome.NotCreated => throw Guard($"Test database '{name}' was not created on {_settings.DataSource}: {verdict.Detail}"),
            CreateOutcome.CleanedUpBySameBatch => throw Guard($"Test database '{name}' was created but its run token could not be set; the same batch dropped it. {verdict.Detail}"),
            _ => throw Guard(OrphanMessage(entry)),
        };
    }

    // The single transition for a CREATE outcome, recorded before any exception is thrown.
    private void RecordCreate(GuardEntry entry, CreateOutcome outcome, string detail)
    {
        lock (_gate)
        {
            switch (outcome)
            {
                case CreateOutcome.Created:
                    Transition(entry, TestDatabaseState.Creating, TestDatabaseState.Ready);
                    break;
                case CreateOutcome.NotCreated:
                    Transition(entry, TestDatabaseState.Creating, TestDatabaseState.Dropped, "not created: " + detail);
                    _entries.Remove(entry.Name); // unknown from now on: can never be leased, opened or dropped
                    break;
                case CreateOutcome.CleanedUpBySameBatch:
                    Transition(entry, TestDatabaseState.Creating, TestDatabaseState.Dropped, "dropped by the CREATE batch: " + detail);
                    break;
                default:
                    Transition(entry, TestDatabaseState.Creating, TestDatabaseState.Orphaned, "CREATE outcome unclear: " + detail);
                    break;
            }
        }
    }

    // ---------------- verified connections ----------------

    // The reservation is taken under the lock before anything is opened, and changes hands exactly once:
    // to the returned VerifiedConnection, or back to the registry on failure - but only when every
    // connection this call opened is known to be disposed. Otherwise it is kept, so the owner's DROP can
    // only end Orphaned. No CancellationToken: a cancellation can never strand the registry midway.
    public async Task<VerifiedConnection> OpenVerifiedAsync(string name)
    {
        GuardEntry entry;
        lock (_gate)
        {
            entry = RequireReady(name, "open a verified connection to");
            entry.Reservations++;
        }

        var sessionMayBeLive = false;
        SqlConnection? connection = null;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                lock (_gate)
                {
                    RequireReady(entry, "open a verified connection to");
                }

                var nonce = NewNonce(); // fresh for every attempt
                var parameters = Parameters(entry.Name, nonce);
                var open = await _runner.OpenAsync(_settings.GuardConnectionString(entry.Name)).ConfigureAwait(false);
                if (open is OpenResult.Failed failed)
                {
                    if (failed.DisposeFailed)
                    {
                        sessionMayBeLive = true;
                    }

                    // Only "cannot open database" right after CREATE is worth another try.
                    if (SingleQualifying(failed.Errors) is { Number: LoginDatabaseUnavailable } && attempt < MaxOpenAttempts)
                    {
                        await Task.Delay(_retryDelay).ConfigureAwait(false);
                        continue;
                    }

                    throw Guard($"Could not open a verified connection to '{entry.Name}' on {_settings.DataSource} " +
                                $"(attempt {attempt} of {MaxOpenAttempts}). {Describe(open)}");
                }

                connection = ((OpenResult.Opened)open).Connection;
                var (verdict, detail) = ClassifyVerify(
                    await _runner.ExecuteAsync(connection, VerifyBatch, parameters).ConfigureAwait(false), nonce, entry.Name);

                if (verdict == VerifyOutcome.Verified)
                {
                    var verified = new VerifiedConnection(this, entry, connection); // cannot throw
                    connection = null;
                    return verified;
                }

                if (verdict == VerifyOutcome.Mismatch)
                {
                    // The target is no longer the database this run created: stop handing it out, never drop it.
                    lock (_gate)
                    {
                        if (entry.State == TestDatabaseState.Ready)
                        {
                            Transition(entry, TestDatabaseState.Ready, TestDatabaseState.Orphaned, "a verified open found a mismatch: " + detail);
                        }
                    }

                    throw Guard($"Verified open of '{entry.Name}' found a different target; it is now Orphaned. {detail}");
                }

                throw Guard($"Verified open of '{entry.Name}' on {_settings.DataSource} failed. {detail}");
            }
        }
        catch (Exception ex)
        {
            Exception? disposeFailure = null;
            if (connection is not null)
            {
                try
                {
                    _runner.Close(connection);
                }
                catch (Exception closeFailure)
                {
                    disposeFailure = closeFailure;
                    sessionMayBeLive = true;
                }
            }

            if (sessionMayBeLive)
            {
                lock (_gate)
                {
                    entry.Notes.Add("a connection from a failed verified open could not be confirmed closed, so its reservation is kept");
                }
            }
            else
            {
                ReleaseReservation(entry);
            }

            if (disposeFailure is not null)
            {
                throw new AggregateException(ex, disposeFailure);
            }

            throw;
        }
    }

    // Called once per VerifiedConnection (its own Interlocked flag). The reservation is released only
    // after the connection's Dispose returned; if Dispose throws, the reservation is kept.
    internal void CloseVerified(GuardEntry entry, SqlConnection connection)
    {
        try
        {
            _runner.Close(connection);
        }
        catch
        {
            lock (_gate)
            {
                entry.Notes.Add("a verified connection could not be confirmed closed, so its reservation is kept");
            }

            throw;
        }

        ReleaseReservation(entry);
    }

    internal bool IsReady(GuardEntry entry)
    {
        lock (_gate)
        {
            return entry.State == TestDatabaseState.Ready;
        }
    }

    // ---------------- leases and hand-out strings ----------------

    // A non-owning reference to a Ready database. Only the returned handle can release it.
    public TestDatabaseLease AcquireLease(string name)
    {
        GuardEntry entry;
        lock (_gate)
        {
            entry = RequireReady(name, "lease");
            entry.Leases++;
        }

        return new TestDatabaseLease(this, entry);
    }

    // Checked on every call: Orphaned or Dropped databases never get a new string. A string already handed
    // out cannot be revoked (see TestHost.ConnectionString).
    public string ConnectionStringFor(string name)
    {
        lock (_gate)
        {
            RequireReady(name, "hand out a connection string for");
        }

        return _settings.HandOutConnectionString(name);
    }

    internal void ReleaseLease(GuardEntry entry)
    {
        lock (_gate)
        {
            if (entry.State is TestDatabaseState.Dropping or TestDatabaseState.Dropped || entry.Leases <= 0)
            {
                throw new InvalidOperationException($"Guard bug: lease release for '{entry.Name}' in state {entry.State} with {entry.Leases} lease(s).");
            }

            entry.Leases--;
        }
    }

    private void ReleaseReservation(GuardEntry entry)
    {
        lock (_gate)
        {
            if (entry.State is TestDatabaseState.Dropping or TestDatabaseState.Dropped || entry.Reservations <= 0)
            {
                throw new InvalidOperationException($"Guard bug: reservation release for '{entry.Name}' in state {entry.State} with {entry.Reservations} reservation(s).");
            }

            entry.Reservations--;
        }
    }

    // ---------------- drop ----------------

    // Only reached through TestDatabase, whose first dispose stores this task. Only a caller that moves
    // Ready -> Dropping with no leases and no reservations sends the single DROP batch. Never throws.
    internal async Task<DropOutcome> DisposeOwnedAsync(GuardEntry entry)
    {
        var nonce = NewNonce();
        var parameters = Parameters(entry.Name, nonce);
        lock (_gate)
        {
            if (entry.State != TestDatabaseState.Ready)
            {
                return Outcome(entry);
            }

            if (entry.Leases != 0 || entry.Reservations != 0)
            {
                Transition(entry, TestDatabaseState.Ready, TestDatabaseState.Orphaned,
                    $"the owner was disposed while {entry.Leases} lease(s) and {entry.Reservations} verified-connection reservation(s) were active");
                return Outcome(entry);
            }

            Transition(entry, TestDatabaseState.Ready, TestDatabaseState.Dropping);
        }

        try
        {
            // Only this process's pools; ROLLBACK IMMEDIATE is what actually ends other sessions.
            _runner.ClearPool(_settings.HandOutConnectionString(entry.Name));

            var open = await _runner.OpenAsync(_settings.GuardConnectionString("master")).ConfigureAwait(false);
            if (open is not OpenResult.Opened opened)
            {
                return Finish(entry, TestDatabaseState.Orphaned, "DROP was not sent: could not open master. " + Describe(open));
            }

            BatchResult result;
            try
            {
                result = await _runner.ExecuteAsync(opened.Connection, DropBatch, parameters).ConfigureAwait(false);
            }
            finally
            {
                CloseQuietly(opened.Connection);
            }

            var (state, detail) = ClassifyDrop(result, nonce);
            return Finish(entry, state, detail);
        }
        catch (Exception ex)
        {
            ForceOrphanIfStill(entry, TestDatabaseState.Dropping, "guard fault during DROP: " + Describe(ex));
            return Outcome(entry);
        }
    }

    private DropOutcome Finish(GuardEntry entry, TestDatabaseState state, string detail)
    {
        lock (_gate)
        {
            Transition(entry, TestDatabaseState.Dropping, state, detail);
            return Outcome(entry);
        }
    }

    private DropOutcome Outcome(GuardEntry entry)
    {
        lock (_gate)
        {
            return entry.State switch
            {
                TestDatabaseState.Dropped => new DropOutcome(TestDatabaseState.Dropped, $"Test database '{entry.Name}' was dropped."),
                TestDatabaseState.Orphaned => new DropOutcome(TestDatabaseState.Orphaned, _settings.Redact(OrphanMessage(entry))),
                _ => new DropOutcome(entry.State, $"Test database '{entry.Name}' is {entry.State}; nothing was dropped."),
            };
        }
    }

    // ---------------- registry helpers (callers hold _gate unless noted) ----------------

    private GuardEntry RequireReady(string name, string action)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            throw Guard($"Refusing to {action} '{name}': it is not a database this test run created. Leftover and unknown databases are never adopted.");
        }

        RequireReady(entry, action);
        return entry;
    }

    private void RequireReady(GuardEntry entry, string action)
    {
        if (entry.State != TestDatabaseState.Ready)
        {
            throw Guard($"Refusing to {action} '{entry.Name}': it is {entry.State}.");
        }
    }

    // There are no transitions out of Dropped or Orphaned. A wrong transition throws and changes nothing.
    private static void Transition(GuardEntry entry, TestDatabaseState from, TestDatabaseState to, string? reason = null)
    {
        if (entry.State != from)
        {
            throw new InvalidOperationException($"Guard bug: '{entry.Name}' cannot move {from} -> {to}; it is {entry.State}.");
        }

        entry.State = to;
        if (reason is not null)
        {
            entry.Reason = reason;
        }
    }

    // Takes the lock itself. Used when guard code itself failed while an entry was Creating or Dropping.
    private void ForceOrphanIfStill(GuardEntry entry, TestDatabaseState expected, string reason)
    {
        lock (_gate)
        {
            if (entry.State == expected)
            {
                Transition(entry, expected, TestDatabaseState.Orphaned, reason);
            }
        }
    }

    // Master sessions hold no reservation and pin no test database, so a failure to dispose one cannot make
    // a DROP unsafe; reporting it would only hide the batch outcome that was already recorded.
    private void CloseQuietly(SqlConnection connection)
    {
        try
        {
            _runner.Close(connection);
        }
        catch
        {
            // deliberately ignored, see above
        }
    }

    // Test-only view of an entry: (null, 0, 0) for names the registry does not hold.
    internal (TestDatabaseState? State, int Leases, int Reservations) Inspect(string name)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(name, out var e) ? (e.State, e.Leases, e.Reservations) : (null, 0, 0);
        }
    }

    private IReadOnlyList<GuardParameter> Parameters(string name, string nonce)
    {
        GuardParameter[] parameters =
        [
            new("@name", SqlDbType.NVarChar, 128, name),
            new("@expected", SqlDbType.NVarChar, 256, _settings.ExpectedServer),
            new("@marker", SqlDbType.NVarChar, 128, TestSqlSettings.MarkerDatabase),
            new("@token", SqlDbType.NVarChar, 64, _token),
            new("@nonce", SqlDbType.NVarChar, 36, nonce),
        ];

        foreach (var p in parameters)
        {
            if (string.IsNullOrEmpty(p.Value) || p.Value.Length > p.Size)
            {
                throw Guard($"Guard parameter {p.Name} is empty or too long; nothing was sent.");
            }
        }

        return parameters;
    }

    private static string NewNonce() => Guid.NewGuid().ToString("D");

    private static GuardSqlError? SingleQualifying(IReadOnlyList<GuardSqlError> errors) =>
        errors.Where(e => e.Class >= 11).ToList() is [var only] ? only : null;

    private string OrphanMessage(GuardEntry entry) =>
        $"Test database '{entry.Name}' on {_settings.DataSource} is Orphaned and was not dropped: {Describe(entry)} " +
        "Review it manually; the guard never cleans it up or reuses it.";

    private static string Describe(GuardEntry entry) =>
        entry.Notes.Count == 0 ? entry.Reason ?? "" : $"{entry.Reason} ({string.Join("; ", entry.Notes)})";

    private static string Describe(BatchResult result) => result switch
    {
        BatchResult.Completed c => $"completed with {c.ResultSets.Count} result set(s) and {c.ResultSets.Sum(s => s.Rows.Count)} row(s)",
        BatchResult.SqlFailed f => (f.RowsSeen ? "rows arrived before the failure; " : "") +
            string.Join(" | ", f.Errors.Select(e => $"SQL error {e.Number} (class {e.Class}, state {e.State}): {e.Message}")),
        BatchResult.Faulted f => "fault: " + f.Description,
        _ => result.GetType().Name,
    };

    private static string Describe(OpenResult result) => result switch
    {
        OpenResult.Failed f => (f.Errors.Count == 0 ? f.Description : string.Join(" | ", f.Errors.Select(e => $"SQL error {e.Number}: {e.Message}")))
            + (f.DisposeFailed ? " (the failed connection could not be disposed)" : ""),
        _ => result.GetType().Name,
    };

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private TestSqlGuardException Guard(string message) => new(_settings.Redact(message));
}

// A database this process created and owns. Its first dispose decides the one terminal outcome; every
// later or concurrent dispose awaits that same task and never sends another batch.
public sealed class TestDatabase : IDisposable, IAsyncDisposable
{
    private readonly TestSqlServer _server;
    private readonly GuardEntry _entry;
    private Task<DropOutcome>? _outcome;

    internal TestDatabase(TestSqlServer server, GuardEntry entry)
    {
        _server = server;
        _entry = entry;
    }

    public string Name => _entry.Name;

    public Task<DropOutcome> DisposeOutcomeAsync()
    {
        var completion = new TaskCompletionSource<DropOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Interlocked.CompareExchange(ref _outcome, completion.Task, null);
        if (existing is not null)
        {
            return existing;
        }

        _ = CompleteAsync(completion);
        return completion.Task;
    }

    private async Task CompleteAsync(TaskCompletionSource<DropOutcome> completion)
    {
        try
        {
            completion.SetResult(await _server.DisposeOwnedAsync(_entry).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    // Throws unless the database was dropped, so an Orphaned database fails the test visibly.
    public async ValueTask DisposeAsync()
    {
        var outcome = await DisposeOutcomeAsync().ConfigureAwait(false);
        if (outcome.State != TestDatabaseState.Dropped)
        {
            throw new TestSqlGuardException(outcome.Detail);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

// A non-owning reference used by hosts that share another host's database. Disposing it releases this
// one lease, at most once, and never drops anything.
public sealed class TestDatabaseLease : IDisposable
{
    private readonly TestSqlServer _server;
    private readonly GuardEntry _entry;
    private int _released;

    internal TestDatabaseLease(TestSqlServer server, GuardEntry entry)
    {
        _server = server;
        _entry = entry;
    }

    public string Name => _entry.Name;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _server.ReleaseLease(_entry);
        }
    }
}

// An open connection whose server, database and run token were verified on this same session. The raw
// SqlConnection never leaves this class: commands go through CreateCommand, EF through
// CreateMigrationOptions. Both refuse once the connection has left the Open state (it could otherwise be
// reopened unverified) or the database is no longer Ready.
public sealed class VerifiedConnection : IDisposable, IAsyncDisposable
{
    private readonly TestSqlServer _server;
    private readonly GuardEntry _entry;
    private readonly SqlConnection _connection;
    private int _disposed;
    private volatile bool _stillVerified = true;

    internal VerifiedConnection(TestSqlServer server, GuardEntry entry, SqlConnection connection)
    {
        _server = server;
        _entry = entry;
        _connection = connection;
        _connection.StateChange += OnStateChange;
    }

    public string DatabaseName => _entry.Name;

    public VerifiedCommand CreateCommand(string sql)
    {
        EnsureUsable();
        return new VerifiedCommand(this, new SqlCommand(sql, _connection));
    }

    // EF uses this already-open, verified connection and never owns it; the tripwire turns any attempt by
    // EF to create or open a connection of its own into an exception. No retry strategy (it would reopen).
    public DbContextOptions<AiReapDbContext> CreateMigrationOptions()
    {
        EnsureUsable();
        return new DbContextOptionsBuilder<AiReapDbContext>()
            .UseSqlServer(_connection, contextOwnsConnection: false)
            .AddInterceptors(new EfConnectionTripwire())
            .Options;
    }

    internal void EnsureUsable()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_stillVerified || _connection.State != ConnectionState.Open || !_server.IsReady(_entry))
        {
            throw new TestSqlGuardException($"The verified connection to '{_entry.Name}' is no longer open and verified.");
        }
    }

    private void OnStateChange(object? sender, StateChangeEventArgs e)
    {
        if (e.CurrentState != ConnectionState.Open)
        {
            _stillVerified = false; // permanent: a reopened session was never verified
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stillVerified = false;
        _connection.StateChange -= OnStateChange;
        _server.CloseVerified(_entry, _connection);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

// A command on a verified connection. Re-checks the connection before every execution and does not
// expose it.
public sealed class VerifiedCommand : IDisposable, IAsyncDisposable
{
    private readonly VerifiedConnection _owner;
    private readonly SqlCommand _command;

    internal VerifiedCommand(VerifiedConnection owner, SqlCommand command)
    {
        _owner = owner;
        _command = command;
    }

    public SqlParameterCollection Parameters => _command.Parameters;

    public async Task<object?> ExecuteScalarAsync()
    {
        _owner.EnsureUsable();
        return await _command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    public async Task<int> ExecuteNonQueryAsync()
    {
        _owner.EnsureUsable();
        return await _command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public void Dispose() => _command.Dispose();

    public ValueTask DisposeAsync() => _command.DisposeAsync();
}

// Throws if EF tries to create or open a connection itself: building its own master connection to create a
// database, or silently reopening a verified connection that was closed underneath it.
// TestSqlGuardException derives from Exception, not DbException, so EF's existence probes cannot swallow it.
internal sealed class EfConnectionTripwire : DbConnectionInterceptor
{
    public override InterceptionResult<DbConnection> ConnectionCreating(
        ConnectionCreatingEventData eventData, InterceptionResult<DbConnection> result) =>
        throw Refuse("create");

    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
        throw Refuse("open");

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        throw Refuse("open");

    private static TestSqlGuardException Refuse(string what) =>
        new($"EF tried to {what} a database connection itself; test migrations may only use a verified, already-open connection.");
}
