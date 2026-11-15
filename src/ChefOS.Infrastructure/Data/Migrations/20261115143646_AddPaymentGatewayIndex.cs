using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChefOS.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentGatewayIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_RestaurantId",
                table: "Payments");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RestaurantId_GatewayPaymentId",
                table: "Payments",
                columns: new[] { "RestaurantId", "GatewayPaymentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_RestaurantId_GatewayPaymentId",
                table: "Payments");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RestaurantId",
                table: "Payments",
                column: "RestaurantId");
        }
    }
}
