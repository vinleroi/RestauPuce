namespace ChefOS.Core.Costing;

public sealed record CycleInfo(string ContainerName, string ContainedName)
{
    public string Message => $"{ContainerName} contient déjà {ContainedName} (directement ou indirectement)";
}
