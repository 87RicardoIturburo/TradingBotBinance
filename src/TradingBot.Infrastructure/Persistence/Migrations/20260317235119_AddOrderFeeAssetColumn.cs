using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderFeeAssetColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeeAsset",
                table: "Orders",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeeAsset",
                table: "Orders");
        }
    }
}
