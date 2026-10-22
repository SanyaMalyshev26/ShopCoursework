using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopCoursework.Migrations
{
    /// <inheritdoc />
    public partial class AddProductSizeGuides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductSizeGuides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    Size = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ChestMin = table.Column<decimal>(type: "TEXT", nullable: false),
                    ChestMax = table.Column<decimal>(type: "TEXT", nullable: false),
                    WaistMin = table.Column<decimal>(type: "TEXT", nullable: false),
                    WaistMax = table.Column<decimal>(type: "TEXT", nullable: false),
                    HipsMin = table.Column<decimal>(type: "TEXT", nullable: false),
                    HipsMax = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSizeGuides", x => x.Id);
                    table.CheckConstraint(
                        "CK_ProductSizeGuides_ChestRange",
                        "CAST(\"ChestMin\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"ChestMax\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"ChestMin\" AS REAL) <= CAST(\"ChestMax\" AS REAL)");
                    table.CheckConstraint(
                        "CK_ProductSizeGuides_HipsRange",
                        "CAST(\"HipsMin\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"HipsMax\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"HipsMin\" AS REAL) <= CAST(\"HipsMax\" AS REAL)");
                    table.CheckConstraint(
                        "CK_ProductSizeGuides_WaistRange",
                        "CAST(\"WaistMin\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"WaistMax\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"WaistMin\" AS REAL) <= CAST(\"WaistMax\" AS REAL)");
                    table.ForeignKey(
                        name: "FK_ProductSizeGuides_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductSizeGuides_ProductId_Size",
                table: "ProductSizeGuides",
                columns: new[] { "ProductId", "Size" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductSizeGuides");
        }
    }
}
