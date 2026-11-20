using AiReap.Application.Common;
using AiReap.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Artifacts;

// Saves what `prepare` adds to the context in ONE SaveChangesAsync, so artifacts, their versions,
// fingerprints, links, newly recorded business objectives and audit rows are written together or
// not at all. A save that loses a race on a unique index - an artifact code another run took
// (ArtifactCodeGenerator reads the highest code, it does not lock it), or an artifact fingerprint
// another run just recorded (ArtifactFingerprints) - or that was chosen as a deadlock victim, is
// undone in the context and `prepare` runs again against the current database: fresh codes, and
// duplicates skipped. After MaxSaveAttempts it is refused (409) with nothing saved. A concurrency
// conflict (an artifact's CurrentVersion or RowVersion token no longer matching, see ArtifactConcurrency)
// is not retried; it is rethrown for the caller after the context is restored.
public static class ArtifactPersistence
{
    public const int MaxSaveAttempts = 3;

    public static async Task SaveAsync(IAiReapDbContext db, Func<CancellationToken, Task> prepare, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await prepare(cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                Undo(db);
                throw;
            }
            catch (Exception ex) when (IsRetryableSaveRace(ex))
            {
                Undo(db);
                if (attempt >= MaxSaveAttempts)
                {
                    throw new ConflictException(
                        "Requirements for this source were being saved by another run at the same time. Nothing from this run was saved; try again.");
                }
            }
            catch (DbUpdateException)
            {
                Undo(db);
                throw;
            }
        }
    }

    // SQL Server 2601 (unique index) / 2627 (unique or primary key constraint), read without a
    // provider dependency in the Application layer.
    // A unique-index race or a deadlock, as thrown by SaveChangesAsync: EF's SQL Server execution
    // strategy wraps a deadlock (a transient error) in an InvalidOperationException.
    public static bool IsRetryableSaveRace(Exception ex) =>
        AsDbUpdate(ex) is { } update && update is not DbUpdateConcurrencyException && (IsUniqueViolation(update) || IsDeadlock(update));

    public static DbUpdateException? AsDbUpdate(Exception ex) =>
        ex as DbUpdateException ?? (ex is InvalidOperationException { InnerException: DbUpdateException inner } ? inner : null);

    public static bool IsUniqueViolation(DbUpdateException ex) => SqlErrorNumber(ex) is 2601 or 2627;

    // A unique violation on one named index: SQL Server names the index in the error message.
    public static bool IsUniqueViolationOn(DbUpdateException ex, string indexName) =>
        IsUniqueViolation(ex) && ex.InnerException!.Message.Contains(indexName, StringComparison.Ordinal);

    // SQL Server 1205: this save was chosen as a deadlock victim - concurrent saves inserting into
    // the same unique indexes can deadlock. Its transaction was rolled back entirely, so preparing
    // and saving again is safe.
    public static bool IsDeadlock(DbUpdateException ex) => SqlErrorNumber(ex) == 1205;

    private static int? SqlErrorNumber(DbUpdateException ex) =>
        ex.InnerException is { } inner && inner.GetType().GetProperty("Number")?.GetValue(inner) is int number ? number : null;

    // Back to the state before `prepare`: added entities detached, modified ones restored.
    public static void Undo(IAiReapDbContext db)
    {
        if (db is not DbContext context)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }
}
