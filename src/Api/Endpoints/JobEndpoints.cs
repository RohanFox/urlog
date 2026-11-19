using System.Globalization;
using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Api.Endpoints;

/// <summary>
/// <c>/api/v1/cases/{caseId}/jobs</c>: the ingest queue of a case, newest first, with the progress of a job that
/// runs and the reason of one that failed, word for word as the pipeline reported it. A read-write member
/// cancels a job that waits and queues the work of a failed or cancelled one again; both are on the file's trail.
/// </summary>
public static class JobEndpoints
{
    public static void Map(RouteGroupBuilder oneCase)
    {
        ArgumentNullException.ThrowIfNull(oneCase);
        oneCase.MapGet("/jobs", ListAsync).RequireCaseRole(MemberRole.Ro).WithName("ListCaseJobs");
        oneCase.MapGet("/jobs/{jobId:long}", GetAsync).RequireCaseRole(MemberRole.Ro).WithName("GetCaseJob");
        oneCase.MapPost("/jobs/{jobId:long}/cancel", CancelAsync).RequireCaseRole(MemberRole.Rw).WithName("CancelCaseJob");
        oneCase.MapPost("/jobs/{jobId:long}/retry", RetryAsync).RequireCaseRole(MemberRole.Rw).WithName("RetryCaseJob");
    }

    /// <summary>Filters: <c>state</c>, <c>file</c>. Newest first; <c>before</c> is the <c>next_after</c> of the page before.</summary>
    private static async Task<Ok<JobPageResponse>> ListAsync(long caseId, HttpRequest request, MetadataDb db, CancellationToken cancellationToken)
    {
        var query = request.Query;
        var limit = Number(query, "limit") ?? FileEndpoints.DefaultPage;
        if (limit is < 1 or > FileEndpoints.MaxPage)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"limit: 1 to {FileEndpoints.MaxPage}"));
        }

        var jobs = db.Jobs.AsNoTracking().Where(j => j.CaseId == caseId);
        if (Number(query, "before") is { } before)
        {
            jobs = jobs.Where(j => j.Id < before);
        }

        if (Number(query, "file") is { } file)
        {
            jobs = jobs.Where(j => j.FileId == file);
        }

        if (query.TryGetValue("state", out var stateText) && stateText.ToString() is { Length: > 0 } text)
        {
            var state = FileEndpoints.Parse<JobState>(text, "state");
            jobs = jobs.Where(j => j.State == state);
        }

        var page = await jobs.OrderByDescending(j => j.Id).Take((int)limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var more = page.Count > limit;
        if (more)
        {
            page.RemoveAt(page.Count - 1);
        }

        var fileIds = page.Select(j => j.FileId).Distinct().ToList();
        var sizes = await db.Files.AsNoTracking().Where(f => fileIds.Contains(f.Id)).Select(f => new { f.Id, f.Size }).ToDictionaryAsync(f => f.Id, f => f.Size, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new JobPageResponse([.. page.Select(j => CaseViews.Job(j, sizes.TryGetValue(j.FileId, out var size) ? size : null))], more ? page[^1].Id : null));
    }

    private static async Task<Results<Ok<JobResponse>, ProblemHttpResult>> GetAsync(long caseId, long jobId, MetadataDb db, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.CaseId == caseId, cancellationToken).ConfigureAwait(false);
        return job is null
            ? TypedResults.Problem(Problems.NotFound(string.Create(CultureInfo.InvariantCulture, $"no job {jobId} in case {caseId}")))
            : TypedResults.Ok(await ViewAsync(db, job, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Ok<JobResponse>> CancelAsync(long caseId, long jobId, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var job = await JobOperations.CancelAsync(db, store.Audit, me.Actor, caseId, jobId, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(await ViewAsync(db, job, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Accepted<JobResponse>> RetryAsync(long caseId, long jobId, CurrentUser me, MetadataDb db, MetadataStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        var job = await JobOperations.RetryAsync(db, store.Audit, me.Actor, caseId, jobId, clock, cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted(string.Create(CultureInfo.InvariantCulture, $"/api/v1/cases/{caseId}/jobs/{job.Id}"), await ViewAsync(db, job, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<JobResponse> ViewAsync(MetadataDb db, Job job, CancellationToken cancellationToken)
    {
        var size = await db.Files.AsNoTracking().Where(f => f.Id == job.FileId).Select(f => (long?)f.Size).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return CaseViews.Job(job, size);
    }

    private static long? Number(IQueryCollection query, string name) =>
        !query.TryGetValue(name, out var value) || value.ToString() is not { Length: > 0 } text ? null
        : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number
        : throw new ArgumentException($"{name}: a whole number");
}
