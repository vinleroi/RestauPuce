using ChefOS.Core.Ingredients;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Units;

namespace ChefOS.Core.Recipes;

public class RecipeLine : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int RecipeId { get; set; }
    public int? IngredientId { get; set; }
    public Ingredient? Ingredient { get; set; }
    public int? SubRecipeId { get; set; }
    public Recipe? SubRecipe { get; set; }
    public decimal Quantity { get; set; }
    public MeasureUnit Unit { get; set; }
}
