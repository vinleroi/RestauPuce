namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// Les classes qui lisent ou modifient des variables d'environnement du processus (<c>MOLLIE_API_KEY</c>, <c>ANTHROPIC_API_KEY</c>) s'exécutent
/// l'une après l'autre et jamais en même temps que d'autres tests : une variable de processus est partagée par tous les tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection
{
    public const string Name = "variables d'environnement";
}
