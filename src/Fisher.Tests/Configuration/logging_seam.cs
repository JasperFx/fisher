using System.Data.Common;
using Fisher.Internal;
using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#207 — <see cref="IFisherLogger" /> / <see cref="IFisherSessionLogger" />, the per-store
///     and per-session seam that answers "what SQL did that actually run?".
/// </summary>
/// <remarks>
///     <para>
///         The facts worth having here are the two the issue asked to be decided rather than ported:
///         that parameter <em>values</em> are omitted by default and the parameter's bound CLR type is
///         logged instead, and that a store with no logger pays nothing — including not being asked
///         for a change set it would otherwise have to build.
///     </para>
/// </remarks>
public class logging_seam : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("logging-seam");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Trawler>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        await _database.DisposeAsync();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the store-level seam ----

    [Fact]
    public void a_store_nobody_configured_holds_the_nullo_logger()
    {
        _store.Options.Logger().ShouldBeSameAs(NulloFisherLogger.Flyweight);
    }

    [Fact]
    public void the_store_logger_cannot_be_set_to_null()
    {
        Should.Throw<ArgumentNullException>(() => _store.Options.Logger(null!));
    }

    [Fact]
    public void a_session_starts_with_the_stores_logger()
    {
        var logger = new RecordingLogger();
        _store.Options.Logger(logger);

        using var session = _store.LightweightSession();

        session.Logger.ShouldBeSameAs(logger);

        // Restore, since the store is shared across this class's tests.
        _store.Options.Logger(NulloFisherLogger.Flyweight);
    }

    // ---- command logging ----

    [Fact]
    public async Task a_commit_logs_its_statements_and_then_the_commit()
    {
        var logger = new RecordingLogger();

        await using var session = _store.LightweightSession();
        session.Logger = logger;

        session.Store(new Trawler { Id = Guid.NewGuid(), Name = "Andrea Gail" });
        await session.SaveChangesAsync(Token);

        logger.Successes.ShouldNotBeEmpty();
        logger.Successes.ShouldContain(x => x.Contains("fi_doc_trawler"));

        logger.Commits.Count.ShouldBe(1);
        logger.Commits[0].Operations.ShouldBe(1);
        logger.Commits[0].Updated.ShouldBe(1);

        // OnBeforeExecute has to bracket every statement, or the duration on the line is measured
        // from whatever ran before it.
        logger.BeforeExecuteCount.ShouldBe(logger.Successes.Count);
    }

    [Fact]
    public async Task a_query_logs_the_statement_it_ran()
    {
        var logger = new RecordingLogger();

        await using var session = _store.LightweightSession();
        session.Logger = logger;

        await session.Query<Trawler>().Where(x => x.Name == "nobody").ToListAsync(Token);

        logger.Successes.ShouldContain(x => x.Contains("fi_doc_trawler") && x.Contains("select"));
    }

    [Fact]
    public async Task a_load_logs_the_statement_it_ran()
    {
        var logger = new RecordingLogger();

        await using var session = _store.LightweightSession();
        session.Logger = logger;

        await session.LoadAsync<Trawler>(Guid.NewGuid(), Token);

        logger.Successes.ShouldContain(x => x.Contains("fi_doc_trawler"));
    }

    [Fact]
    public async Task a_failed_statement_is_logged_with_its_command_and_then_the_failed_commit()
    {
        var logger = new RecordingLogger();

        var id = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Insert(new Trawler { Id = id, Name = "first" });
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.LightweightSession();
        session.Logger = logger;

        // A second Insert on the same id is a primary key violation, so the statement throws inside
        // the batch and the whole unit of work goes with it.
        session.Insert(new Trawler { Id = id, Name = "second" });

        await Should.ThrowAsync<Exception>(async () => await session.SaveChangesAsync(Token));

        logger.CommandFailures.Count.ShouldBe(1);
        logger.CommandFailures[0].Sql.ShouldContain("fi_doc_trawler");

        // Both halves are wanted: the statement says what SQLite refused, the message says the whole
        // unit of work is gone.
        logger.MessageFailures.Count.ShouldBe(1);
        logger.MessageFailures[0].ShouldContain("commit");
    }

    // ---- the parameter value decision ----

    [Fact]
    public void by_default_a_parameter_is_described_by_its_type_and_not_its_value()
    {
        using var command = new SqliteCommand("select 1 where ? = ? and ? = ?");
        command.Parameters.AddWithValue("@p0", "9 Broad Street, Gloucester");
        command.Parameters.AddWithValue("@p1", 42);
        command.Parameters.Add(new SqliteParameter("@p2", DBNull.Value));

        var described = DefaultFisherLogger.Describe(command, includeValues: false);

        described.ShouldNotContain("Broad Street");
        described.ShouldContain("(String)");
        described.ShouldContain("(Int32)");
        described.ShouldContain("(null)");
    }

    [Fact]
    public void opting_in_logs_the_values()
    {
        using var command = new SqliteCommand("select 1 where ? = ?");
        command.Parameters.AddWithValue("@p0", "9 Broad Street, Gloucester");

        DefaultFisherLogger.Describe(command, includeValues: true)
            .ShouldContain("9 Broad Street, Gloucester");
    }

    /// <summary>
    ///     The reason the default is a type rather than a redaction marker: it is the diagnostic for
    ///     Fisher's sharpest binding trap, where a <see cref="Guid" /> bound without conversion becomes
    ///     a BLOB that can never match the TEXT the schema holds.
    /// </summary>
    [Fact]
    public void the_type_shown_is_what_diagnoses_the_guid_binding_trap()
    {
        using var wrong = new SqliteCommand("select 1 where id = ?");
        wrong.Parameters.AddWithValue("@p0", Guid.NewGuid());

        using var right = new SqliteCommand("select 1 where id = ?");
        right.Parameters.AddWithValue("@p0", Guid.NewGuid().ToString("d"));

        DefaultFisherLogger.Describe(wrong, includeValues: false).ShouldContain("(Guid)");
        DefaultFisherLogger.Describe(right, includeValues: false).ShouldContain("(String)");
    }

    [Fact]
    public async Task the_default_logger_omits_values_from_a_real_commit()
    {
        var captured = new CapturingLoggerProvider();

        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(captured);
        });

        await using var session = _store.LightweightSession();
        session.Logger = new DefaultFisherLogger(factory.CreateLogger("Fisher")).StartSession(session);

        session.Store(new Trawler { Id = Guid.NewGuid(), Name = "Hannah Boden" });
        await session.SaveChangesAsync(Token);

        var written = string.Join("\n", captured.Messages);

        written.ShouldContain("fi_doc_trawler");
        written.ShouldNotContain("Hannah Boden");
    }

    [Fact]
    public async Task opting_in_through_the_logger_writes_the_document_body()
    {
        var captured = new CapturingLoggerProvider();

        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(captured);
        });

        await using var session = _store.LightweightSession();
        session.Logger = new DefaultFisherLogger(factory.CreateLogger("Fisher"), logParameterValues: true)
            .StartSession(session);

        session.Store(new Trawler { Id = Guid.NewGuid(), Name = "Hannah Boden" });
        await session.SaveChangesAsync(Token);

        string.Join("\n", captured.Messages).ShouldContain("Hannah Boden");
    }

    // ---- the hot path ----

    /// <summary>
    ///     The no-logger path costs no allocations at all — the fisher#165 property, held before it
    ///     could be lost.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Measured on the guard rather than through a real commit, deliberately: a commit
    ///         allocates for a hundred reasons that have nothing to do with logging, so a whole-commit
    ///         measurement could only ever be a threshold, and a threshold wide enough to be stable is
    ///         wide enough to hide a per-command allocation. What has to be exactly zero is the thing
    ///         this feature added, and that is <see cref="FisherSession.IsLogging" /> plus the
    ///         flyweight's answer.
    ///     </para>
    ///     <para>
    ///         <c>GC.GetAllocatedBytesForCurrentThread</c> is exact rather than sampled, so
    ///         <c>ShouldBe(0)</c> is a real assertion and not a tolerance.
    ///     </para>
    /// </remarks>
    [Fact]
    public void the_no_logger_path_allocates_nothing()
    {
        using var session = (FisherSession)_store.LightweightSession();
        using var command = new SqliteCommand("select 1");

        // Warm up: JIT the property, the interface dispatch and the assertion path before measuring.
        for (var i = 0; i < 64; i++)
        {
            Exercise(session, command);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 10_000; i++)
        {
            Exercise(session, command);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }

    /// <summary>
    ///     Exactly what a command site does — ask, and touch nothing if the answer is no.
    /// </summary>
    private static void Exercise(FisherSession session, DbCommand command)
    {
        if (session.IsLogging)
        {
            session.Logger.OnBeforeExecute(command);
            session.Logger.LogSuccess(command);
        }
    }

    [Fact]
    public void an_unlogged_session_reports_that_it_is_not_logging()
    {
        using var session = (FisherSession)_store.LightweightSession();

        session.IsLogging.ShouldBeFalse();
    }

    [Fact]
    public void a_logger_whose_level_is_off_reports_that_it_is_not_logging()
    {
        using var session = (FisherSession)_store.LightweightSession();

        // NullLogger.IsEnabled is false for every level, which is the shape of a host at its default
        // levels — attached, and still with nothing to say.
        session.Logger = new DefaultFisherLogger(NullLogger.Instance);

        session.IsLogging.ShouldBeFalse();
    }

    [Fact]
    public void assigning_the_flyweight_back_restores_the_free_path()
    {
        using var session = (FisherSession)_store.LightweightSession();

        session.Logger = new RecordingLogger();
        session.IsLogging.ShouldBeTrue();

        session.Logger = NulloFisherLogger.Flyweight;
        session.IsLogging.ShouldBeFalse();
    }

    /// <summary>
    ///     The specific shape fisher#165 was: an argument built before the gate that would have
    ///     rejected it.
    /// </summary>
    /// <remarks>
    ///     <c>RecordSavedChanges</c> wants an <see cref="Services.IChangeSet" /> that
    ///     <c>SaveChangesAsync</c> otherwise builds only when a listener is registered — so if the
    ///     guard were inside the logger rather than at the call site, the change set would be
    ///     constructed for every commit on every store in the world, and this logger would be called.
    ///     Its being called is therefore the exact evidence of the regression.
    /// </remarks>
    [Fact]
    public async Task a_disabled_logger_is_never_asked_for_a_change_set()
    {
        var logger = new RecordingLogger { Answers = false };

        await using var session = _store.LightweightSession();
        session.Logger = logger;

        session.Store(new Trawler { Id = Guid.NewGuid(), Name = "Miss Millie" });
        await session.SaveChangesAsync(Token);

        logger.Commits.ShouldBeEmpty();
        logger.Successes.ShouldBeEmpty();
        logger.BeforeExecuteCount.ShouldBe(0);
    }

    // ---- tenant scopes ----

    /// <summary>
    ///     A tenant scope shares the parent's unit of work, so it must share the parent's logger —
    ///     otherwise a caller who set <c>session.Logger</c> would not see the reads it makes through
    ///     <c>ForTenant</c>, and one transaction's log would be split across two.
    /// </summary>
    [Fact]
    public void a_tenant_scope_forwards_to_the_session_that_created_it()
    {
        var logger = new RecordingLogger();

        using var session = _store.LightweightSession();
        var scope = (FisherSession)session.ForTenant("gloucester");

        scope.Logger.ShouldBeSameAs(session.Logger);

        session.Logger = logger;

        scope.Logger.ShouldBeSameAs(logger);
        scope.IsLogging.ShouldBeTrue();
    }

    // ---- DI ----

    /// <summary>
    ///     <c>AddFisher</c> attaches a <see cref="DefaultFisherLogger" />, as <c>AddMarten</c> does —
    ///     otherwise <c>session.Logger</c> in a hosted application would be an API that compiles and
    ///     does nothing.
    /// </summary>
    [Fact]
    public void add_fisher_attaches_the_default_logger()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFisher(options => options.ConnectionString = _database.ConnectionString);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<DocumentStore>().Options.Logger()
            .ShouldBeOfType<DefaultFisherLogger>()
            .LogParameterValues.ShouldBeFalse();
    }

    [Fact]
    public void the_store_option_turns_parameter_values_on_for_the_attached_logger()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFisher(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.LogSqlParameterValues = true;
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<DocumentStore>().Options.Logger()
            .ShouldBeOfType<DefaultFisherLogger>()
            .LogParameterValues.ShouldBeTrue();
    }

    [Fact]
    public void a_logger_the_application_named_is_not_replaced()
    {
        var mine = new ConsoleFisherLogger();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFisher(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Logger(mine);
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<DocumentStore>().Options.Logger().ShouldBeSameAs(mine);
    }

    // ---- test doubles ----

    public class Trawler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class RecordingLogger: IFisherLogger, IFisherSessionLogger
    {
        public bool Answers { get; init; } = true;

        public bool Enabled => Answers;

        public List<string> Successes { get; } = [];
        public List<(string Sql, Exception Exception)> CommandFailures { get; } = [];
        public List<string> MessageFailures { get; } = [];
        public List<DefaultFisherLogger.CommitCounts> Commits { get; } = [];
        public int BeforeExecuteCount { get; private set; }

        public IFisherSessionLogger StartSession(IQuerySession session) => this;

        public void OnBeforeExecute(DbCommand command) => BeforeExecuteCount++;

        public void LogSuccess(DbCommand command) => Successes.Add(command.CommandText);

        public void LogFailure(DbCommand command, Exception ex)
            => CommandFailures.Add((command.CommandText, ex));

        public void LogFailure(Exception ex, string message) => MessageFailures.Add(message);

        public void RecordSavedChanges(IDocumentSession session, Services.IChangeSet commit)
            => Commits.Add(DefaultFisherLogger.CommitCounts.For(commit));
    }

    private sealed class CapturingLoggerProvider: ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
