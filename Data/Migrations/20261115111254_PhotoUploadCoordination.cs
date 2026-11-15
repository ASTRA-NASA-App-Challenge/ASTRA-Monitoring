using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ASTRAMonitoring.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhotoUploadCoordination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotoUploadLeases",
                columns: table => new
                {
                    MeasurementId = table.Column<string>(type: "TEXT", nullable: false),
                    Pin = table.Column<string>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoUploadLeases", x => x.MeasurementId);
                    table.ForeignKey(
                        name: "FK_PhotoUploadLeases_CrewMembers_Pin",
                        column: x => x.Pin,
                        principalTable: "CrewMembers",
                        principalColumn: "Pin",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoUploadLeases_Pin",
                table: "PhotoUploadLeases",
                column: "Pin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotoUploadLeases");
        }
    }
}
