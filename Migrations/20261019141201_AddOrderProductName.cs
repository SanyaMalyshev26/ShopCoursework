using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopCoursework.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderProductName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProductName",
                table: "OrderItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductVariants_Stock_NonNegative",
                table: "ProductVariants",
                sql: "\"Stock\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProductVariants_Stock_NonNegative",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "ProductName",
                table: "OrderItems");
        }
    }
}
