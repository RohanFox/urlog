using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Urlog.Metadata.Tests;

/// <summary>A migrated metadata file in a fresh temp directory, deleted with its WAL and shared-memory files on dispose.</summary>
internal sealed class TempDb : IAsyncDisposable
{
    private readonly DirectoryInfo _directory;

    private TempDb(DirectoryInfo directory, TimeProvider? clock)
    {
        _directory = directory;
        Path = System.IO.Path.Combine(directory.FullName, "metadata.db");
        Store = new MetadataStore(Path, clock);
    }

    public string Path { get; }

    public MetadataStore Store { get; }

    public static TempDb CreateUnmigrated(TimeProvider? clock = null) => new(Directory.CreateTempSubdirectory("urlog-metadata-"), clock);

    public static async Task<TempDb> CreateAsync(TimeProvider? clock = null)
    {
        var db = CreateUnmigrated(clock);
        await db.Store.MigrateAsync();
        return db;
    }

    /// <summary>The actor the helpers act as (no user id: tests that need one pass their own).</summary>
    public static AuditActor Alice { get; } = new("alice");

    public MetadataDb Open() => Store.CreateDbContext();

    /// <summary>A user to own things; the name is unique per call.</summary>
    public static async Task<long> AddUserAsync(MetadataDb db, string name = "alice")
    {
        var user = new User { Name = name, PasswordHash = "argon2id$placeholder", CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>An active case with its owner row.</summary>
    public static async Task<CaseRecord> AddCaseAsync(MetadataDb db, long ownerId, string name = "incident 1") =>
        await CaseOperations.CreateAsync(db, AuditChain.Default, Alice, name, ownerId, DateTime.UtcNow);

    /// <summary>An intake record for <paramref name="content"/>, hashed as the pipeline would hash the bytes.</summary>
    public static EvidenceFile NewFile(long caseId, string content, string name = "access.log") => new()
    {
        CaseId = caseId,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
        Size = content.Length,
        OriginalName = name,
        StoredPath = name,
        Source = FileSource.Web,
        Uploader = "alice",
        ReceivedAt = DateTime.UtcNow,
        Encoding = "utf8",
    };

    public ValueTask DisposeAsync()
    {
        // Pooled connections keep the file open; drop them before deleting the directory.
        SqliteConnection.ClearAllPools();
        try
        {
            _directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A straggling handle on Windows: the temp directory is cleaned by the OS eventually.
        }

        return ValueTask.CompletedTask;
    }
}
