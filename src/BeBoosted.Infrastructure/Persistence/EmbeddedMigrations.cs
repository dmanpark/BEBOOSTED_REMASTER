using System.Globalization;
using System.Reflection;

namespace BeBoosted.Infrastructure.Persistence;

/// <summary>
/// Loads the application's migration scripts from embedded resources named
/// Persistence/Migrations/NNNN_name.sql.
/// </summary>
public static class EmbeddedMigrations
{
    public static IReadOnlyList<Migration> Load()
    {
        var assembly = typeof(EmbeddedMigrations).Assembly;
        var migrations = new List<Migration>();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Resource names look like "BeBoosted.Infrastructure.Persistence.Migrations.0001_initial.sql".
            var fileName = resourceName.Split('.')[^2];
            var separator = fileName.IndexOf('_', StringComparison.Ordinal);
            if (separator < 1 || !int.TryParse(fileName[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException($"Migration resource '{resourceName}' is not named NNNN_name.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing embedded resource '{resourceName}'.");
            using var reader = new StreamReader(stream);
            migrations.Add(new Migration(version, fileName[(separator + 1)..], reader.ReadToEnd()));
        }

        return migrations.OrderBy(m => m.Version).ToList();
    }
}
