using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Urlog.Core.Metadata;

/// <summary>
/// The metadata store (design record, "Data model", "Case lifecycle", "Consistency"): users, cases, the case ACL,
/// hosts, the intake records, the job queue and the audit chain. SQLite in production (<see cref="MetadataStore"/>);
/// the model is kept Postgres-portable and tests/Metadata.Tests builds it against Npgsql to prove it: snake_case
/// identifiers that need no quoting, enums as text with a CHECK, every DateTime UTC, no provider-specific annotation.
/// One instance per request or job; never shared between threads.
/// </summary>
public sealed class MetadataDb(DbContextOptions<MetadataDb> options) : DbContext(options)
{
    /// <summary>EF's migrations history table, renamed so that every identifier in the file is lowercase snake_case.</summary>
    public const string MigrationsHistoryTable = "ef_migrations_history";

    public DbSet<User> Users => Set<User>();

    public DbSet<CaseRecord> Cases => Set<CaseRecord>();

    public DbSet<CaseMember> CaseMembers => Set<CaseMember>();

    public DbSet<Host> Hosts => Set<Host>();

    public DbSet<EvidenceFile> Files => Set<EvidenceFile>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<AuditRow> Audit => Set<AuditRow>();

    public DbSet<Session> Sessions => Set<Session>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        // Every DateTime (nullable included) is UTC in memory and on disk: ISO text in SQLite, timestamptz in Postgres.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users", t => t.HasCheckConstraint("ck_users_role", Names.EnumCheck<UserRole>("role")));
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Role).HasConversion(EnumText<UserRole>()).HasMaxLength(16);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
        });

        modelBuilder.Entity<CaseRecord>(e =>
        {
            e.ToTable("cases", t => t.HasCheckConstraint("ck_cases_state", Names.EnumCheck<CaseState>("state")));
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256);
            // A stale transition (two callers read `active`, both try to close) fails on save instead of applying twice.
            e.Property(x => x.State).HasConversion(EnumText<CaseState>()).HasMaxLength(16).IsConcurrencyToken();
            e.Property(x => x.PriorState).HasConversion(EnumText<CaseState>()).HasMaxLength(16);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.State);
        });

        modelBuilder.Entity<CaseMember>(e =>
        {
            e.ToTable("case_members", t => t.HasCheckConstraint("ck_case_members_role", Names.EnumCheck<MemberRole>("role")));
            e.HasKey(x => new { x.CaseId, x.UserId });
            e.Property(x => x.Role).HasConversion(EnumText<MemberRole>()).HasMaxLength(8);
            e.HasOne<CaseRecord>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // The per-user lookup that builds SQL_allowed_cases on every request (design record, "Security").
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<Host>(e =>
        {
            e.ToTable("hosts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(253);
            e.Property(x => x.Timezone).HasMaxLength(64);
            e.HasOne<CaseRecord>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            // Hosts are case-scoped: the same name in two cases is two rows, in one case one row (design record).
            e.HasIndex(x => new { x.CaseId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<EvidenceFile>(e =>
        {
            e.ToTable("files", t =>
            {
                t.HasCheckConstraint("ck_files_source", Names.EnumCheck<FileSource>("source"));
                t.HasCheckConstraint("ck_files_state", Names.EnumCheck<FileState>("state"));
                t.HasCheckConstraint("ck_files_sha256", "length(sha256) = 64");
                t.HasCheckConstraint("ck_files_detected_kind", Names.EnumCheck<Urlog.Core.Detection.FileKind>("detected_kind"));
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.OriginalName).HasMaxLength(1024);
            e.Property(x => x.StoredPath).HasMaxLength(1024);
            e.Property(x => x.ArchivePath).HasMaxLength(4096);
            e.Property(x => x.Source).HasConversion(EnumText<FileSource>()).HasMaxLength(8);
            e.Property(x => x.State).HasConversion(EnumText<FileState>()).HasMaxLength(16);
            e.Property(x => x.Uploader).HasMaxLength(128);
            e.Property(x => x.Timezone).HasMaxLength(64);
            e.Property(x => x.Encoding).HasMaxLength(32);
            e.Property(x => x.Compression).HasMaxLength(64);
            e.Property(x => x.DetectedKind).HasConversion(EnumText<Urlog.Core.Detection.FileKind>()).HasMaxLength(16);
            e.Property(x => x.DetectedSource).HasMaxLength(64);
            e.Property(x => x.DetectedLayout).HasMaxLength(128);
            e.Property(x => x.OverrideSource).HasMaxLength(64);
            e.HasOne<CaseRecord>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Host>().WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
            // NO ACTION, not RESTRICT: the case's cascade deletes parents and members in one statement, and SQLite checks
            // NO ACTION at the end of the statement while RESTRICT would refuse the parent row while a member still points at it.
            e.HasOne<EvidenceFile>().WithMany().HasForeignKey(x => x.ParentFileId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UploaderUserId).OnDelete(DeleteBehavior.Restrict);
            // Idempotent on hash within a case (evidence rule 3).
            e.HasIndex(x => new { x.CaseId, x.Sha256 }).IsUnique();
            e.HasIndex(x => new { x.CaseId, x.State });
            e.HasIndex(x => x.HostId);
            e.HasIndex(x => x.ParentFileId);
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.ToTable("jobs", t =>
            {
                t.HasCheckConstraint("ck_jobs_kind", Names.EnumCheck<JobKind>("kind"));
                t.HasCheckConstraint("ck_jobs_state", Names.EnumCheck<JobState>("state"));
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion(EnumText<JobKind>()).HasMaxLength(8);
            e.Property(x => x.State).HasConversion(EnumText<JobState>()).HasMaxLength(16);
            e.Property(x => x.Worker).HasMaxLength(128);
            e.HasOne<CaseRecord>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<EvidenceFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.State, x.CreatedAt });
            e.HasIndex(x => x.FileId);
        });

        modelBuilder.Entity<AuditRow>(e =>
        {
            e.ToTable("audit");
            e.HasKey(x => x.Seq);
            // The writer assigns seq: strictly monotonic, no gaps; an identity column would skip on rollback.
            e.Property(x => x.Seq).ValueGeneratedNever();
            e.Property(x => x.Actor).HasMaxLength(128);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.TargetType).HasMaxLength(32);
            e.Property(x => x.PrevHash).HasMaxLength(64);
            e.Property(x => x.Hash).HasMaxLength(64);
            // target_id is a plain number on purpose: the audit trail outlives the case, file or user it names.
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TargetType, x.TargetId });
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            // The SHA-256 of the cookie token: the file never holds a usable session.
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.CsrfToken).HasMaxLength(64);
            e.Property(x => x.ClientAddress).HasMaxLength(64);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // Lockout, role change and password reset end every session of the user.
            e.HasIndex(x => x.UserId);
        });

        ApplySnakeCaseNames(modelBuilder);
    }

    /// <summary>Every column, key, foreign key and index gets a lowercase snake_case name that needs no quoting anywhere.</summary>
    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName() ?? Names.Snake(entity.ClrType.Name);
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Names.Snake(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(key.IsPrimaryKey() ? "pk_" + table : "ak_" + table + "_" + Columns(key.Properties));
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName("fk_" + table + "_" + foreignKey.PrincipalEntityType.GetTableName() + "_" + Columns(foreignKey.Properties));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName((index.IsUnique ? "ux_" : "ix_") + table + "_" + Columns(index.Properties));
            }
        }

        static string Columns(IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty> properties) =>
            string.Join("_", properties.Select(p => Names.Snake(p.Name)));
    }

    private static ValueConverter<TEnum, string> EnumText<TEnum>() where TEnum : struct, Enum =>
        new(v => Names.Of(v), s => Names.Parse<TEnum>(s));

    /// <summary>Writes UTC (a Local value is converted, an Unspecified one is taken as UTC) and reads back Kind=Utc.</summary>
    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
