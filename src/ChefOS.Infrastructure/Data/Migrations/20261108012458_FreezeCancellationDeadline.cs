using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChefOS.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FreezeCancellationDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FreeCancellationUntilUtc",
                table: "Reservations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Les réservations existantes gardent la règle en vigueur de leur restaurant (24 h sans profil).
            migrationBuilder.Sql("""
                UPDATE "Reservations" r
                SET "FreeCancellationUntilUtc" = r."StartsAtUtc" - make_interval(hours => COALESCE(
                    (SELECT p."FreeCancellationHours" FROM "VitrineProfiles" p WHERE p."RestaurantId" = r."RestaurantId"), 24));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FreeCancellationUntilUtc",
                table: "Reservations");
        }
    }
}
