using AiReap.Application.Files;
using AiReap.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiReap.Infrastructure.Files;

// Keeps the original-document store and the database in step (docs/SOURCE_DOCUMENT_RETENTION.md):
//   * with OriginalDocuments:RetentionDays set, deletes originals older than that - the row, then the
//     file - and leaves the source, its text and its questions as they are;
//   * removes orphans: stored files no source references (an upload whose save failed and whose
//     clean-up failed too, a project delete whose file removal failed), once older than the grace period.
// Runs every CleanupIntervalMinutes (0 = off). Failures are logged by source ID or storage key only,
// never with document content, and never stop the API.
public class OriginalDocumentJanitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOriginalDocumentStore _store;
    private readonly IOptions<OriginalDocumentOptions> _options;
    private readonly ILogger<OriginalDocumentJanitor> _logger;

    public OriginalDocumentJanitor(
        IServiceScopeFactory scopes, IOriginalDocumentStore store, IOptions<OriginalDocumentOptions> options, ILogger<OriginalDocumentJanitor> logger)
    {
        _scopes = scopes;
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Value.CleanupIntervalMinutes;
        if (interval <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(interval));
        do
        {
            try
            {
                await SweepAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // E.g. the RequirementSourceFiles migration not applied yet: nothing is removed.
                _logger.LogWarning(ex, "Original-document clean-up did not run; it will be retried.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SweepAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiReapDbContext>();
        var options = _options.Value;

        if (options.RetentionDays is int days && days > 0)
        {
            var cutoff = nowUtc.AddDays(-days);
            var expired = await db.RequirementSourceFiles.Where(f => f.CreatedAt < cutoff).ToListAsync(cancellationToken);
            if (expired.Count > 0)
            {
                db.RequirementSourceFiles.RemoveRange(expired);
                await db.SaveChangesAsync(cancellationToken);
                foreach (var file in expired)
                {
                    await _store.DeleteAsync(file.StorageKey, cancellationToken);
                }

                _logger.LogInformation("Deleted {Count} original documents older than the {Days}-day retention period.", expired.Count, days);
            }
        }

        var removed = await OrphanSweep.RunAsync(
            _store,
            OwnerOf(db),
            async (keys, ct) =>
            {
                // In batches: SQL Server takes at most 2,100 parameters in one query.
                var referenced = new HashSet<string>(StringComparer.Ordinal);
                foreach (var batch in keys.Chunk(500))
                {
                    referenced.UnionWith(await db.RequirementSourceFiles.Where(f => batch.Contains(f.StorageKey)).Select(f => f.StorageKey).ToListAsync(ct));
                }

                return referenced;
            },
            nowUtc.AddMinutes(-Math.Max(0, options.OrphanGraceMinutes)),
            cancellationToken);
        if (removed is null)
        {
            _logger.LogWarning("The original-document directory belongs to another database, so orphaned files were not removed. Give each database its own OriginalDocuments:RootPath.");
        }
        else if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} orphaned original documents.", removed);
        }
    }

    // The database this API writes to, as a store owner: server and database name, hashed (the
    // marker file then says nothing about the server).
    public static string OwnerOf(AiReapDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var identity = $"{connection.DataSource}|{connection.Database}".ToLowerInvariant();
        return OriginalDocumentFormats.Sha256(System.Text.Encoding.UTF8.GetBytes(identity));
    }
}
