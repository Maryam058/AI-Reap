#pragma warning disable EF1001 // IAsyncQueryProvider is EF's provider seam; implemented here only to run LINQ in memory.
using System.Collections;
using System.Linq.Expressions;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace AiReap.Tests;

// A test-only IAiReapDbContext over in-memory lists, for unit tests of Application services and agents
// that need no SQL Server: EF's async LINQ (ToListAsync, FirstOrDefaultAsync, AnyAsync) runs as LINQ to
// objects. Add appends to the list; SaveChangesAsync counts calls and saves nothing. Navigation
// properties are not populated, so a query that walks one needs the tests to set it (or an empty set).
public sealed class InMemoryAiReapDb : IAiReapDbContext
{
    public DbSet<Project> Projects { get; } = new InMemoryDbSet<Project>();
    public DbSet<ProjectStakeholder> ProjectStakeholders { get; } = new InMemoryDbSet<ProjectStakeholder>();
    public DbSet<ProjectMember> ProjectMembers { get; } = new InMemoryDbSet<ProjectMember>();
    public DbSet<RequirementSource> RequirementSources { get; } = new InMemoryDbSet<RequirementSource>();
    public DbSet<RequirementSourceFile> RequirementSourceFiles { get; } = new InMemoryDbSet<RequirementSourceFile>();
    public DbSet<Artifact> Artifacts { get; } = new InMemoryDbSet<Artifact>();
    public DbSet<ArtifactVersion> ArtifactVersions { get; } = new InMemoryDbSet<ArtifactVersion>();
    public DbSet<ArtifactRelationship> ArtifactRelationships { get; } = new InMemoryDbSet<ArtifactRelationship>();
    public DbSet<ArtifactReview> ArtifactReviews { get; } = new InMemoryDbSet<ArtifactReview>();
    public DbSet<ArtifactImpactNotice> ArtifactImpactNotices { get; } = new InMemoryDbSet<ArtifactImpactNotice>();
    public DbSet<AIExecution> AIExecutions { get; } = new InMemoryDbSet<AIExecution>();
    public DbSet<Document> Documents { get; } = new InMemoryDbSet<Document>();
    public DbSet<DocumentChunk> DocumentChunks { get; } = new InMemoryDbSet<DocumentChunk>();
    public DbSet<AgentRun> AgentRuns { get; } = new InMemoryDbSet<AgentRun>();
    public DbSet<AgentStageRun> AgentStageRuns { get; } = new InMemoryDbSet<AgentStageRun>();
    public DbSet<ProjectStageTransition> ProjectStageTransitions { get; } = new InMemoryDbSet<ProjectStageTransition>();
    public DbSet<ProjectStageReview> ProjectStageReviews { get; } = new InMemoryDbSet<ProjectStageReview>();
    public DbSet<ProjectNotification> ProjectNotifications { get; } = new InMemoryDbSet<ProjectNotification>();
    public DbSet<ArtifactWorkRecord> ArtifactWorkRecords { get; } = new InMemoryDbSet<ArtifactWorkRecord>();
    public DbSet<ArtifactFingerprint> ArtifactFingerprints { get; } = new InMemoryDbSet<ArtifactFingerprint>();
    public DbSet<AiChunkRun> AiChunkRuns { get; } = new InMemoryDbSet<AiChunkRun>();
    public DbSet<AiChunkRunPart> AiChunkRunParts { get; } = new InMemoryDbSet<AiChunkRunPart>();

    public int SaveCount { get; private set; }

    // Test hook (S1): when set, the next SaveChangesAsync throws it instead of saving - e.g. a
    // DbUpdateConcurrencyException standing in for SQL Server refusing a stale version token. It only
    // simulates the exception: rows added before the save are still in the lists (nothing here models
    // a transaction), so tests assert on the save calls, not on rolled-back rows.
    public Exception? FailNextSave { get; set; }
    public int FailedSaveCount { get; private set; }

    // S1 - the artifacts MarkArtifactForConcurrencyCheck was asked to check, in call order. Recording
    // only: nothing here is checked or versioned, so a test can assert that a service requested a check,
    // not that EF Core or SQL Server enforces one (MigrationModelConsistencyTests inspects the real
    // UPDATE's shape offline; a real two-connection race is not exercised by these tests).
    public List<Guid> ConcurrencyChecks { get; } = new();

    public void MarkArtifactForConcurrencyCheck(Artifact artifact) => ConcurrencyChecks.Add(artifact.Id);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextSave is { } failure)
        {
            FailNextSave = null;
            FailedSaveCount++;
            return Task.FromException<int>(failure);
        }

        SaveCount++;
        return Task.FromResult(0);
    }

    public static List<T> Rows<T>(DbSet<T> set) where T : class => ((InMemoryDbSet<T>)set).Rows;
}

public sealed class InMemoryDbSet<T> : DbSet<T>, IQueryable<T> where T : class
{
    public List<T> Rows { get; } = new();

    private IQueryable<T> Query => new InMemoryAsyncQueryable<T>(Rows);

    public override IEntityType EntityType => throw new NotSupportedException();

    public override EntityEntry<T> Add(T entity)
    {
        Rows.Add(entity);
        return null!;
    }

    public override void AddRange(params T[] entities) => Rows.AddRange(entities);

    public override void AddRange(IEnumerable<T> entities) => Rows.AddRange(entities);

    // Removes at once, like Add; a failed save does not put the rows back (no transaction is modelled).
    public override EntityEntry<T> Remove(T entity)
    {
        Rows.Remove(entity);
        return null!;
    }

    public override void RemoveRange(params T[] entities) => RemoveRange((IEnumerable<T>)entities);

    public override void RemoveRange(IEnumerable<T> entities)
    {
        foreach (var entity in entities.ToList())
        {
            Rows.Remove(entity);
        }
    }

    Type IQueryable.ElementType => typeof(T);
    Expression IQueryable.Expression => Query.Expression;
    IQueryProvider IQueryable.Provider => Query.Provider;
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => Rows.ToList().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => Rows.ToList().GetEnumerator();

    public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new InMemoryAsyncEnumerator<T>(Rows.ToList().GetEnumerator());
}

internal sealed class InMemoryAsyncQueryable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    public InMemoryAsyncQueryable(IEnumerable<T> rows) : base(rows) { }

    public InMemoryAsyncQueryable(Expression expression) : base(expression) { }

    IQueryProvider IQueryable.Provider => new InMemoryAsyncQueryProvider(this);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new InMemoryAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());
}

internal sealed class InMemoryAsyncQueryProvider(IQueryProvider inner) : IAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression) =>
        (IQueryable)Activator.CreateInstance(
            typeof(InMemoryAsyncQueryable<>).MakeGenericType(expression.Type.GetGenericArguments().Last()), expression)!;

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new InMemoryAsyncQueryable<TElement>(expression);

    public object? Execute(Expression expression) => inner.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);

    // EF's async operators ask for Task<TValue>; run the synchronous LINQ-to-objects query and wrap it.
    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var valueType = typeof(TResult).GetGenericArguments()[0];
        var value = typeof(IQueryProvider).GetMethods()
            .Single(m => m.Name == nameof(IQueryProvider.Execute) && m.IsGenericMethod)
            .MakeGenericMethod(valueType)
            .Invoke(inner, [expression]);
        return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(valueType).Invoke(null, [value])!;
    }
}

internal sealed class InMemoryAsyncEnumerator<T>(IEnumerator<T> inner) : IAsyncEnumerator<T>
{
    public T Current => inner.Current;

    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(inner.MoveNext());

    public ValueTask DisposeAsync()
    {
        inner.Dispose();
        return ValueTask.CompletedTask;
    }
}
