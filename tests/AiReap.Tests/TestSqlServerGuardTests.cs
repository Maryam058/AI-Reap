using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace AiReap.Tests;

// Offline only. Nothing here reads the real environment, touches TestSqlServer.Default or the public
// TestHost constructor, or opens a socket: every SQL "result" is scripted by RecordingRunner, and every
// SqlConnection it hands out targets a local named pipe that does not exist and is never opened.
// These tests prove the guard's C# decisions; they do not prove how SQL Server behaves.
public class TestSqlServerGuardTests
{
    internal const string OfflinePipe = @"np:\\.\pipe\aireap-guard-offline-none";
    internal const string FakePassword = "fake-guard-test-pw-7Q";
    internal const string Expected = "DISPOSABLE-SQL";
    internal const string RunToken = "0123456789abcdef0123456789abcdef";

    internal static readonly string ValidConnection = new SqlConnectionStringBuilder
    {
        DataSource = OfflinePipe,
        UserID = "guard-test",
        Password = FakePassword,
        TrustServerCertificate = true,
        ConnectTimeout = 1,
    }.ConnectionString;

    internal static Func<string, string?> Env(params (string Key, string? Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    internal static TestSqlSettings Settings() =>
        TestSqlSettings.Resolve(Env((TestSqlSettings.ConnectionVariable, ValidConnection), (TestSqlSettings.ExpectedServerVariable, Expected)));

    internal static (TestSqlServer Server, RecordingRunner Runner) NewServer(Func<string>? names = null)
    {
        var runner = new RecordingRunner();
        return (new TestSqlServer(Settings(), runner, RunToken, names, TimeSpan.Zero), runner);
    }

    internal static GuardSqlError GuardError(string nonce, int code, byte cls = 16, byte state = 1) =>
        new(code, cls, state, $"AIREAP-GUARD {nonce} {code} scripted guard refusal");

    internal static BatchResult Fail(params GuardSqlError[] errors) => new BatchResult.SqlFailed(errors, RowsSeen: false);

    internal static BatchResult Row(params (string Name, object? Value)[] columns) =>
        new BatchResult.Completed([new GuardResultSet([new GuardRow(columns)])]);

    internal static void AssertGuardInChain(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is TestSqlGuardException) return;
            if (e is AggregateException agg && agg.InnerExceptions.Any(i => i is TestSqlGuardException)) return;
        }

        Assert.Fail($"Expected a TestSqlGuardException, got {ex.GetType().Name}: {ex.Message}");
    }

    // ---------------- settings ----------------

    [Theory]
    [InlineData(null, Expected)]
    [InlineData("", Expected)]
    [InlineData("   ", Expected)]
    [InlineData("valid", null)]
    [InlineData("valid", "")]
    [InlineData("valid", "  ")]
    public void Missing_or_blank_settings_fail_closed(string? connection, string? expected)
    {
        var ex = Assert.Throws<TestSqlGuardException>(() => TestSqlSettings.Resolve(Env(
            (TestSqlSettings.ConnectionVariable, connection == "valid" ? ValidConnection : connection),
            (TestSqlSettings.ExpectedServerVariable, expected))));

        Assert.Contains(TestSqlSettings.ConnectionVariable, ex.Message);
        Assert.Contains(TestSqlSettings.ExpectedServerVariable, ex.Message);
        Assert.Contains("no fallback", ex.Message);
    }

    [Fact]
    public void There_is_no_fallback_to_other_connection_configuration()
    {
        // The developer's API connection string, offered under every name the old code or the API reads.
        var env = Env(
            ("ConnectionStrings__Default", ValidConnection),
            ("ConnectionStrings:Default", ValidConnection),
            (TestSqlSettings.ExpectedServerVariable, Expected));

        Assert.Throws<TestSqlGuardException>(() => TestSqlSettings.Resolve(env));
    }

    [Fact]
    public void An_unparsable_connection_string_fails_without_echoing_it()
    {
        var raw = $"Server=x;Pwd={FakePassword};NotAKeyword=1";
        var ex = Assert.Throws<TestSqlGuardException>(() => TestSqlSettings.Resolve(Env(
            (TestSqlSettings.ConnectionVariable, raw), (TestSqlSettings.ExpectedServerVariable, Expected))));

        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(FakePassword, ex.Message);
        Assert.DoesNotContain("NotAKeyword", ex.Message);
    }

    [Theory]
    [InlineData("Database", "AiReapDb")]
    [InlineData("Initial Catalog", "AiReapDb")]
    [InlineData("AttachDBFilename", @"C:\data\x.mdf")]
    [InlineData("Failover Partner", "other-server")]
    [InlineData("ApplicationIntent", "ReadOnly")]
    [InlineData("Data Source", "")]
    public void Unsafe_connection_options_are_rejected(string keyword, string value)
    {
        var builder = new SqlConnectionStringBuilder(ValidConnection) { [keyword] = value };
        var ex = Assert.Throws<TestSqlGuardException>(() => TestSqlSettings.Resolve(Env(
            (TestSqlSettings.ConnectionVariable, builder.ConnectionString), (TestSqlSettings.ExpectedServerVariable, Expected))));

        Assert.DoesNotContain(FakePassword, ex.Message);
    }

    [Fact]
    public void Valid_settings_build_master_guard_strings_and_an_unchanged_hand_out_string()
    {
        var settings = Settings();
        var name = "AiReapTests_" + new string('a', 32);

        var master = new SqlConnectionStringBuilder(settings.GuardConnectionString("master"));
        Assert.Equal("master", master.InitialCatalog);
        Assert.False(master.Pooling);
        Assert.Equal(0, master.ConnectRetryCount);
        Assert.False(master.Enlist);
        Assert.False(master.PersistSecurityInfo);
        Assert.Equal("AiReap.Tests.Guard", master.ApplicationName);

        var verified = new SqlConnectionStringBuilder(settings.GuardConnectionString(name));
        Assert.Equal(name, verified.InitialCatalog);
        Assert.False(verified.Pooling);

        var handOut = new SqlConnectionStringBuilder(settings.HandOutConnectionString(name));
        Assert.Equal(name, handOut.InitialCatalog);
        Assert.True(handOut.Pooling); // the caller's setting (default) is kept
        Assert.Equal(OfflinePipe, handOut.DataSource);

        Assert.Equal(OfflinePipe, settings.DataSource);
        Assert.Equal(Expected, settings.ExpectedServer);
        Assert.DoesNotContain(FakePassword, settings.ToString());
    }

    // ---------------- names ----------------

    [Theory]
    [InlineData("AiReapTests_0123456789abcdef0123456789abcdef", true)]
    [InlineData("AiReapTests_0123456789abcdef0123456789abcdef\n", false)] // the old pattern's `$` accepted this
    [InlineData("AiReapTests_0123456789abcdef0123456789abcdef ", false)]
    [InlineData("AiReapTests_0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("AiReapTests_0123456789abcdef0123456789abcde", false)]
    [InlineData("AiReapTests_0123456789abcdef0123456789abcdef0", false)]
    [InlineData("AiReapTests_０123456789abcdef0123456789abcdef", false)] // full-width digit
    [InlineData("AiReapTests_0123456789abcdef0123456789ab]; DROP DATABASE x--", false)]
    [InlineData("master", false)]
    [InlineData("AiReapDisposableTestInstance", false)]
    [InlineData("AiReapDb", false)]
    [InlineData("", false)]
    public void Only_guard_generated_names_are_allowed(string name, bool allowed)
    {
        Assert.Equal(allowed, TestSqlServer.IsAllowedDatabaseName(name));
    }

    // ---------------- batch text and parameters ----------------

    public static TheoryData<string> Batches => new() { "Create", "Drop", "Verify" };

    private static string BatchText(string kind) => kind switch
    {
        "Create" => TestSqlServer.CreateBatch,
        "Drop" => TestSqlServer.DropBatch,
        _ => TestSqlServer.VerifyBatch,
    };

    [Theory]
    [MemberData(nameof(Batches))]
    public void Every_batch_starts_with_its_transaction_and_null_guards(string kind)
    {
        var sql = BatchText(kind);

        Assert.StartsWith("SET NOCOUNT ON;\nSET XACT_ABORT ON;\nSET IMPLICIT_TRANSACTIONS OFF;\n", sql);
        var nullCheck = sql.IndexOf("IF @name IS NULL OR @expected IS NULL OR @marker IS NULL OR @token IS NULL OR @nonce IS NULL", StringComparison.Ordinal);
        var tranCount = sql.IndexOf("IF NOT EXISTS (SELECT 1 WHERE @@TRANCOUNT = 0)", StringComparison.Ordinal);
        Assert.True(nullCheck > 0 && tranCount > nullCheck);
        Assert.True(sql.IndexOf("THROW 51014", StringComparison.Ordinal) < sql.IndexOf("THROW 51010", StringComparison.Ordinal));

        // Positive-form comparisons only: a NULL can never make a check pass.
        Assert.DoesNotContain("<>", sql);
        Assert.DoesNotContain("!=", sql);

        // Ordered pre-checks, all before any DDL or result row.
        var order = new List<int> { nullCheck, tranCount };
        if (kind != "Verify") order.Add(sql.IndexOf("THROW 51000", StringComparison.Ordinal));
        order.Add(sql.IndexOf("THROW 51001", StringComparison.Ordinal));
        order.Add(sql.IndexOf("THROW 51013", StringComparison.Ordinal));
        order.Add(sql.IndexOf("THROW 51002", StringComparison.Ordinal));
        order.Add(sql.IndexOf("THROW 51003", StringComparison.Ordinal));
        Assert.All(order, i => Assert.True(i > 0));
        Assert.Equal(order.OrderBy(i => i), order);

        var firstEffect = new[] { "CREATE DATABASE", "ALTER DATABASE", "SELECT N'", "SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)) AS ServerName" }
            .Select(s => sql.IndexOf(s, StringComparison.Ordinal)).Where(i => i >= 0).Min();
        Assert.True(order.Max() < firstEffect);
    }

    [Theory]
    [MemberData(nameof(Batches))]
    public void Every_throw_carries_the_nonce_tag_and_its_own_code(string kind)
    {
        var sql = BatchText(kind);
        var throws = sql.Split('\n').Where(l => l.Contains("THROW", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(throws);
        foreach (var line in throws)
        {
            var m = Regex.Match(line, @"SET @m = CONCAT\(@tag, N'(?<a>\d{5}) .*\); THROW (?<b>\d{5}), @m, 1;");
            Assert.True(m.Success, $"THROW without a tagged message: {line.Trim()}");
            Assert.Equal(m.Groups["a"].Value, m.Groups["b"].Value);
        }

        Assert.Contains("DECLARE @tag nvarchar(64)  = CONCAT(N'AIREAP-GUARD ', @nonce, N' ');", sql);
        Assert.DoesNotMatch(@"THROW \d+, N'", sql);
        Assert.Contains("@nonce AS Nonce", sql);
    }

    [Fact]
    public void Create_and_drop_recheck_the_transaction_count_immediately_before_their_ddl()
    {
        var create = TestSqlServer.CreateBatch;
        var tryStart = create.IndexOf("BEGIN TRY", StringComparison.Ordinal);
        var innerCheck = create.IndexOf("IF NOT EXISTS (SELECT 1 WHERE @@TRANCOUNT = 0)", tryStart, StringComparison.Ordinal);
        Assert.True(tryStart > 0 && innerCheck > tryStart && innerCheck < create.IndexOf("CREATE DATABASE", StringComparison.Ordinal));
        Assert.Contains("    IF @created = 0", create);
        Assert.True(create.IndexOf("IF @created = 0", StringComparison.Ordinal) < create.IndexOf("N'DROP DATABASE '", StringComparison.Ordinal));
        Assert.Contains("HAS_PERMS_BY_NAME(NULL, NULL, N'CREATE ANY DATABASE') = 1", create);
        Assert.Contains("THROW 51004", create);

        var drop = TestSqlServer.DropBatch;
        var token = drop.IndexOf("THROW 51005", StringComparison.Ordinal);
        var lastTranCount = drop.LastIndexOf("IF NOT EXISTS (SELECT 1 WHERE @@TRANCOUNT = 0)", StringComparison.Ordinal);
        var alter = drop.IndexOf("ALTER DATABASE", StringComparison.Ordinal);
        Assert.True(token > 0 && lastTranCount > token && alter > lastTranCount);
        Assert.Contains("SELECT N'Absent' AS Outcome, @nonce AS Nonce;", drop);
        Assert.DoesNotContain("CREATE ANY DATABASE", drop);
    }

    [Fact]
    public void Identifiers_come_only_from_quotename_and_values_only_from_typed_parameters()
    {
        foreach (var sql in new[] { TestSqlServer.CreateBatch, TestSqlServer.DropBatch, TestSqlServer.VerifyBatch })
        {
            Assert.Contains("DECLARE @db  nvarchar(258) = QUOTENAME(@name);", sql);
            Assert.Contains("DATALENGTH(@name) = 88", sql);
            Assert.Contains("@name COLLATE Latin1_General_BIN2 LIKE N'AiReapTests[_]' + REPLICATE(N'[0-9a-f]', 32)", sql);
            Assert.DoesNotContain(TestSqlSettings.MarkerDatabase, sql);
            Assert.DoesNotContain(Expected, sql);
        }
    }

    [Fact]
    public async Task Each_batch_gets_five_typed_parameters_and_a_fresh_nonce()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        (await server.OpenVerifiedAsync(db.Name)).Dispose();
        (await server.OpenVerifiedAsync(db.Name)).Dispose();
        await db.DisposeAsync();

        Assert.Equal(["Create", "Verify", "Verify", "Drop"], runner.Batches.Select(b => b.Kind));
        Assert.Equal(["master", db.Name, db.Name, "master"], runner.Batches.Select(b => b.Catalog));
        foreach (var batch in runner.Batches)
        {
            Assert.Equal(
                [("@name", SqlDbType.NVarChar, 128), ("@expected", SqlDbType.NVarChar, 256), ("@marker", SqlDbType.NVarChar, 128),
                 ("@token", SqlDbType.NVarChar, 64), ("@nonce", SqlDbType.NVarChar, 36)],
                batch.Parameters.Select(p => (p.Name, p.Type, p.Size)));
            Assert.Equal(db.Name, batch.Value("@name"));
            Assert.Equal(Expected, batch.Value("@expected"));
            Assert.Equal(TestSqlSettings.MarkerDatabase, batch.Value("@marker"));
            Assert.Equal(RunToken, batch.Value("@token"));
            Assert.True(Guid.TryParseExact(batch.Nonce, "D", out _));
            Assert.DoesNotContain(db.Name, batch.Sql);
        }

        Assert.Equal(4, runner.Batches.Select(b => b.Nonce).Distinct().Count());
    }

    // ---------------- classifier ----------------

    [Fact]
    public void The_classifier_accepts_only_one_exact_guard_error()
    {
        var nonce = Guid.NewGuid().ToString("D");
        int[] allowed = [51006];

        Assert.Equal(51006, TestSqlServer.MatchGuardError(Fail(GuardError(nonce, 51006)), nonce, allowed));

        // Informational entries (class <= 10) are ignored; they can never be a guard THROW.
        Assert.Equal(51006, TestSqlServer.MatchGuardError(
            Fail(new GuardSqlError(0, 10, 1, "warning"), GuardError(nonce, 51006)), nonce, allowed));

        Assert.Null(TestSqlServer.MatchGuardError(Fail(GuardError(nonce, 51006), GuardError(nonce, 51006)), nonce, allowed)); // two errors
        Assert.Null(TestSqlServer.MatchGuardError(Fail(GuardError(nonce, 51006), new GuardSqlError(208, 16, 1, "x")), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Fail(GuardError(nonce, 51006, cls: 17)), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Fail(GuardError(nonce, 51006, cls: 20)), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Fail(GuardError(nonce, 51006, state: 2)), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Fail(GuardError(Guid.NewGuid().ToString("D"), 51006)), nonce, allowed)); // wrong nonce
        Assert.Null(TestSqlServer.MatchGuardError(Fail(new GuardSqlError(51006, 16, 1, $"AIREAP-GUARD  51006 empty nonce")), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Fail(new GuardSqlError(51006, 16, 1, null)), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Fail(new GuardSqlError(51006, 16, 1, $"AIREAP-GUARD {nonce} 51004 code mismatch")), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Fail(new GuardSqlError(51006, 16, 1, $"x AIREAP-GUARD {nonce} 51006 not a prefix")), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Fail(GuardError(nonce, 51004)), nonce, allowed)); // not this batch's code
        Assert.Null(TestSqlServer.MatchGuardError(Fail(new GuardSqlError(-2, 11, 0, "Execution Timeout Expired.")), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Fail(), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(new BatchResult.SqlFailed([GuardError(nonce, 51006)], RowsSeen: true), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(new BatchResult.Faulted("IOException: pipe broken"), nonce, allowed));
        Assert.Null(TestSqlServer.MatchGuardError(Row(("Outcome", "Created"), ("Nonce", nonce)), nonce, allowed));
    }

    // ---------------- CREATE outcomes ----------------

    [Fact]
    public async Task A_created_row_with_this_batchs_nonce_makes_the_database_ready()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();

        Assert.Equal((TestDatabaseState.Ready, 0, 0), server.Inspect(db.Name));
        Assert.True(TestSqlServer.IsAllowedDatabaseName(db.Name));
        Assert.Equal(["master"], runner.Opens);
        Assert.Equal(1, runner.Closes);
    }

    [Theory]
    [InlineData(51000)]
    [InlineData(51001)]
    [InlineData(51002)]
    [InlineData(51003)]
    [InlineData(51004)]
    [InlineData(51010)]
    [InlineData(51013)]
    [InlineData(51014)]
    public async Task Pre_check_refusals_create_nothing_and_leave_the_name_unknown(int code)
    {
        var (server, runner) = NewServer();
        runner.OnBatch = b => Fail(GuardError(b.Nonce, code));

        await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);

        var name = runner.Batches.Single().Value("@name");
        Assert.Equal((null, 0, 0), server.Inspect(name));
        Assert.Throws<TestSqlGuardException>(() => server.AcquireLease(name));
        Assert.Throws<TestSqlGuardException>(() => server.ConnectionStringFor(name));
        await Assert.ThrowsAsync<TestSqlGuardException>(() => server.OpenVerifiedAsync(name));
        Assert.Empty(server.Orphans);
        Assert.Equal(["master"], runner.Opens);
    }

    [Fact]
    public async Task Same_batch_cleanup_is_the_only_create_failure_recorded_as_dropped()
    {
        var (server, runner) = NewServer();
        runner.OnBatch = b => Fail(new GuardSqlError(5701, 0, 1, "Changed database context."), GuardError(b.Nonce, 51006));

        await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);

        var name = runner.Batches.Single().Value("@name");
        Assert.Equal((TestDatabaseState.Dropped, 0, 0), server.Inspect(name));
        Assert.Throws<TestSqlGuardException>(() => server.AcquireLease(name));
        Assert.Empty(server.Orphans);
    }

    public static TheoryData<string> UnclearCreateResults => new()
    {
        "51007", "51008", "51009", "51012-not-a-create-code", "wrong-nonce", "empty-nonce", "code-prefix-mismatch",
        "class-17", "class-20", "state-2", "two-errors", "timeout", "transport", "unexpected-exception",
        "rows-then-error", "created-wrong-nonce", "created-two-rows", "created-extra-column", "no-result-set",
        "unexpected-outcome", "runner-throws",
    };

    [Theory]
    [MemberData(nameof(UnclearCreateResults))]
    public async Task Every_unclear_create_outcome_is_orphaned_and_never_dropped(string scenario)
    {
        var (server, runner) = NewServer();
        runner.OnBatch = b => scenario switch
        {
            "51007" => Fail(GuardError(b.Nonce, 51007)),
            "51008" => Fail(GuardError(b.Nonce, 51008)),
            "51009" => Fail(GuardError(b.Nonce, 51009)),
            "51012-not-a-create-code" => Fail(GuardError(b.Nonce, 51012)),
            "wrong-nonce" => Fail(GuardError(Guid.NewGuid().ToString("D"), 51006)),
            "empty-nonce" => Fail(new GuardSqlError(51006, 16, 1, "AIREAP-GUARD  51006 x")),
            "code-prefix-mismatch" => Fail(new GuardSqlError(51006, 16, 1, $"AIREAP-GUARD {b.Nonce} 51004 x")),
            "class-17" => Fail(GuardError(b.Nonce, 51006, cls: 17)),
            "class-20" => Fail(new GuardSqlError(64, 20, 0, "A transport-level error has occurred.")),
            "state-2" => Fail(GuardError(b.Nonce, 51004, state: 2)),
            "two-errors" => Fail(GuardError(b.Nonce, 51006), GuardError(b.Nonce, 51004)),
            "timeout" => Fail(new GuardSqlError(-2, 11, 0, "Execution Timeout Expired.")),
            "transport" => new BatchResult.Faulted("IOException: the pipe has been ended"),
            "unexpected-exception" => new BatchResult.Faulted("InvalidOperationException: x"),
            "rows-then-error" => new BatchResult.SqlFailed([GuardError(b.Nonce, 51004)], RowsSeen: true),
            "created-wrong-nonce" => Row(("Outcome", "Created"), ("Nonce", Guid.NewGuid().ToString("D"))),
            "created-two-rows" => new BatchResult.Completed([new GuardResultSet([
                new GuardRow([("Outcome", "Created"), ("Nonce", b.Nonce)]), new GuardRow([("Outcome", "Created"), ("Nonce", b.Nonce)])])]),
            "created-extra-column" => Row(("Outcome", "Created"), ("Nonce", b.Nonce), ("Extra", 1)),
            "no-result-set" => new BatchResult.Completed([]),
            "unexpected-outcome" => Row(("Outcome", "Dropped"), ("Nonce", b.Nonce)),
            "runner-throws" => throw new InvalidOperationException("runner bug"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var ex = await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);

        var name = runner.Batches.Single().Value("@name");
        Assert.Equal((TestDatabaseState.Orphaned, 0, 0), server.Inspect(name));
        Assert.Contains(server.Orphans, o => o.Name == name);
        Assert.Contains("Orphaned", ex.Message);
        Assert.Throws<TestSqlGuardException>(() => server.AcquireLease(name));
        Assert.Throws<TestSqlGuardException>(() => server.ConnectionStringFor(name));
        Assert.Equal(["Create"], runner.Batches.Select(b => b.Kind)); // nothing else, and certainly no DROP
    }

    [Fact]
    public async Task A_failed_master_open_sends_nothing_registers_nothing_and_tombstones_the_name()
    {
        const string name = "AiReapTests_11111111111111111111111111111111";
        var (server, runner) = NewServer(() => name);
        runner.OnOpen = (_, _) => new OpenResult.Failed([new GuardSqlError(18456, 14, 1, "Login failed for user 'guard-test'.")], "SqlException", false);

        await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);
        Assert.Empty(runner.Batches);
        Assert.Equal((null, 0, 0), server.Inspect(name));

        runner.OnOpen = null;
        var again = await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);
        Assert.Contains("reuse", again.Message);
        Assert.Single(runner.Opens); // the second attempt was refused before opening anything
    }

    [Fact]
    public async Task Names_are_never_reused_even_after_drop_or_orphan()
    {
        var names = new Queue<string>(["AiReapTests_22222222222222222222222222222222", "AiReapTests_22222222222222222222222222222222",
            "AiReapTests_33333333333333333333333333333333", "AiReapTests_33333333333333333333333333333333"]);
        var (server, runner) = NewServer(() => names.Dequeue());

        await (await server.CreateDatabaseAsync()).DisposeAsync(); // Dropped
        await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);

        runner.OnBatch = b => Fail(GuardError(b.Nonce, 51009)); // Orphaned
        await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);
        runner.OnBatch = null;
        await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);

        Assert.Equal(["Create", "Drop", "Create"], runner.Batches.Select(b => b.Kind));
    }

    [Fact]
    public async Task An_invalid_generated_name_is_refused_before_any_connection()
    {
        var (server, runner) = NewServer(() => "AiReapDb");
        await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);
        Assert.Empty(runner.Opens);
    }

    // ---------------- DROP outcomes and single terminal decision ----------------

    [Fact]
    public async Task Dispose_drops_through_one_batch_after_clearing_the_hand_out_pool()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();

        await db.DisposeAsync();

        Assert.Equal((TestDatabaseState.Dropped, 0, 0), server.Inspect(db.Name));
        Assert.Equal(["Create", "Drop"], runner.Batches.Select(b => b.Kind));
        var clear = runner.Events.IndexOf("clearpool:" + db.Name);
        Assert.True(clear >= 0 && clear < runner.Events.IndexOf("batch:Drop"));
        Assert.Throws<TestSqlGuardException>(() => server.ConnectionStringFor(db.Name));
    }

    public static TheoryData<string> UnclearDropResults => new()
    {
        "absent", "51000", "51001", "51002", "51003", "51005", "51010", "51013", "51014", "wrong-nonce", "timeout",
        "transport", "class-20", "rows-then-error", "no-result-set", "runner-throws", "master-open-fails",
    };

    [Theory]
    [MemberData(nameof(UnclearDropResults))]
    public async Task Every_drop_outcome_except_dropped_is_orphaned_with_exactly_one_attempt(string scenario)
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        runner.OnBatch = b => b.Kind != "Drop" ? RecordingRunner.Default(b) : scenario switch
        {
            "absent" => Row(("Outcome", "Absent"), ("Nonce", b.Nonce)),
            "wrong-nonce" => Row(("Outcome", "Dropped"), ("Nonce", Guid.NewGuid().ToString("D"))),
            "timeout" => Fail(new GuardSqlError(-2, 11, 0, "Execution Timeout Expired.")),
            "transport" => new BatchResult.Faulted("IOException: x"),
            "class-20" => Fail(new GuardSqlError(64, 20, 0, "transport")),
            "rows-then-error" => new BatchResult.SqlFailed([GuardError(b.Nonce, 51005)], RowsSeen: true),
            "no-result-set" => new BatchResult.Completed([]),
            "runner-throws" => throw new InvalidOperationException("runner bug"),
            _ => Fail(GuardError(b.Nonce, int.Parse(scenario))),
        };
        if (scenario == "master-open-fails")
        {
            runner.OnBatch = null;
            runner.OnOpen = (catalog, _) => catalog == "master"
                ? new OpenResult.Failed([new GuardSqlError(53, 20, 0, "network path not found")], "SqlException", false)
                : null;
        }

        var outcome = await db.DisposeOutcomeAsync();
        var ex = await Assert.ThrowsAsync<TestSqlGuardException>(() => db.DisposeAsync().AsTask());

        Assert.Equal(TestDatabaseState.Orphaned, outcome.State);
        Assert.Equal(outcome.Detail, ex.Message);
        Assert.Equal((TestDatabaseState.Orphaned, 0, 0), server.Inspect(db.Name));
        Assert.Contains(server.Orphans, o => o.Name == db.Name);
        Assert.Equal(scenario == "master-open-fails" ? 0 : 1, runner.Batches.Count(b => b.Kind == "Drop"));

        // Repeated disposal returns the same terminal outcome and never retries.
        Assert.Same(outcome, await db.DisposeOutcomeAsync());
        await Assert.ThrowsAsync<TestSqlGuardException>(() => db.DisposeAsync().AsTask());
        Assert.Equal(scenario == "master-open-fails" ? 0 : 1, runner.Batches.Count(b => b.Kind == "Drop"));
    }

    [Fact]
    public async Task Concurrent_and_repeated_disposal_share_one_outcome_and_one_drop_batch()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        var release = new TaskCompletionSource();
        runner.OnBatchAsync = async b =>
        {
            if (b.Kind == "Drop") await release.Task;
            return RecordingRunner.Default(b);
        };

        var first = db.DisposeOutcomeAsync();
        var second = db.DisposeOutcomeAsync();
        var third = db.DisposeAsync().AsTask();

        Assert.Same(first, second);
        await runner.WaitForBatchAsync("Drop");
        Assert.False(first.IsCompleted);
        Assert.Equal(TestDatabaseState.Dropping, server.Inspect(db.Name).State);

        // Nothing can reach a database while it is Dropping.
        Assert.Throws<TestSqlGuardException>(() => server.AcquireLease(db.Name));
        Assert.Throws<TestSqlGuardException>(() => server.ConnectionStringFor(db.Name));
        var opensBefore = runner.Opens.Count;
        await Assert.ThrowsAsync<TestSqlGuardException>(() => server.OpenVerifiedAsync(db.Name));
        Assert.Equal(opensBefore, runner.Opens.Count);

        release.SetResult();
        Assert.Equal(TestDatabaseState.Dropped, (await first).State);
        await third;
        await db.DisposeAsync();
        db.Dispose();

        Assert.Equal(1, runner.Batches.Count(b => b.Kind == "Drop"));
        Assert.Equal((TestDatabaseState.Dropped, 0, 0), server.Inspect(db.Name));
    }

    // ---------------- leases (N1) ----------------

    [Fact]
    public async Task Disposing_one_lease_handle_twice_cannot_release_another_lease()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        var first = server.AcquireLease(db.Name);
        var second = server.AcquireLease(db.Name);
        Assert.Equal(2, server.Inspect(db.Name).Leases);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, server.Inspect(db.Name).Leases);

        var outcome = await db.DisposeOutcomeAsync();

        Assert.Equal(TestDatabaseState.Orphaned, outcome.State);
        Assert.Contains("1 lease(s)", outcome.Detail);
        Assert.DoesNotContain(runner.Batches, b => b.Kind == "Drop");

        second.Dispose(); // still allowed; the database stays Orphaned
        second.Dispose();
        Assert.Equal((TestDatabaseState.Orphaned, 0, 0), server.Inspect(db.Name));
        Assert.Same(outcome, await db.DisposeOutcomeAsync());
        Assert.DoesNotContain(runner.Batches, b => b.Kind == "Drop");
    }

    [Fact]
    public async Task Ready_to_dropping_needs_zero_leases_and_zero_reservations()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        var lease = server.AcquireLease(db.Name);
        var connection = await server.OpenVerifiedAsync(db.Name);
        Assert.Equal((TestDatabaseState.Ready, 1, 1), server.Inspect(db.Name));

        lease.Dispose();
        connection.Dispose();
        connection.Dispose();
        Assert.Equal((TestDatabaseState.Ready, 0, 0), server.Inspect(db.Name));

        await db.DisposeAsync();
        Assert.Equal(TestDatabaseState.Dropped, server.Inspect(db.Name).State);
        Assert.Single(runner.Batches, b => b.Kind == "Drop");
    }

    [Fact]
    public async Task An_open_verified_connection_blocks_the_drop()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        var connection = await server.OpenVerifiedAsync(db.Name);

        var outcome = await db.DisposeOutcomeAsync();
        Assert.Equal(TestDatabaseState.Orphaned, outcome.State);
        Assert.Contains("1 verified-connection reservation(s)", outcome.Detail);

        connection.Dispose();
        Assert.Equal((TestDatabaseState.Orphaned, 0, 0), server.Inspect(db.Name));
        Assert.DoesNotContain(runner.Batches, b => b.Kind == "Drop");
    }

    [Fact]
    public async Task Unknown_or_leftover_names_are_never_adopted()
    {
        var (server, runner) = NewServer();
        const string leftover = "AiReapTests_44444444444444444444444444444444";

        Assert.Throws<TestSqlGuardException>(() => server.AcquireLease(leftover));
        Assert.Throws<TestSqlGuardException>(() => server.ConnectionStringFor(leftover));
        await Assert.ThrowsAsync<TestSqlGuardException>(() => server.OpenVerifiedAsync(leftover));
        Assert.Empty(runner.Opens);
    }

    [Fact]
    public async Task Parallel_lease_traffic_leaves_the_count_at_zero()
    {
        var (server, _) = NewServer();
        var db = await server.CreateDatabaseAsync();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            var lease = server.AcquireLease(db.Name);
            lease.Dispose();
            lease.Dispose();
        })));

        Assert.Equal((TestDatabaseState.Ready, 0, 0), server.Inspect(db.Name));
        await db.DisposeAsync();
    }

    // ---------------- verified open ----------------

    [Theory]
    [InlineData(51001)]
    [InlineData(51002)]
    [InlineData(51005)]
    [InlineData(51012)]
    public async Task A_verify_mismatch_orphans_the_database_even_while_leased(int code)
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        var lease = server.AcquireLease(db.Name);
        runner.OnBatch = b => b.Kind == "Verify" ? Fail(GuardError(b.Nonce, code)) : RecordingRunner.Default(b);

        await Assert.ThrowsAsync<TestSqlGuardException>(() => server.OpenVerifiedAsync(db.Name));

        Assert.Equal((TestDatabaseState.Orphaned, 1, 0), server.Inspect(db.Name));
        Assert.Throws<TestSqlGuardException>(() => server.ConnectionStringFor(db.Name));
        lease.Dispose();
        Assert.Equal(TestDatabaseState.Orphaned, (await db.DisposeOutcomeAsync()).State);
        Assert.DoesNotContain(runner.Batches, b => b.Kind == "Drop");
        Assert.Equal(runner.Opens.Count, runner.Closes); // the failed verify connection was closed
    }

    [Fact]
    public async Task A_verify_row_naming_another_target_is_a_mismatch()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        runner.OnBatch = b => b.Kind == "Verify"
            ? Row(("ServerName", "SOME-DEV-BOX"), ("DatabaseName", db.Name), ("RunToken", RunToken), ("Nonce", b.Nonce))
            : RecordingRunner.Default(b);

        await Assert.ThrowsAsync<TestSqlGuardException>(() => server.OpenVerifiedAsync(db.Name));
        Assert.Equal((TestDatabaseState.Orphaned, 0, 0), server.Inspect(db.Name));
    }

    public static TheoryData<string> FailedVerifyResults => new() { "51014", "51013", "51010", "timeout", "transport", "wrong-nonce", "empty" };

    [Theory]
    [MemberData(nameof(FailedVerifyResults))]
    public async Task Other_verify_failures_throw_release_the_reservation_and_keep_the_state(string scenario)
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        runner.OnBatch = b => b.Kind != "Verify" ? RecordingRunner.Default(b) : scenario switch
        {
            "timeout" => Fail(new GuardSqlError(-2, 11, 0, "Execution Timeout Expired.")),
            "transport" => new BatchResult.Faulted("IOException: x"),
            "wrong-nonce" => Row(("ServerName", Expected), ("DatabaseName", db.Name), ("RunToken", RunToken), ("Nonce", Guid.NewGuid().ToString("D"))),
            "empty" => new BatchResult.Completed([]),
            _ => Fail(GuardError(b.Nonce, int.Parse(scenario))),
        };

        await Assert.ThrowsAsync<TestSqlGuardException>(() => server.OpenVerifiedAsync(db.Name));

        Assert.Equal((TestDatabaseState.Ready, 0, 0), server.Inspect(db.Name));
        Assert.Equal(runner.Opens.Count, runner.Closes);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Login_4060_is_retried_at_most_three_times_and_nothing_else_is_retried()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        var unavailable = new OpenResult.Failed([new GuardSqlError(4060, 11, 1, "Cannot open database")], "SqlException", false);

        runner.OnOpen = (catalog, n) => catalog == db.Name && n <= 2 ? unavailable : null;
        (await server.OpenVerifiedAsync(db.Name)).Dispose();
        Assert.Equal(3, runner.Opens.Count(c => c == db.Name));

        runner.OnOpen = (catalog, _) => catalog == db.Name ? unavailable : null;
        await Assert.ThrowsAsync<TestSqlGuardException>(() => server.OpenVerifiedAsync(db.Name));
        Assert.Equal(6, runner.Opens.Count(c => c == db.Name));

        runner.OnOpen = (catalog, _) => catalog == db.Name
            ? new OpenResult.Failed([new GuardSqlError(18456, 14, 1, "Login failed")], "SqlException", false)
            : null;
        await Assert.ThrowsAsync<TestSqlGuardException>(() => server.OpenVerifiedAsync(db.Name));
        Assert.Equal(7, runner.Opens.Count(c => c == db.Name));

        Assert.Equal((TestDatabaseState.Ready, 0, 0), server.Inspect(db.Name));
    }

    // ---------------- reservations kept when a session may be live (N4) ----------------

    [Fact]
    public async Task A_failed_verify_whose_connection_cannot_be_closed_keeps_the_reservation_and_reports_both_errors()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        runner.OnBatch = b => b.Kind == "Verify" ? new BatchResult.Faulted("IOException: x") : RecordingRunner.Default(b);
        runner.OnClose = catalog => catalog == db.Name ? new IOException("close failed") : null;

        var ex = await Assert.ThrowsAsync<AggregateException>(() => server.OpenVerifiedAsync(db.Name));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.IsType<TestSqlGuardException>(ex.InnerExceptions[0]);
        Assert.IsType<IOException>(ex.InnerExceptions[1]);
        Assert.Equal((TestDatabaseState.Ready, 0, 1), server.Inspect(db.Name));

        var outcome = await db.DisposeOutcomeAsync();
        Assert.Equal(TestDatabaseState.Orphaned, outcome.State);
        Assert.Contains("could not be confirmed closed", outcome.Detail);
        Assert.DoesNotContain(runner.Batches, b => b.Kind == "Drop");
    }

    [Fact]
    public async Task A_failed_open_whose_connection_could_not_be_disposed_keeps_the_reservation()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        runner.OnOpen = (catalog, _) => catalog == db.Name
            ? new OpenResult.Failed([new GuardSqlError(4060, 11, 1, "Cannot open database")], "SqlException", DisposeFailed: true)
            : null;

        await Assert.ThrowsAsync<TestSqlGuardException>(() => server.OpenVerifiedAsync(db.Name));

        Assert.Equal((TestDatabaseState.Ready, 0, 1), server.Inspect(db.Name));
        Assert.Equal(TestDatabaseState.Orphaned, (await db.DisposeOutcomeAsync()).State);
        Assert.DoesNotContain(runner.Batches, b => b.Kind == "Drop");
    }

    [Fact]
    public async Task A_verified_connection_whose_dispose_fails_keeps_its_reservation_once()
    {
        var (server, runner) = NewServer();
        var db = await server.CreateDatabaseAsync();
        var connection = await server.OpenVerifiedAsync(db.Name);
        runner.OnClose = catalog => catalog == db.Name ? new IOException("close failed") : null;

        Assert.Throws<IOException>(connection.Dispose);
        connection.Dispose(); // a second dispose does nothing at all
        await connection.DisposeAsync();

        Assert.Equal((TestDatabaseState.Ready, 0, 1), server.Inspect(db.Name));
        Assert.Equal(TestDatabaseState.Orphaned, (await db.DisposeOutcomeAsync()).State);
        Assert.DoesNotContain(runner.Batches, b => b.Kind == "Drop");
    }

    // ---------------- raw connection access (N2) ----------------

    [Fact]
    public void Verified_types_expose_no_raw_connection_or_command()
    {
        var forbidden = new[] { typeof(DbConnection), typeof(DbCommand) };
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        foreach (var type in new[] { typeof(VerifiedConnection), typeof(VerifiedCommand) })
        {
            foreach (var field in type.GetFields(all).Where(f => forbidden.Any(t => t.IsAssignableFrom(f.FieldType))))
            {
                Assert.True(field.IsPrivate, $"{type.Name}.{field.Name} must be private");
            }

            foreach (var property in type.GetProperties(all))
            {
                var getter = property.GetGetMethod(nonPublic: true);
                Assert.False(getter is not null && !getter.IsPrivate && forbidden.Any(t => t.IsAssignableFrom(property.PropertyType)),
                    $"{type.Name}.{property.Name} exposes a raw connection or command");
            }

            foreach (var method in type.GetMethods(all).Where(m => !m.IsPrivate && m.DeclaringType == type))
            {
                Assert.False(forbidden.Any(t => t.IsAssignableFrom(method.ReturnType)), $"{type.Name}.{method.Name} returns a raw connection or command");
            }
        }
    }

    [Fact]
    public async Task A_verified_connection_refuses_work_once_it_is_not_open_or_disposed()
    {
        var (server, _) = NewServer();
        var db = await server.CreateDatabaseAsync();
        var connection = await server.OpenVerifiedAsync(db.Name);

        // Offline, the scripted connection was never physically opened, so it is not usable.
        Assert.Throws<TestSqlGuardException>(() => connection.CreateCommand("SELECT 1"));
        Assert.Throws<TestSqlGuardException>(connection.CreateMigrationOptions);

        connection.Dispose();
        Assert.Throws<TestSqlGuardException>(() => connection.CreateCommand("SELECT 1"));
        await db.DisposeAsync();
    }

    // ---------------- EF tripwire (offline, local named pipe that does not exist) ----------------

    private static readonly string OfflineEfConnection = new SqlConnectionStringBuilder
    {
        DataSource = OfflinePipe,
        InitialCatalog = "AiReapTests_" + new string('e', 32),
        IntegratedSecurity = true,
        ConnectTimeout = 1,
        Pooling = false,
    }.ConnectionString;

    private static AiReapDbContext TripwireContext(DbConnection? connection = null)
    {
        var builder = new DbContextOptionsBuilder<AiReapDbContext>();
        if (connection is null) builder.UseSqlServer(OfflineEfConnection);
        else builder.UseSqlServer(connection);
        return new AiReapDbContext(builder.AddInterceptors(new EfConnectionTripwire()).Options);
    }

    [Fact]
    public void The_tripwire_stops_ef_opening_a_connection_from_a_connection_string()
    {
        using var db = TripwireContext();
        AssertGuardInChain(Record.Exception(() => db.Database.OpenConnection())!);
    }

    [Fact]
    public async Task The_tripwire_stops_ef_opening_a_connection_asynchronously()
    {
        await using var db = TripwireContext();
        AssertGuardInChain((await Record.ExceptionAsync(() => db.Database.OpenConnectionAsync()))!);
    }

    [Fact]
    public void The_tripwire_stops_ef_creating_its_own_connection()
    {
        using var db = TripwireContext();
        AssertGuardInChain(Record.Exception(() => db.Database.GetDbConnection())!);
    }

    [Fact]
    public void The_tripwire_stops_ef_creating_a_database_through_master()
    {
        using var db = TripwireContext();
        AssertGuardInChain(Record.Exception(() => db.GetService<IRelationalDatabaseCreator>().Create())!);
    }

    [Fact]
    public void The_tripwire_stops_migrate_on_a_closed_connection()
    {
        using var connection = new SqlConnection(OfflineEfConnection);
        using var db = TripwireContext(connection);
        AssertGuardInChain(Record.Exception(() => db.Database.Migrate())!);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    // ---------------- redaction ----------------

    [Fact]
    public async Task No_guard_message_contains_the_password()
    {
        var (server, runner) = NewServer();
        runner.OnOpen = (_, _) => new OpenResult.Failed([], $"SqlException: login failed using {FakePassword}", false);
        var open = await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);
        Assert.DoesNotContain(FakePassword, open.Message);
        Assert.Contains("***", open.Message);

        runner.OnOpen = null;
        runner.OnBatch = b => new BatchResult.Faulted($"InvalidOperationException: {FakePassword}");
        var batch = await Assert.ThrowsAsync<TestSqlGuardException>(server.CreateDatabaseAsync);
        Assert.DoesNotContain(FakePassword, batch.Message);
        Assert.All(server.Orphans, o => Assert.DoesNotContain(FakePassword, o.Reason));
    }
}

// TestHost's own guard paths, offline: the internal constructor takes a TestSqlServer over the recording
// runner and never touches TestSqlServer.Default. The API host is never started.
public class TestHostGuardTests
{
    private static TestHost Host(TestSqlServer server, string? existingDatabase = null, TestHostSeams? seams = null) =>
        new(() => server, existingDatabase, bootstrapAdmin: true, chatClient: null, settings: null, seams);

    [Fact]
    public void Missing_settings_fail_testhost_construction_before_any_connection()
    {
        var runner = new RecordingRunner();

        var ex = Assert.Throws<TestSqlGuardException>(() =>
            new TestHost(() => TestSqlServer.FromEnvironment(TestSqlServerGuardTests.Env(), runner), null, true, null, null, null));

        Assert.Contains(TestSqlSettings.ConnectionVariable, ex.Message);
        Assert.Empty(runner.Opens);
        Assert.Empty(runner.Batches);
    }

    [Fact]
    public void Unsafe_settings_fail_testhost_construction_before_any_connection()
    {
        var runner = new RecordingRunner();
        var withDatabase = new SqlConnectionStringBuilder(TestSqlServerGuardTests.ValidConnection) { InitialCatalog = "AiReapDb" }.ConnectionString;
        var env = TestSqlServerGuardTests.Env(
            (TestSqlSettings.ConnectionVariable, withDatabase), (TestSqlSettings.ExpectedServerVariable, TestSqlServerGuardTests.Expected));

        Assert.Throws<TestSqlGuardException>(() => new TestHost(() => TestSqlServer.FromEnvironment(env, runner), null, true, null, null, null));
        Assert.Empty(runner.Opens);
    }

    [Fact]
    public void An_owner_constructor_failure_after_creation_drops_the_database_and_keeps_the_original_error()
    {
        var (server, runner) = TestSqlServerGuardTests.NewServer();

        // Offline the verified connection is never physically open, so migration setup fails after CREATE.
        var ex = Assert.Throws<TestSqlGuardException>(() => Host(server));

        Assert.Contains("no longer open and verified", ex.Message);
        var name = runner.Batches[0].Value("@name");
        Assert.Equal(["Create", "Verify", "Drop"], runner.Batches.Select(b => b.Kind));
        Assert.Equal((TestDatabaseState.Dropped, 0, 0), server.Inspect(name));
    }

    [Fact]
    public void An_owner_constructor_failure_whose_cleanup_also_fails_reports_both()
    {
        var (server, runner) = TestSqlServerGuardTests.NewServer();
        runner.OnBatch = b => b.Kind == "Drop" ? new BatchResult.Faulted("IOException: x") : RecordingRunner.Default(b);

        var ex = Assert.Throws<AggregateException>(() => Host(server));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains("no longer open and verified", ex.InnerExceptions[0].Message);
        Assert.Contains("Orphaned", ex.InnerExceptions[1].Message);
        Assert.Equal(TestDatabaseState.Orphaned, server.Inspect(runner.Batches[0].Value("@name")).State);
    }

    [Fact]
    public async Task A_lessee_constructor_failure_after_the_lease_releases_it()
    {
        var (server, runner) = TestSqlServerGuardTests.NewServer();
        var db = await server.CreateDatabaseAsync();
        runner.OnBatch = b => b.Kind == "Verify" ? new BatchResult.Faulted("IOException: x") : RecordingRunner.Default(b);

        Assert.Throws<TestSqlGuardException>(() => Host(server, db.Name));

        Assert.Equal((TestDatabaseState.Ready, 0, 0), server.Inspect(db.Name));
        runner.OnBatch = null;
        await db.DisposeAsync();
        Assert.Equal(TestDatabaseState.Dropped, server.Inspect(db.Name).State);
    }

    [Fact]
    public async Task A_lessee_failure_after_build_releases_the_lease_once_and_keeps_the_original_error()
    {
        var (server, _) = TestSqlServerGuardTests.NewServer();
        var db = await server.CreateDatabaseAsync();
        var other = server.AcquireLease(db.Name);
        var failure = new InvalidOperationException("late failure");

        var ex = Assert.Throws<InvalidOperationException>(() => Host(server, db.Name, new TestHostSeams(AfterConstruction: () => throw failure)));

        Assert.Same(failure, ex);
        Assert.Equal((TestDatabaseState.Ready, 1, 0), server.Inspect(db.Name)); // only its own lease went
        other.Dispose();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_and_repeated_testhost_disposal_await_the_same_task()
    {
        var (server, _) = TestSqlServerGuardTests.NewServer();
        var db = await server.CreateDatabaseAsync();
        var release = new TaskCompletionSource();
        var factoryDisposals = 0;
        var host = Host(server, db.Name, new TestHostSeams(DisposeFactory: async () =>
        {
            Interlocked.Increment(ref factoryDisposals);
            await release.Task;
        }));
        Assert.Equal(1, server.Inspect(db.Name).Leases);

        var first = host.DisposeAsync().AsTask();
        var second = host.DisposeAsync().AsTask();

        Assert.Same(first, second);
        Assert.False(second.IsCompleted); // the second call does not return early
        release.SetResult();
        await Task.WhenAll(first, second);
        await host.DisposeAsync();

        Assert.Equal(1, factoryDisposals);
        Assert.Equal((TestDatabaseState.Ready, 0, 0), server.Inspect(db.Name));
        await db.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_api_host_disposal_still_releases_the_lease_and_is_reported()
    {
        var (server, _) = TestSqlServerGuardTests.NewServer();
        var db = await server.CreateDatabaseAsync();
        var failure = new InvalidOperationException("host dispose failed");
        var host = Host(server, db.Name, new TestHostSeams(DisposeFactory: () => throw failure));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisposeAsync().AsTask());

        Assert.Same(failure, ex);
        Assert.Equal((TestDatabaseState.Ready, 0, 0), server.Inspect(db.Name));
        await db.DisposeAsync();
    }

    [Fact]
    public void Raw_sql_helpers_keep_their_signatures()
    {
        Assert.Equal(typeof(Task<int>), typeof(TestHost).GetMethod(nameof(TestHost.ScalarAsync), [typeof(string)])!.ReturnType);
        Assert.Equal(typeof(Task), typeof(TestHost).GetMethod(nameof(TestHost.ExecuteAsync), [typeof(string)])!.ReturnType);
        var ctor = typeof(TestHost).GetConstructor([typeof(string), typeof(bool), typeof(IAiChatClient), typeof(IReadOnlyDictionary<string, string>)]);
        Assert.NotNull(ctor);
        Assert.Equal(typeof(string), typeof(TestHost).GetProperty(nameof(TestHost.ConnectionString))!.PropertyType);
    }
}

internal sealed record RecordedBatch(string Kind, string Catalog, string Sql, IReadOnlyList<GuardParameter> Parameters)
{
    public string Nonce => Value("@nonce");

    public string Value(string name) => Parameters.Single(p => p.Name == name).Value;
}

// Records every guard call and answers with scripted results. Connections it hands out are never opened.
internal sealed class RecordingRunner : ITestSqlBatchRunner
{
    private readonly object _lock = new();
    private readonly List<string> _opens = new();
    private readonly List<RecordedBatch> _batches = new();
    private readonly List<string> _events = new();
    private int _closes;

    public Func<string, int, OpenResult?>? OnOpen { get; set; }          // (catalog, nth open of that catalog)
    public Func<RecordedBatch, BatchResult>? OnBatch { get; set; }
    public Func<RecordedBatch, Task<BatchResult>>? OnBatchAsync { get; set; }
    public Func<string, Exception?>? OnClose { get; set; }               // (catalog) -> exception to throw

    public List<string> Opens { get { lock (_lock) return _opens.ToList(); } }
    public List<RecordedBatch> Batches { get { lock (_lock) return _batches.ToList(); } }
    public List<string> Events { get { lock (_lock) return _events.ToList(); } }
    public int Closes { get { lock (_lock) return _closes; } }

    public static BatchResult Default(RecordedBatch b) => b.Kind switch
    {
        "Create" => TestSqlServerGuardTests.Row(("Outcome", "Created"), ("Nonce", b.Nonce)),
        "Drop" => TestSqlServerGuardTests.Row(("Outcome", "Dropped"), ("Nonce", b.Nonce)),
        _ => TestSqlServerGuardTests.Row(("ServerName", TestSqlServerGuardTests.Expected.ToLowerInvariant()), ("DatabaseName", b.Value("@name")),
            ("RunToken", b.Value("@token")), ("Nonce", b.Nonce)),
    };

    public Task<OpenResult> OpenAsync(string connectionString)
    {
        var catalog = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        int nth;
        lock (_lock)
        {
            _opens.Add(catalog);
            _events.Add("open:" + catalog);
            nth = _opens.Count(c => c == catalog);
        }

        var scripted = OnOpen?.Invoke(catalog, nth);
        return Task.FromResult(scripted ?? new OpenResult.Opened(new SqlConnection(connectionString)));
    }

    public async Task<BatchResult> ExecuteAsync(SqlConnection connection, string sql, IReadOnlyList<GuardParameter> parameters)
    {
        var kind = sql == TestSqlServer.CreateBatch ? "Create" : sql == TestSqlServer.DropBatch ? "Drop" : sql == TestSqlServer.VerifyBatch ? "Verify" : "Unknown";
        var batch = new RecordedBatch(kind, new SqlConnectionStringBuilder(connection.ConnectionString).InitialCatalog, sql, parameters);
        lock (_lock)
        {
            _batches.Add(batch);
            _events.Add("batch:" + kind);
        }

        if (OnBatchAsync is not null) return await OnBatchAsync(batch);
        return OnBatch is not null ? OnBatch(batch) : Default(batch);
    }

    public async Task WaitForBatchAsync(string kind)
    {
        for (var i = 0; i < 500 && !Batches.Any(b => b.Kind == kind); i++) await Task.Delay(10);
        Assert.Contains(Batches, b => b.Kind == kind);
    }

    public void Close(SqlConnection connection)
    {
        var catalog = new SqlConnectionStringBuilder(connection.ConnectionString).InitialCatalog;
        lock (_lock)
        {
            _closes++;
            _events.Add("close:" + catalog);
        }

        var failure = OnClose?.Invoke(catalog);
        if (failure is not null) throw failure;
        connection.Dispose();
    }

    public void ClearPool(string connectionString)
    {
        lock (_lock) _events.Add("clearpool:" + new SqlConnectionStringBuilder(connectionString).InitialCatalog);
    }
}
