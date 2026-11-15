using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASTRAMonitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetiredPinsAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeletionPending",
                table: "CrewMembers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Retired",
                table: "CrewMembers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_Pin_ClientMeasurementId",
                table: "Measurements",
                columns: new[] { "Pin", "ClientMeasurementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentReadings_Pin_ClientReadingId",
                table: "EnvironmentReadings",
                columns: new[] { "Pin", "ClientReadingId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Measurements_Pin_ClientMeasurementId",
                table: "Measurements");

            migrationBuilder.DropIndex(
                name: "IX_EnvironmentReadings_Pin_ClientReadingId",
                table: "EnvironmentReadings");

            migrationBuilder.DropColumn(
                name: "DeletionPending",
                table: "CrewMembers");

            migrationBuilder.DropColumn(
                name: "Retired",
                table: "CrewMembers");
        }
    }
}
