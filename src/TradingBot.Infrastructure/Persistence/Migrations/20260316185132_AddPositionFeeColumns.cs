using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionFeeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EntryFee",
                table: "Positions",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedFeeRate",
                table: "Positions",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ExitFee",
                table: "Positions",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntryFee",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "EstimatedFeeRate",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "ExitFee",
                table: "Positions");
        }
    }
}
