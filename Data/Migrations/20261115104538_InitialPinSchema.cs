using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASTRAMonitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPinSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CrewMembers",
                columns: table => new
                {
                    Pin = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrewMembers", x => x.Pin);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialHash = table.Column<string>(type: "TEXT", nullable: false),
                    PairedDeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    AstronautIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentReadings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClientReadingId = table.Column<string>(type: "TEXT", nullable: false),
                    Pin = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    MeasuredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TemperatureC = table.Column<double>(type: "REAL", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                    RelativeHumidityPercent = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnvironmentReadings_CrewMembers_Pin",
                        column: x => x.Pin,
                        principalTable: "CrewMembers",
                        principalColumn: "Pin",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Measurements",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClientMeasurementId = table.Column<string>(type: "TEXT", nullable: false),
                    Pin = table.Column<string>(type: "TEXT", nullable: false),
                    UnoDeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    CameraDeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    MeasuredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Bpm = table.Column<int>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                    Quality = table.Column<string>(type: "TEXT", nullable: false),
                    AlgorithmVersion = table.Column<string>(type: "TEXT", nullable: false),
                    PulseStatusReason = table.Column<string>(type: "TEXT", nullable: true),
                    WaveformJson = table.Column<string>(type: "TEXT", nullable: true),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    PhotoStatus = table.Column<string>(type: "TEXT", nullable: false),
                    PhotoExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PhotoPath = table.Column<string>(type: "TEXT", nullable: true),
                    PhotoSha256 = table.Column<string>(type: "TEXT", nullable: true),
                    PhotoReceivedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Measurements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Measurements_CrewMembers_Pin",
                        column: x => x.Pin,
                        principalTable: "CrewMembers",
                        principalColumn: "Pin",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentReadings_Pin_MeasuredAt",
                table: "EnvironmentReadings",
                columns: new[] { "Pin", "MeasuredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_Pin_MeasuredAt",
                table: "Measurements",
                columns: new[] { "Pin", "MeasuredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "EnvironmentReadings");

            migrationBuilder.DropTable(
                name: "Measurements");

            migrationBuilder.DropTable(
                name: "CrewMembers");
        }
    }
}
