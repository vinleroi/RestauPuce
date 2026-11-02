namespace ChefOS.Core.Ingredients;

[Flags]
public enum EuAllergen
{
    None = 0,
    Gluten = 1,
    Crustaceans = 2,
    Eggs = 4,
    Fish = 8,
    Peanuts = 16,
    Soy = 32,
    Milk = 64,
    Nuts = 128,
    Celery = 256,
    Mustard = 512,
    Sesame = 1024,
    Sulphites = 2048,
    Lupin = 4096,
    Molluscs = 8192
}
