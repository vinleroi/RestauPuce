namespace ChefOS.Infrastructure.Seeding;

/// <summary>Compte de démonstration créé par <see cref="DemoSeeder"/> (section de configuration <c>Demo</c>).</summary>
public sealed record DemoOptions
{
    public string Email { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}
