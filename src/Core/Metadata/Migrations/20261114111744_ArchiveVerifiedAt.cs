using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveVerifiedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "case_tasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "case_tasks");
        }
    }
}
