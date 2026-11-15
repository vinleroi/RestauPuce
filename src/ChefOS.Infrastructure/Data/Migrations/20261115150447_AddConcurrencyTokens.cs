using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChefOS.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConcurrencyTokens : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Vide à dessein : <c>Version</c> est la colonne système <c>xmin</c> de PostgreSQL, qui existe déjà sur chaque table.
        /// Le générateur propose d'ajouter une colonne « xmin », ce qui échouerait ; seul l'instantané du modèle change.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
