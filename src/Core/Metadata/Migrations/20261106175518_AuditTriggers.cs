using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <summary>
    /// The audit table is append-only in the store itself, not only in the code. Generated empty by
    /// <c>dotnet ef migrations add AuditTriggers</c> (the model did not change), then the three SQLite triggers written by
    /// hand: UPDATE and DELETE abort; an INSERT whose <c>seq</c> is not the last seq + 1 or whose <c>prev_hash</c> is not
    /// the last row's hash aborts, so a fork of the chain from any process is an immediate error rather than a later
    /// <c>verify-audit</c> failure. <c>VACUUM INTO</c> copies triggers, so a backup carries them too. On Postgres the same
    /// is <c>REVOKE UPDATE, DELETE ON audit</c> for the application role plus one trigger function; written when a Postgres
    /// deployment exists, so this migration is a no-op there.
    /// </summary>
    public partial class AuditTriggers : Migration
    {
        private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.IsSqlite())
            {
                return;
            }

            migrationBuilder.Sql("""
                CREATE TRIGGER audit_no_update BEFORE UPDATE ON audit
                BEGIN
                    SELECT RAISE(ABORT, 'audit is append-only: update refused');
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_no_delete BEFORE DELETE ON audit
                BEGIN
                    SELECT RAISE(ABORT, 'audit is append-only: delete refused');
                END;
                """);
            migrationBuilder.Sql($"""
                CREATE TRIGGER audit_chain_insert BEFORE INSERT ON audit
                BEGIN
                    SELECT RAISE(ABORT, 'audit: seq must be the last seq + 1')
                     WHERE NEW.seq <> (SELECT coalesce(max(seq), 0) + 1 FROM audit);
                    SELECT RAISE(ABORT, 'audit: prev_hash must be the last row''s hash')
                     WHERE NEW.prev_hash <> coalesce((SELECT hash FROM audit WHERE seq = NEW.seq - 1), '{ZeroHash}');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.IsSqlite())
            {
                return;
            }

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_chain_insert;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_no_delete;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_no_update;");
        }
    }
}
