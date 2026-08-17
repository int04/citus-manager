using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CitusManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRestoreTargetIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetIdentityHash",
                table: "RestoreRuns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestoreRuns_TargetIdentityHash_Status",
                table: "RestoreRuns",
                columns: new[] { "TargetIdentityHash", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RestoreRuns_TargetIdentityHash_Status",
                table: "RestoreRuns");

            migrationBuilder.DropColumn(
                name: "TargetIdentityHash",
                table: "RestoreRuns");
        }
    }
}
