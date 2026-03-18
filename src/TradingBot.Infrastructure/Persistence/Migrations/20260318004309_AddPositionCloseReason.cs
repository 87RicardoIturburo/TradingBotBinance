using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionCloseReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CloseReason",
                table: "Positions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloseReason",
                table: "Positions");
        }
    }
}
