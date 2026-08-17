using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CitusManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTopologyQueryEndpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "Operations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClusterQueryEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Health = table.Column<int>(type: "integer", nullable: false),
                    MetadataSynced = table.Column<bool>(type: "boolean", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterQueryEndpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClusterQueryEndpoints_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_ClusterId_IdempotencyKey",
                table: "Operations",
                columns: new[] { "ClusterId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClusterQueryEndpoints_ClusterId_Host_Port",
                table: "ClusterQueryEndpoints",
                columns: new[] { "ClusterId", "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClusterQueryEndpoints_ClusterId_IsEnabled_Health",
                table: "ClusterQueryEndpoints",
                columns: new[] { "ClusterId", "IsEnabled", "Health" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClusterQueryEndpoints");

            migrationBuilder.DropIndex(
                name: "IX_Operations_ClusterId_IdempotencyKey",
                table: "Operations");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "Operations");
        }
    }
}
