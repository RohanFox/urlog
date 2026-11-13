using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <summary>
    /// The one-row table that names the event store this file belongs to, and the ceiling on file ids and case ids.
    /// Ids from 2^62 belong to <c>urlog ingest</c>, which writes a paired store without a record: SQLite triggers,
    /// written by hand, refuse a <c>files</c> row and a <c>cases</c> row at or above it. AFTER, because a BEFORE
    /// INSERT trigger does not see the id SQLite is about to assign. On Postgres the same is a CHECK on each
    /// identity column.
    /// </summary>
    public partial class StoreIdentity : Migration
    {
        // A migration is a record of what was applied: the value is written out, not taken from the code.
        private const string IngestFileIdFloor = "4611686018427387904";
        private const string IngestCaseIdFloor = "4611686018427387904";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "store_identity",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    store_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    origin = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_store_identity", x => x.id);
                    table.CheckConstraint("ck_store_identity_origin", "origin IN ('created', 'paired')");
                    table.CheckConstraint("ck_store_identity_single", "id = 1");
                    table.CheckConstraint("ck_store_identity_store_id", "length(store_id) = 32");
                });

            if (!migrationBuilder.IsSqlite())
            {
                return;
            }

            migrationBuilder.Sql($"""
                CREATE TRIGGER files_id_below_ingest_floor_insert AFTER INSERT ON files
                WHEN NEW.id >= {IngestFileIdFloor}
                BEGIN
                    SELECT RAISE(ABORT, 'files: ids from {IngestFileIdFloor} are reserved for urlog ingest');
                END;
                """);
            migrationBuilder.Sql($"""
                CREATE TRIGGER files_id_below_ingest_floor_update AFTER UPDATE OF id ON files
                WHEN NEW.id >= {IngestFileIdFloor}
                BEGIN
                    SELECT RAISE(ABORT, 'files: ids from {IngestFileIdFloor} are reserved for urlog ingest');
                END;
                """);
            migrationBuilder.Sql($"""
                CREATE TRIGGER cases_id_below_ingest_floor_insert AFTER INSERT ON cases
                WHEN NEW.id >= {IngestCaseIdFloor}
                BEGIN
                    SELECT RAISE(ABORT, 'cases: ids from {IngestCaseIdFloor} are reserved for urlog ingest');
                END;
                """);
            migrationBuilder.Sql($"""
                CREATE TRIGGER cases_id_below_ingest_floor_update AFTER UPDATE OF id ON cases
                WHEN NEW.id >= {IngestCaseIdFloor}
                BEGIN
                    SELECT RAISE(ABORT, 'cases: ids from {IngestCaseIdFloor} are reserved for urlog ingest');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.IsSqlite())
            {
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS cases_id_below_ingest_floor_update;");
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS cases_id_below_ingest_floor_insert;");
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS files_id_below_ingest_floor_update;");
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS files_id_below_ingest_floor_insert;");
            }

            migrationBuilder.DropTable(
                name: "store_identity");
        }
    }
}
