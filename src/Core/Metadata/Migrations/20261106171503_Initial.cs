using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    failed_logins = table.Column<int>(type: "INTEGER", nullable: false),
                    locked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_login_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_role", "role IN ('admin', 'analyst')");
                });

            migrationBuilder.CreateTable(
                name: "audit",
                columns: table => new
                {
                    seq = table.Column<long>(type: "INTEGER", nullable: false),
                    ts_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    actor_id = table.Column<long>(type: "INTEGER", nullable: true),
                    action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    target_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    target_id = table.Column<long>(type: "INTEGER", nullable: true),
                    details_json = table.Column<string>(type: "TEXT", nullable: false),
                    prev_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit", x => x.seq);
                    table.ForeignKey(
                        name: "fk_audit_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cases",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    owner_id = table.Column<long>(type: "INTEGER", nullable: false),
                    prior_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    closed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    archived_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    purging_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    purged_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cases", x => x.id);
                    table.CheckConstraint("ck_cases_state", "state IN ('active', 'closed', 'archived', 'soft_deleted', 'purging', 'purged')");
                    table.ForeignKey(
                        name: "fk_cases_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "case_members",
                columns: table => new
                {
                    case_id = table.Column<long>(type: "INTEGER", nullable: false),
                    user_id = table.Column<long>(type: "INTEGER", nullable: false),
                    role = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    granted_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_members", x => new { x.case_id, x.user_id });
                    table.CheckConstraint("ck_case_members_role", "role IN ('owner', 'rw', 'ro')");
                    table.ForeignKey(
                        name: "fk_case_members_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_case_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "hosts",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    case_id = table.Column<long>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    timezone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    timezone_assumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hosts", x => x.id);
                    table.ForeignKey(
                        name: "fk_hosts_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    case_id = table.Column<long>(type: "INTEGER", nullable: false),
                    host_id = table.Column<long>(type: "INTEGER", nullable: true),
                    parent_file_id = table.Column<long>(type: "INTEGER", nullable: true),
                    archive_path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    original_name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    stored_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    uploader = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    uploader_user_id = table.Column<long>(type: "INTEGER", nullable: true),
                    received_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    timezone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    year_hint = table.Column<int>(type: "INTEGER", nullable: true),
                    encoding = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    compression = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    detected_source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    detected_confidence = table.Column<float>(type: "REAL", nullable: true),
                    detected_layout = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    override_source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    override_options = table.Column<string>(type: "TEXT", nullable: true),
                    parse_generation = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ts_min = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ts_max = table.Column<DateTime>(type: "TEXT", nullable: true),
                    row_count = table.Column<long>(type: "INTEGER", nullable: false),
                    reject_count = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_files", x => x.id);
                    table.CheckConstraint("ck_files_sha256", "length(sha256) = 64");
                    table.CheckConstraint("ck_files_source", "source IN ('web', 'sftp', 'cli')");
                    table.CheckConstraint("ck_files_state", "state IN ('received', 'parsed', 'failed', 'quarantined', 'artifact', 'archived')");
                    table.ForeignKey(
                        name: "fk_files_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_files_files_parent_file_id",
                        column: x => x.parent_file_id,
                        principalTable: "files",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_files_hosts_host_id",
                        column: x => x.host_id,
                        principalTable: "hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_files_users_uploader_user_id",
                        column: x => x.uploader_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    case_id = table.Column<long>(type: "INTEGER", nullable: false),
                    file_id = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    generation = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    worker = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    chunks_read = table.Column<long>(type: "INTEGER", nullable: false),
                    chunks_parsed = table.Column<long>(type: "INTEGER", nullable: false),
                    chunks_acked = table.Column<long>(type: "INTEGER", nullable: false),
                    rows = table.Column<long>(type: "INTEGER", nullable: false),
                    rejects = table.Column<long>(type: "INTEGER", nullable: false),
                    bytes_read = table.Column<long>(type: "INTEGER", nullable: false),
                    message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_kind", "kind IN ('parse', 'reparse')");
                    table.CheckConstraint("ck_jobs_state", "state IN ('queued', 'parsing', 'done', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_jobs_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_jobs_files_file_id",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_actor_id",
                table: "audit",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_target_type_target_id",
                table: "audit",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_members_user_id",
                table: "case_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cases_owner_id",
                table: "cases",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_cases_state",
                table: "cases",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_files_case_id_state",
                table: "files",
                columns: new[] { "case_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_files_host_id",
                table: "files",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "ix_files_parent_file_id",
                table: "files",
                column: "parent_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_files_uploader_user_id",
                table: "files",
                column: "uploader_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_files_case_id_sha256",
                table: "files",
                columns: new[] { "case_id", "sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_hosts_case_id_name",
                table: "hosts",
                columns: new[] { "case_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_case_id",
                table: "jobs",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_file_id",
                table: "jobs",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_state_created_at",
                table: "jobs",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_users_name",
                table: "users",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit");

            migrationBuilder.DropTable(
                name: "case_members");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "hosts");

            migrationBuilder.DropTable(
                name: "cases");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
