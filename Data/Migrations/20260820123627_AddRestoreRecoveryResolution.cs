using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CitusManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRestoreRecoveryResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecoveryResolutionNote",
                table: "RestoreRuns",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveryResolvedAt",
                table: "RestoreRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecoveryResolvedBy",
                table: "RestoreRuns",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecoveryResolutionNote",
                table: "RestoreRuns");

            migrationBuilder.DropColumn(
                name: "RecoveryResolvedAt",
                table: "RestoreRuns");

            migrationBuilder.DropColumn(
                name: "RecoveryResolvedBy",
                table: "RestoreRuns");
        }
    }
}
