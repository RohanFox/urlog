using System.Buffers;
using System.Security.Cryptography;
using tusdotnet.Interfaces;
using tusdotnet.Models;

namespace Urlog.Api.Uploads;

/// <summary>
/// The disk side of the tus endpoint for one request: the uploads of one case as one user sees them. An upload is
/// its bytes, <c>.uploads/&lt;id&gt;</c>, and its sidecar; an id of another case or another user does not exist here.
/// Bytes go from the request to the file through one pooled buffer, so memory does not grow with the file.
/// <para>
/// The checksum of a chunk is taken from the bytes on disk, after the write: what is verified is what was stored.
/// A chunk that does not match, or that the client did not finish, is cut back to where it began.
/// </para>
/// </summary>
public sealed class UploadStore(UploadFolders folders, long caseId, long userId, string userName, TimeProvider clock, TimeSpan expiry)
    : ITusStore, ITusCreationStore, ITusTerminationStore, ITusChecksumStore, ITusExpirationStore
{
    /// <summary>One megabyte per request in flight, whatever the size of the file.</summary>
    public const int BufferSize = 1 << 20;

    private static readonly string[] Algorithms = ["sha1", "sha256"];

    private long? _chunkStart;

    /// <summary>The checked metadata of the upload this request is about to create; set before <see cref="CreateFileAsync"/>.</summary>
    public UploadFields? Fields { get; set; }

    /// <summary>The upload as this caller may see it: of this case, created by this user, not expired.</summary>
    public async Task<UploadInfo?> FindAsync(string fileId, CancellationToken cancellationToken)
    {
        var info = await folders.ReadAsync(caseId, fileId, cancellationToken).ConfigureAwait(false);
        return info is not null && info.UserId == userId && info.ExpiresAt > clock.GetUtcNow() ? info : null;
    }

    public async Task<bool> FileExistAsync(string fileId, CancellationToken cancellationToken) =>
        await FindAsync(fileId, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<long?> GetUploadLengthAsync(string fileId, CancellationToken cancellationToken) =>
        (await FindAsync(fileId, cancellationToken).ConfigureAwait(false))?.Length;

    public async Task<long> GetUploadOffsetAsync(string fileId, CancellationToken cancellationToken)
    {
        var info = await Required(fileId, cancellationToken).ConfigureAwait(false);
        return Offset(info);
    }

    /// <summary>The bytes on disk while they are still expected or were handed back; the whole length once intake has the file.</summary>
    public long Offset(UploadInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.State is UploadState.Receiving or UploadState.Failed ? folders.Received(info.CaseId, info.Id) : info.Length;
    }

    public async Task<string> CreateFileAsync(long uploadLength, string metadata, CancellationToken cancellationToken)
    {
        var fields = Fields ?? throw new TusStoreException("the upload's metadata was not checked");
        var now = clock.GetUtcNow();
        var info = new UploadInfo
        {
            Id = UploadFolders.NewId(),
            CaseId = caseId,
            Length = uploadLength,
            Metadata = metadata ?? "",
            UserId = userId,
            UserName = userName,
            Fields = fields,
            CreatedAt = now,
            ExpiresAt = now + expiry,
            State = UploadState.Receiving,
        };
        Directory.CreateDirectory(folders.Uploads(caseId));
        var data = new FileStream(folders.Data(caseId, info.Id), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await data.DisposeAsync().ConfigureAwait(false);
        await folders.WriteAsync(info, cancellationToken).ConfigureAwait(false);
        return info.Id;
    }

    public async Task<string> GetUploadMetadataAsync(string fileId, CancellationToken cancellationToken) =>
        (await Required(fileId, cancellationToken).ConfigureAwait(false)).Metadata;

    public async Task<long> AppendDataAsync(string fileId, Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var info = await Required(fileId, cancellationToken).ConfigureAwait(false);
        if (info.State != UploadState.Receiving)
        {
            throw new TusStoreException("the upload takes no more bytes");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long written = 0;
        try
        {
            var data = new FileStream(folders.Data(caseId, fileId), FileMode.Open, FileAccess.Write, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (data.ConfigureAwait(false))
            {
                var start = data.Length;
                _chunkStart = start;
                data.Position = start;
                while (!cancellationToken.IsCancellationRequested)
                {
                    // The library's disconnect and size guards override the array overload of its request stream, so that is the one to call.
#pragma warning disable CA1835
                    var read = await stream.ReadAsync(buffer, 0, BufferSize, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1835
                    if (read == 0)
                    {
                        break;
                    }

                    if (start + written + read > info.Length)
                    {
                        throw new TusStoreException("the request holds more bytes than the upload's length");
                    }

                    // Bytes that arrived are stored even when the client has gone since: the next request goes on behind them.
                    await data.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                    written += read;
                }

                await data.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (written > 0)
        {
            // The 24 hours count from the last byte received.
            await folders.WriteAsync(info with { ExpiresAt = clock.GetUtcNow() + expiry }, CancellationToken.None).ConfigureAwait(false);
        }

        return written;
    }

    public async Task DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        await Required(fileId, cancellationToken).ConfigureAwait(false);
        folders.Remove(caseId, fileId);
    }

    public Task<IEnumerable<string>> GetSupportedAlgorithmsAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<string>>(Algorithms);

    public async Task<bool> VerifyChecksumAsync(string fileId, string algorithm, byte[] checksum, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(checksum);
        await Required(fileId, CancellationToken.None).ConfigureAwait(false);
        if (_chunkStart is not { } start)
        {
            return false;
        }

        var name = algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase) ? HashAlgorithmName.SHA256
            : algorithm.Equals("sha1", StringComparison.OrdinalIgnoreCase) ? HashAlgorithmName.SHA1
            : throw new TusStoreException("unsupported checksum algorithm");
        var data = new FileStream(folders.Data(caseId, fileId), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (data.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using var hash = IncrementalHash.CreateHash(name);
                data.Position = start;
                int read;
                while ((read = await data.ReadAsync(buffer.AsMemory(0, BufferSize), CancellationToken.None).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }

                if (CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), checksum))
                {
                    return true;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            data.SetLength(start);
            await data.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>The expiry is this store's own, counted on its clock at creation and after every chunk; the library's value is not used.</summary>
    public Task SetExpirationAsync(string fileId, DateTimeOffset expires, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<DateTimeOffset?> GetExpirationAsync(string fileId, CancellationToken cancellationToken) =>
        (await folders.ReadAsync(caseId, fileId, cancellationToken).ConfigureAwait(false))?.ExpiresAt;

    /// <summary>Expired uploads are removed by the sweep, which sees every case; a request's store names none.</summary>
    public Task<IEnumerable<string>> GetExpiredFilesAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<string>>([]);

    public Task<int> RemoveExpiredFilesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    private async Task<UploadInfo> Required(string fileId, CancellationToken cancellationToken) =>
        await FindAsync(fileId, cancellationToken).ConfigureAwait(false) ?? throw new TusStoreException("no such upload");
}
