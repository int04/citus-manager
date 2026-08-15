using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CitusManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertDeliveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastNotifiedAt",
                table: "Alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NotificationAttempts",
                table: "Alerts",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastNotifiedAt",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "NotificationAttempts",
                table: "Alerts");
        }
    }
}
