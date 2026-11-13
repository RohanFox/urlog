using System.Globalization;
using System.Net;
using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Intake;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Models.Expiration;

namespace Urlog.Api.Uploads;

/// <summary>
/// <c>/api/v1/cases/{caseId}/uploads</c>: resumable upload, tus 1.0 with the creation, termination, checksum and
/// expiration extensions (design record, "Intake", Web upload). Owners and read-write members upload; the case
/// ACL, the CSRF check and the store identity gate run before anything here. An upload belongs to the user who
/// created it and is 404 to everyone else.
/// <para>
/// The intake gate decides at creation, before a byte is accepted. The last chunk hands the file to intake inside
/// its request, so its 204 says the file is recorded; <c>GET .../uploads/{id}</c> says what became of it to a client
/// that lost that answer. The request body limit is lifted for this route alone.
/// </para>
/// </summary>
public static class UploadEndpoints
{
    public const string OverrideHeader = "X-HTTP-Method-Override";

    private static readonly string[] Methods = ["POST", "HEAD", "PATCH", "DELETE", "OPTIONS"];

    public static void Map(RouteGroupBuilder oneCase)
    {
        ArgumentNullException.ThrowIfNull(oneCase);
        oneCase.MapTus("/uploads", ConfigureAsync)
            .RequireCaseRole(MemberRole.Rw)
            .WithMetadata(new HttpMethodMetadata(Methods));
        oneCase.MapGet("/uploads/{uploadId}", StatusAsync).RequireCaseRole(MemberRole.Rw).WithName("GetUpload");
    }

    private static Task<DefaultTusConfiguration> ConfigureAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var options = services.GetRequiredService<IOptions<UrlogOptions>>().Value.Upload;
        var folders = services.GetRequiredService<UploadFolders>();
        var clock = services.GetRequiredService<TimeProvider>();
        var session = http.Features.GetRequiredFeature<SessionContext>();
        var @case = http.Features.GetRequiredFeature<CaseContext>().Case;

        // A file of any size comes in chunks through this route; every other route keeps the server's limit.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }

        var store = new UploadStore(folders, @case.Id, session.User.Id, session.User.Name, clock, options.Expiry);
        return Task.FromResult(new DefaultTusConfiguration
        {
            Store = store,
            MaxAllowedUploadSizeInBytesLong = options.MaxFileBytes,
            Expiration = new SlidingExpiration(options.Expiry),
            AllowedExtensions = new TusExtensions(TusExtensions.Creation, TusExtensions.Termination, TusExtensions.Checksum, TusExtensions.Expiration),
            // The store reads through the stream guards; one megabyte at a time.
            UsePipelinesIfAvailable = false,
            Events = new Events
            {
                OnAuthorizeAsync = context => AuthorizeAsync(context, @case),
                OnBeforeCreateAsync = context => BeforeCreateAsync(context, store, folders, clock),
                OnBeforeDeleteAsync = context => BeforeDeleteAsync(context, store),
                OnFileCompleteAsync = context => services.GetRequiredService<UploadFinaliser>().FinaliseAsync(@case.Id, context.FileId),
            },
        });
    }

    private static Task AuthorizeAsync(AuthorizeContext context, CaseRecord @case)
    {
        // The library would act on the method this header names, and the CSRF check has seen only the real one.
        if (context.HttpContext.Request.Headers.ContainsKey(OverrideHeader))
        {
            context.FailRequest(HttpStatusCode.BadRequest, $"{OverrideHeader} is not accepted");
        }
        else if (context.Intent is IntentType.CreateFile or IntentType.WriteFile && @case.State != CaseState.Active)
        {
            context.FailRequest(HttpStatusCode.Conflict, Closed(@case));
        }

        return Task.CompletedTask;
    }

    private static async Task BeforeCreateAsync(BeforeCreateContext context, UploadStore store, UploadFolders folders, TimeProvider clock)
    {
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, value) in context.Metadata)
        {
            values[key] = value.GetBytes();
        }

        var now = clock.GetUtcNow();
        if (!UploadFields.TryParse(values, now.Year, out var fields, out var error))
        {
            context.FailRequest(HttpStatusCode.BadRequest, error);
            return;
        }

        var services = context.HttpContext.RequestServices;
        var caseId = context.HttpContext.Features.GetRequiredFeature<CaseContext>().Case.Id;
        Directory.CreateDirectory(folders.Root);
        // The volume owes the uploads in flight their remaining bytes already.
        var incoming = context.UploadLength + await folders.OutstandingAsync(now, context.CancellationToken).ConfigureAwait(false);
        var answer = await services.GetRequiredService<IntakeGate>()
            .CheckAsync(services.GetRequiredService<MetadataDb>(), caseId, folders.Root, incoming, context.CancellationToken).ConfigureAwait(false);
        if (!answer.Accepted)
        {
            context.FailRequest(answer.Refusal == IntakeGateRefusal.Case ? HttpStatusCode.Conflict : HttpStatusCode.InsufficientStorage, answer.Reason);
            return;
        }

        store.Fields = fields;
    }

    private static async Task BeforeDeleteAsync(BeforeDeleteContext context, UploadStore store)
    {
        var info = await store.FindAsync(context.FileId, context.CancellationToken).ConfigureAwait(false);
        if (info?.State == UploadState.Done)
        {
            context.FailRequest(HttpStatusCode.Conflict, string.Create(CultureInfo.InvariantCulture, $"the upload is recorded as file {info.FileId}; a recorded file is not removed here"));
        }
        else if (info?.State == UploadState.Finalising)
        {
            context.FailRequest(HttpStatusCode.Conflict, "the upload is being recorded");
        }
    }

    private static async Task<Results<Ok<UploadStatusResponse>, ProblemHttpResult>> StatusAsync(string uploadId, HttpContext http, CurrentUser me, UploadFolders folders, TimeProvider clock, IOptions<UrlogOptions> options, CancellationToken cancellationToken)
    {
        var caseId = http.Features.GetRequiredFeature<CaseContext>().Case.Id;
        var store = new UploadStore(folders, caseId, me.Id, me.Name, clock, options.Value.Upload.Expiry);
        if (await store.FindAsync(uploadId, cancellationToken).ConfigureAwait(false) is not { } info)
        {
            return TypedResults.Problem(Problems.NotFound("no such upload"));
        }

        return TypedResults.Ok(new UploadStatusResponse(info.Id, info.State, store.Offset(info), info.Length, info.Fields.Name, info.CreatedAt, info.ExpiresAt, info.FileId, info.Sha256, info.JobId, info.Message));
    }

    private static string Closed(CaseRecord @case) =>
        string.Create(CultureInfo.InvariantCulture, $"case {@case.Id} is {Names.Of(@case.State)}; only an active case takes files");
}
