using System.Globalization;
using System.Text.Json;
using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Names = Urlog.Core.Metadata.Names;

namespace Urlog.Api.Endpoints;

/// <summary>
/// <c>/api/v1/cases/{caseId}/files</c>: the file records of a case in pages, one record in full, and the changes
/// a person makes after intake: host, zone and year, the override, and the same for many files in one call.
/// No route returns a byte of a file. A change names the version it was made against in <c>If-Match</c>. A change
/// to one file is made at once and answers 202 with the parse it queued. A bulk change answers first with the
/// number of files and bytes it would have parsed again and a digest of its selection, and changes nothing; the
/// same request with <c>?confirm=true</c> and that digest in <c>If-Match</c> makes it.
/// </summary>
public static class FileEndpoints
{
    public const int DefaultPage = 100;
    public const int MaxPage = 500;

    public static void Map(RouteGroupBuilder oneCase)
    {
        ArgumentNullException.ThrowIfNull(oneCase);
        oneCase.MapGet("/files", ListAsync).RequireCaseRole(MemberRole.Ro).WithName("ListCaseFiles");
        oneCase.MapGet("/files/{fileId:long}", GetAsync).RequireCaseRole(MemberRole.Ro).WithName("GetCaseFile");
        oneCase.MapPatch("/files/{fileId:long}", UpdateAsync).RequireCaseRole(MemberRole.Rw).WithName("UpdateCaseFile");
        oneCase.MapPut("/files/{fileId:long}/override", SetOverrideAsync).RequireCaseRole(MemberRole.Rw).WithName("SetCaseFileOverride");
        oneCase.MapDelete("/files/{fileId:long}/override", ClearOverrideAsync).RequireCaseRole(MemberRole.Rw).WithName("ClearCaseFileOverride");
        oneCase.MapPost("/files/assign-host", AssignHostAsync).RequireCaseRole(MemberRole.Rw).WithName("AssignCaseFilesHost");
        oneCase.MapPost("/files/override", BulkOverrideAsync).RequireCaseRole(MemberRole.Rw).WithName("OverrideCaseFiles");
        oneCase.MapPost("/files/redetect", RedetectAsync).RequireCaseRole(MemberRole.Rw).WithName("RedetectCaseFiles");
    }

    /// <summary>
    /// Filters: <c>host</c> (id), <c>unassigned</c>, <c>state</c>, <c>kind</c>, <c>source</c> (detected),
    /// <c>parent</c> (the members of one archive), <c>pattern</c> (a path template), <c>extension</c>,
    /// <c>stale</c> (detection), <c>q</c> (a part of the name). Pages by <c>after</c> and <c>limit</c>.
    /// </summary>
    private static async Task<Ok<FilePageResponse>> ListAsync(long caseId, HttpRequest request, MetadataDb db, SourceRules rules, CancellationToken cancellationToken)
    {
        var query = request.Query;
        var after = Long(query, "after") ?? 0;
        var limit = (int)(Long(query, "limit") ?? DefaultPage);
        if (limit is < 1 or > MaxPage)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"limit: 1 to {MaxPage}"));
        }

        var files = db.Files.AsNoTracking().Where(f => f.CaseId == caseId && f.Id > after);
        if (Long(query, "host") is { } host)
        {
            files = files.Where(f => f.HostId == host);
        }

        if (Flag(query, "unassigned"))
        {
            files = files.Where(f => f.HostId == null);
        }

        if (Text(query, "state") is { } stateText)
        {
            var state = Parse<FileState>(stateText, "state");
            files = files.Where(f => f.State == state);
        }

        if (Text(query, "kind") is { } kindText)
        {
            var kind = Parse<FileKind>(kindText, "kind");
            files = files.Where(f => f.DetectedKind == kind);
        }

        if (Text(query, "source") is { } source)
        {
            files = files.Where(f => f.DetectedSource == source);
        }

        if (Long(query, "parent") is { } parent)
        {
            files = files.Where(f => f.ParentFileId == parent);
        }

        if (Text(query, "q") is { } part)
        {
            files = files.Where(f => f.OriginalName.Contains(part) || (f.ArchivePath != null && f.ArchivePath.Contains(part)));
        }

        // What the store cannot filter runs over the rows in id order until the page is full.
        var selection = new FileSelection { Pattern = Text(query, "pattern"), Extension = Text(query, "extension"), StaleDetection = Flag(query, "stale") };
        var inMemory = selection.Pattern is not null || selection.Extension is not null || selection.StaleDetection;
        var page = new List<EvidenceFile>(limit + 1);
        if (inMemory)
        {
            var matches = FileSettingsOperations.Compile(selection, rules);
            await foreach (var file in files.OrderBy(f => f.Id).AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (matches(file))
                {
                    page.Add(file);
                    if (page.Count > limit)
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            page = await files.OrderBy(f => f.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var more = page.Count > limit;
        if (more)
        {
            page.RemoveAt(page.Count - 1);
        }

        var hosts = await CaseViews.HostsAsync(db, caseId, cancellationToken).ConfigureAwait(false);
        var active = await CaseViews.ActiveJobsAsync(db, [.. page.Select(f => f.Id)], cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new FilePageResponse([.. page.Select(f => CaseViews.File(f, hosts, active, rules))], more ? page[^1].Id : null));
    }

    private static async Task<Results<Ok<FileDetailResponse>, ProblemHttpResult>> GetAsync(long caseId, long fileId, HttpContext http, MetadataDb db, SourceRules rules, CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.CaseId == caseId, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return TypedResults.Problem(Problems.NotFound(string.Create(CultureInfo.InvariantCulture, $"no file {fileId} in case {caseId}")));
        }

        var jobs = await db.Jobs.AsNoTracking().Where(j => j.FileId == fileId).OrderByDescending(j => j.Id).Take(20).ToListAsync(cancellationToken).ConfigureAwait(false);
        http.Response.Headers.ETag = Preconditions.Tag(file.SettingsVersion);
        return TypedResults.Ok(new FileDetailResponse(
            await CaseViews.FileAsync(db, file, rules, cancellationToken).ConfigureAwait(false),
            CaseViews.Json(file.Detection), CaseViews.Json(file.SeenPaths), file.UnreadableReason, file.StatedSize,
            file.TruncatedDecodedOffset, file.TruncatedStoredOffset, file.TrailingBytes, file.TrailingBytesInexact, CaseViews.Utc(file.VerifiedAt),
            [.. jobs.Select(j => CaseViews.Job(j, file.Size))]));
    }

    private static async Task<IResult> UpdateAsync(long caseId, long fileId, JsonElement body, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, SourceRules rules, TimeProvider clock, CancellationToken cancellationToken)
    {
        Body.Object(body);
        var version = Preconditions.Version(http, $"file {fileId}");
        var edit = new FileEdit(Body.Text(body, "host"), Body.Flag(body, "confirm_host"), Body.Text(body, "timezone"), Body.Number(body, "year"));
        var change = await FileSettingsOperations.UpdateAsync(db, store.Audit, me.Actor, rules, caseId, fileId, version, edit, clock, cancellationToken).ConfigureAwait(false);
        return await AnswerAsync(http, db, rules, caseId, change, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> SetOverrideAsync(long caseId, long fileId, JsonElement body, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, SourceRules rules, TimeProvider clock, CancellationToken cancellationToken)
    {
        Body.Object(body);
        var version = Preconditions.Version(http, $"file {fileId}");
        var source = Body.OptionalText(body, "source");
        var options = Body.Options(body, "options").Value;
        if (string.IsNullOrWhiteSpace(source) && options is not { Count: > 0 })
        {
            throw new ArgumentException("source, options: name a source, give options, or both; DELETE clears the override");
        }

        var change = await FileSettingsOperations.OverrideAsync(db, store.Audit, me.Actor, rules, caseId, fileId, version, source, options, clock, cancellationToken).ConfigureAwait(false);
        return await AnswerAsync(http, db, rules, caseId, change, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ClearOverrideAsync(long caseId, long fileId, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, SourceRules rules, TimeProvider clock, CancellationToken cancellationToken)
    {
        var version = Preconditions.Version(http, $"file {fileId}");
        var change = await FileSettingsOperations.OverrideAsync(db, store.Audit, me.Actor, rules, caseId, fileId, version, source: null, options: null, clock, cancellationToken).ConfigureAwait(false);
        return await AnswerAsync(http, db, rules, caseId, change, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> AssignHostAsync(long caseId, JsonElement body, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, SourceRules rules, TimeProvider clock, CancellationToken cancellationToken)
    {
        Body.Object(body);
        var confirm = Preconditions.Confirmed(http);
        var selection = Walk(body, new FileSelection { Pattern = Body.RequiredText(body, "pattern") });
        var outcome = await FileSettingsOperations.AssignHostAsync(db, store.Audit, me.Actor, rules, caseId, selection, Body.RequiredText(body, "host"), confirm, confirm ? Preconditions.Digest(http) : null, clock, cancellationToken).ConfigureAwait(false);
        return Answer(http, caseId, outcome);
    }

    private static async Task<IResult> BulkOverrideAsync(long caseId, JsonElement body, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, SourceRules rules, TimeProvider clock, CancellationToken cancellationToken)
    {
        Body.Object(body);
        var confirm = Preconditions.Confirmed(http);
        if (!body.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("match: an object that says which files (extension, detected_source, detected_kind, detected_layout, host)");
        }

        var selection = Walk(body, new FileSelection
        {
            Extension = Body.OptionalText(match, "extension"),
            DetectedSource = Body.OptionalText(match, "detected_source"),
            DetectedKind = Body.OptionalText(match, "detected_kind") is { } kind ? Parse<FileKind>(kind, "detected_kind") : null,
            DetectedLayout = Body.OptionalText(match, "detected_layout"),
            HostId = Body.OptionalLong(match, "host"),
        });
        var outcome = await FileSettingsOperations.BulkOverrideAsync(db, store.Audit, me.Actor, rules, caseId, selection, Body.OptionalText(body, "source"), Body.Options(body, "options").Value, confirm, confirm ? Preconditions.Digest(http) : null, clock, cancellationToken).ConfigureAwait(false);
        return Answer(http, caseId, outcome);
    }

    /// <summary>
    /// Asks for files to be detected again: <c>{"stale": true}</c> for every file whose detection record was made
    /// by another detector version or source list, or <c>file_ids</c>. The job runner does the detecting.
    /// </summary>
    private static async Task<IResult> RedetectAsync(long caseId, JsonElement body, HttpContext http, CurrentUser me, MetadataDb db, MetadataStore store, SourceRules rules, TimeProvider clock, CancellationToken cancellationToken)
    {
        Body.Object(body);
        var confirm = Preconditions.Confirmed(http);
        var selection = Walk(body, new FileSelection { StaleDetection = Body.Flag(body, "stale"), FileIds = Body.Ids(body, "file_ids") });
        var outcome = await FileSettingsOperations.RedetectAsync(db, store.Audit, me.Actor, rules, caseId, selection, confirm, confirm ? Preconditions.Digest(http) : null, clock, cancellationToken).ConfigureAwait(false);
        return Answer(http, caseId, outcome);
    }

    /// <summary><c>after_id</c> and <c>limit</c> in the body turn a bulk call into one step of a walk through a large selection.</summary>
    private static FileSelection Walk(JsonElement body, FileSelection selection)
    {
        var after = Body.OptionalLong(body, "after_id");
        var limit = Body.OptionalLong(body, "limit");
        return selection with { AfterId = after ?? 0, Limit = (int)Math.Clamp(limit ?? FileSettingsOperations.BulkLimit, 0, int.MaxValue), Paged = after is not null || limit is not null };
    }

    private static async Task<IResult> AnswerAsync(HttpContext http, MetadataDb db, SourceRules rules, long caseId, FileChange change, CancellationToken cancellationToken)
    {
        var response = new FileChangeResponse(change.Changed, await CaseViews.FileAsync(db, change.File, rules, cancellationToken).ConfigureAwait(false), CaseViews.JobRef(change.Job), new ReparseResponse(change.Cost.Files, change.Cost.Bytes));
        http.Response.Headers.ETag = Preconditions.Tag(change.File.SettingsVersion);
        return change.Job is { } job
            ? TypedResults.Accepted(string.Create(CultureInfo.InvariantCulture, $"/api/v1/cases/{caseId}/jobs/{job.Id}"), response)
            : TypedResults.Ok(response);
    }

    private static IResult Answer(HttpContext http, long caseId, BulkOutcome outcome)
    {
        var response = new BulkResponse(outcome.Applied, outcome.Matched, outcome.Digest, new ReparseResponse(outcome.Cost.Files, outcome.Cost.Bytes), outcome.Changed, outcome.Jobs, outcome.Skipped, outcome.NextAfterId);
        if (!outcome.Applied)
        {
            http.Response.Headers.ETag = $"\"{outcome.Digest}\"";
        }

        return outcome.Applied && outcome.Jobs > 0
            ? TypedResults.Accepted(string.Create(CultureInfo.InvariantCulture, $"/api/v1/cases/{caseId}/jobs"), response)
            : TypedResults.Ok(response);
    }

    private static string? Text(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var value) && value.ToString() is { Length: > 0 } text ? text : null;

    private static bool Flag(IQueryCollection query, string name) =>
        Text(query, name) is { } text && (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ? true : string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ? false : throw new ArgumentException($"{name}: true or false"));

    private static long? Long(IQueryCollection query, string name) =>
        Text(query, name) is not { } text ? null
        : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value
        : throw new ArgumentException($"{name}: a whole number");

    internal static TEnum Parse<TEnum>(string text, string name) where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(Names.Of(value), text, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new ArgumentException($"{name}: '{text}' is none of {string.Join(", ", Enum.GetValues<TEnum>().Select(v => Names.Of(v)))}");
    }
}
