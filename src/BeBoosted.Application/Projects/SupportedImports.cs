using BeBoosted.Domain.Projects;

namespace BeBoosted.Application.Projects;

/// <summary>
/// The file types an import accepts, shared by the picker's filter and the validation
/// below it. The picker's filename box accepts any typed path, so the filter alone
/// guarantees nothing (BB-QA-005) — refusal happens here, before any bytes move.
/// </summary>
public static class SupportedImports
{
    public static readonly IReadOnlyList<string> DocumentExtensions =
        [".pdf", ".doc", ".docx", ".txt", ".md"];

    public static readonly IReadOnlyList<string> ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    /// <summary>Picker patterns ("*.pdf", …) for the kind, from the same lists the check uses.</summary>
    public static IReadOnlyList<string> PatternsFor(ResourceKind kind)
        => [.. ExtensionsFor(kind).Select(extension => "*" + extension)];

    public static bool Accepts(ResourceKind kind, string fileName)
        => ExtensionsFor(kind).Contains(
            Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>The refusal, named for the user: the file, and what would be accepted.</summary>
    public static string RefusalFor(ResourceKind kind, string fileName)
        => $"'{fileName}' isn't a type BeBoosted can import here. "
            + $"Supported: {string.Join(", ", ExtensionsFor(kind))}.";

    private static IReadOnlyList<string> ExtensionsFor(ResourceKind kind) => kind switch
    {
        ResourceKind.Document => DocumentExtensions,
        ResourceKind.Image => ImageExtensions,
        _ => [], // links and notes are never imported from disk
    };
}
