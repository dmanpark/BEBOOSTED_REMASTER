namespace BeBoosted.Domain;

/// <summary>A domain rule was violated.</summary>
public sealed class DomainException(string message) : Exception(message)
{
}
