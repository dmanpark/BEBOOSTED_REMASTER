namespace BeBoosted.Infrastructure.Persistence;

/// <summary>A single forward-only schema migration.</summary>
public sealed record Migration(int Version, string Name, string Sql);
