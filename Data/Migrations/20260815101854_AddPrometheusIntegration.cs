using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CitusManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrometheusIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrometheusBaseUrl",
                table: "Clusters",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedPrometheusToken",
                table: "Clusters",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrometheusBaseUrl",
                table: "Clusters");

            migrationBuilder.DropColumn(
                name: "ProtectedPrometheusToken",
                table: "Clusters");
        }
    }
}
