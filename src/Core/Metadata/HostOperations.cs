using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>
/// Host resolution is per case (design record, "Data model"): the same hostname in two cases is two rows; within a
/// case the lowercased name is unique and <see cref="GetOrCreateAsync"/> returns the existing row. No global registry.
/// </summary>
public static class HostOperations
{
    /// <summary>Trimmed and lowercased, as the pipeline writes the <c>host</c> column.</summary>
    public static string Normalise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>The case's host of that name, created when missing. Safe under a concurrent create of the same name (the unique index decides).</summary>
    public static async Task<Host> GetOrCreateAsync(MetadataDb db, long caseId, string name, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var normalised = Normalise(name);
        if (normalised.Length == 0)
        {
            throw new ArgumentException("a host name is required", nameof(name));
        }

        var existing = await FindAsync(db, caseId, normalised, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var host = new Host { CaseId = caseId, Name = normalised, CreatedAt = nowUtc };
        db.Hosts.Add(host);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch (DbUpdateException)
        {
            // Lost the race (or the case does not exist): forget our row and take the winner's, else let the error through.
            db.Entry(host).State = EntityState.Detached;
            existing = await FindAsync(db, caseId, normalised, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    private static Task<Host?> FindAsync(MetadataDb db, long caseId, string normalised, CancellationToken cancellationToken) =>
        db.Hosts.FirstOrDefaultAsync(h => h.CaseId == caseId && h.Name == normalised, cancellationToken);
}
