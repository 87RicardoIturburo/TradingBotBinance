using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionPeakPriceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "HighestPriceSinceEntry",
                table: "Positions",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "LowestPriceSinceEntry",
                table: "Positions",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            // Inicializar con EntryPrice para posiciones existentes
            migrationBuilder.Sql("""
                UPDATE "Positions"
                SET "HighestPriceSinceEntry" = "EntryPrice",
                    "LowestPriceSinceEntry"  = "EntryPrice"
                WHERE "HighestPriceSinceEntry" = 0
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HighestPriceSinceEntry",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "LowestPriceSinceEntry",
                table: "Positions");
        }
    }
}
