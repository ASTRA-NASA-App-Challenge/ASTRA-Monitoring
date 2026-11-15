using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASTRAMonitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class FirmwareSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Pin = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    AccessHash = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshHash = table.Column<string>(type: "TEXT", nullable: false),
                    AccessExpiresTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    RefreshExpiresTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceSessions_CrewMembers_Pin",
                        column: x => x.Pin,
                        principalTable: "CrewMembers",
                        principalColumn: "Pin",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSessions_AccessHash",
                table: "DeviceSessions",
                column: "AccessHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSessions_Pin",
                table: "DeviceSessions",
                column: "Pin");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSessions_RefreshHash",
                table: "DeviceSessions",
                column: "RefreshHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceSessions");
        }
    }
}
