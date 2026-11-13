using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Urlog.Api.Uploads;

/// <summary>Where an upload stands. Everything but <see cref="Receiving"/> is set by the step that hands the bytes to intake.</summary>
public enum UploadState
{
    /// <summary>Bytes are still expected.</summary>
    Receiving,

    /// <summary>All bytes are here and intake is at work; a stop in this state is picked up again at the next start.</summary>
    Finalising,

    /// <summary>Recorded as a file of the case.</summary>
    Done,

    /// <summary>The case already holds these bytes; nothing new was recorded and the uploaded copy is gone.</summary>
    Duplicate,

    /// <summary>Intake refused the file (a packet capture); nothing of it is kept.</summary>
    Rejected,

    /// <summary>Intake could not take the file as it is (the case was closed meanwhile); the bytes wait for the sweep.</summary>
    Failed,
}

/// <summary>
/// What is known about one upload, kept beside its bytes as <c>&lt;id&gt;.json</c>. It outlives the bytes until the
/// upload expires, so a client that lost the answer to its last request still learns what became of the file.
/// </summary>
public sealed record UploadInfo
{
    public required string Id { get; init; }

    public required long CaseId { get; init; }

    /// <summary>The declared size in bytes; the offset is the size of the bytes on disk.</summary>
    public required long Length { get; init; }

    /// <summary>The <c>Upload-Metadata</c> header as it was sent, which the protocol hands back on HEAD.</summary>
    public string Metadata { get; init; } = "";

    public required long UserId { get; init; }

    public required string UserName { get; init; }

    public required UploadFields Fields { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public UploadState State { get; init; }

    public long? FileId { get; init; }

    public string? Sha256 { get; init; }

    public long? JobId { get; init; }

    public string? Message { get; init; }
}

/// <summary>The paths of the upload area under the evidence root, and the sidecar's reads and writes.</summary>
public sealed class UploadFolders
{
    /// <summary>Unfinished uploads of a case; the only folder the expiry sweep opens.</summary>
    public const string UploadsFolder = ".uploads";

    /// <summary>Finished uploads of a case, each under its upload id: no name an uploader chose reaches the file system.</summary>
    public const string FinishedFolder = "web";

    private const string InfoSuffix = ".json";
    private const string TempSuffix = ".json.tmp";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = true,
    };

    public UploadFolders(string evidenceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRoot);
        Root = Path.GetFullPath(evidenceRoot);
    }

    public string Root { get; }

    public string CaseDirectory(long caseId) => Path.Combine(Root, caseId.ToString(CultureInfo.InvariantCulture));

    public string Uploads(long caseId) => Path.Combine(CaseDirectory(caseId), UploadsFolder);

    public string Data(long caseId, string id) => Path.Combine(Uploads(caseId), id);

    public string Info(long caseId, string id) => Path.Combine(Uploads(caseId), id + InfoSuffix);

    public string Finished(long caseId, string id) => Path.Combine(CaseDirectory(caseId), FinishedFolder, id);

    /// <summary>The finished file's path as the intake record holds it: relative to the case's folder.</summary>
    public static string StoredPath(string id) => Path.Combine(FinishedFolder, id);

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>An id this server hands out: 32 lowercase hex characters. Anything else never becomes a path.</summary>
    public static bool IsId(string? id)
    {
        if (id is not { Length: 32 })
        {
            return false;
        }

        foreach (var c in id)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The id a file in an uploads folder belongs to (its bytes, its sidecar or a sidecar being written), or null for a file that is not ours.</summary>
    public static string? IdOf(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var id = fileName.EndsWith(TempSuffix, StringComparison.Ordinal) ? fileName[..^TempSuffix.Length]
            : fileName.EndsWith(InfoSuffix, StringComparison.Ordinal) ? fileName[..^InfoSuffix.Length]
            : fileName;
        return IsId(id) ? id : null;
    }

    public async Task<UploadInfo?> ReadAsync(long caseId, string id, CancellationToken cancellationToken = default)
    {
        if (!IsId(id))
        {
            return null;
        }

        var path = Info(caseId, id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using (stream.ConfigureAwait(false))
            {
                var info = await JsonSerializer.DeserializeAsync<UploadInfo>(stream, Json, cancellationToken).ConfigureAwait(false);
                // A sidecar copied in from another case or under another name describes nothing here.
                return info is not null && info.CaseId == caseId && string.Equals(info.Id, id, StringComparison.Ordinal) ? info : null;
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Written beside its target and renamed over it, so a reader sees the old sidecar or the new one.</summary>
    public async Task WriteAsync(UploadInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        Directory.CreateDirectory(Uploads(info.CaseId));
        var path = Info(info.CaseId, info.Id);
        var temp = Path.Combine(Uploads(info.CaseId), info.Id + TempSuffix);
        var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, info, Json, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Removes the bytes and the sidecar of one upload; nothing outside the uploads folder.</summary>
    public void Remove(long caseId, string id)
    {
        if (!IsId(id))
        {
            return;
        }

        File.Delete(Data(caseId, id));
        File.Delete(Info(caseId, id));
        File.Delete(Path.Combine(Uploads(caseId), id + TempSuffix));
    }

    /// <summary>The case folders under the root: the ones whose name is a case id.</summary>
    public IEnumerable<long> Cases()
    {
        if (!Directory.Exists(Root))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            if (long.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var caseId))
            {
                yield return caseId;
            }
        }
    }

    /// <summary>The ids that have a sidecar in the case's uploads folder.</summary>
    public IEnumerable<string> Ids(long caseId)
    {
        var directory = Uploads(caseId);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*" + InfoSuffix))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(InfoSuffix, StringComparison.Ordinal) && IsId(name[..^InfoSuffix.Length]))
            {
                yield return name[..^InfoSuffix.Length];
            }
        }
    }

    /// <summary>The bytes on disk for an upload that is still receiving; 0 when there are none.</summary>
    public long Received(long caseId, string id)
    {
        var data = new FileInfo(Data(caseId, id));
        return data.Exists ? data.Length : 0;
    }

    /// <summary>What the unfinished uploads of every case have still to send: space the volume owes them already.</summary>
    public async Task<long> OutstandingAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        long total = 0;
        foreach (var caseId in Cases())
        {
            foreach (var id in Ids(caseId))
            {
                if (await ReadAsync(caseId, id, cancellationToken).ConfigureAwait(false) is { State: UploadState.Receiving } info && info.ExpiresAt > now)
                {
                    total += Math.Max(0, info.Length - Received(caseId, id));
                }
            }
        }

        return total;
    }
}
