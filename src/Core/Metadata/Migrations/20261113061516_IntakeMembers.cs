using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class IntakeMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_kind",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_files_state",
                table: "files");

            migrationBuilder.AddColumn<string>(
                name: "options",
                table: "jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "archive_depth",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "cache_path",
                table: "files",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "container_cut",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "expanded_members",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "hash_of_stored_bytes",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "host_inferred",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "host_rule",
                table: "files",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "input_truncated",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "member_offset",
                table: "files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "member_ordinal",
                table: "files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "root_file_id",
                table: "files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "seen_paths",
                table: "files",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "spans_dst",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "stated_size",
                table: "files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "trailing_bytes",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "trailing_bytes_inexact",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "truncated_decoded_offset",
                table: "files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "truncated_stored_offset",
                table: "files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "unreadable_reason",
                table: "files",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "year_assumed",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "year_given",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_kind",
                table: "jobs",
                sql: "kind IN ('parse', 'reparse', 'expand')");

            migrationBuilder.CreateIndex(
                name: "ix_files_root_file_id",
                table: "files",
                column: "root_file_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_files_state",
                table: "files",
                sql: "state IN ('received', 'parsed', 'failed', 'quarantined', 'artifact', 'archived', 'expanded', 'unreadable')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_kind",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_files_root_file_id",
                table: "files");

            migrationBuilder.DropCheckConstraint(
                name: "ck_files_state",
                table: "files");

            migrationBuilder.DropColumn(
                name: "options",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "archive_depth",
                table: "files");

            migrationBuilder.DropColumn(
                name: "cache_path",
                table: "files");

            migrationBuilder.DropColumn(
                name: "container_cut",
                table: "files");

            migrationBuilder.DropColumn(
                name: "expanded_members",
                table: "files");

            migrationBuilder.DropColumn(
                name: "hash_of_stored_bytes",
                table: "files");

            migrationBuilder.DropColumn(
                name: "host_inferred",
                table: "files");

            migrationBuilder.DropColumn(
                name: "host_rule",
                table: "files");

            migrationBuilder.DropColumn(
                name: "input_truncated",
                table: "files");

            migrationBuilder.DropColumn(
                name: "member_offset",
                table: "files");

            migrationBuilder.DropColumn(
                name: "member_ordinal",
                table: "files");

            migrationBuilder.DropColumn(
                name: "root_file_id",
                table: "files");

            migrationBuilder.DropColumn(
                name: "seen_paths",
                table: "files");

            migrationBuilder.DropColumn(
                name: "spans_dst",
                table: "files");

            migrationBuilder.DropColumn(
                name: "stated_size",
                table: "files");

            migrationBuilder.DropColumn(
                name: "trailing_bytes",
                table: "files");

            migrationBuilder.DropColumn(
                name: "trailing_bytes_inexact",
                table: "files");

            migrationBuilder.DropColumn(
                name: "truncated_decoded_offset",
                table: "files");

            migrationBuilder.DropColumn(
                name: "truncated_stored_offset",
                table: "files");

            migrationBuilder.DropColumn(
                name: "unreadable_reason",
                table: "files");

            migrationBuilder.DropColumn(
                name: "year_assumed",
                table: "files");

            migrationBuilder.DropColumn(
                name: "year_given",
                table: "files");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_kind",
                table: "jobs",
                sql: "kind IN ('parse', 'reparse')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_files_state",
                table: "files",
                sql: "state IN ('received', 'parsed', 'failed', 'quarantined', 'artifact', 'archived')");
        }
    }
}
