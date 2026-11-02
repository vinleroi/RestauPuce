namespace ChefOS.Core.Common;

public sealed class DomainValidationException(string message) : Exception(message);
