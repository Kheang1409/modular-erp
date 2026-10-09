using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Erp.Abstractions.Modules;

namespace Erp.ModuleSystem;

public sealed partial class ModuleManifestParser : IModuleManifestParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ModuleManifest Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ManifestDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ManifestDocument>(json, SerializerOptions)
                ?? throw new InvalidOperationException("Module manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Module manifest JSON is invalid.", exception);
        }

        var manifest = new ModuleManifest(
            document.Id ?? string.Empty,
            document.Name ?? string.Empty,
            document.Version ?? string.Empty,
            document.Description ?? string.Empty,
            document.Author ?? string.Empty,
            document.License ?? string.Empty,
            document.MinimumPlatformVersion ?? string.Empty,
            document.Dependencies?.Select(x => new ModuleDependency(x.ModuleId ?? string.Empty, x.VersionRange ?? string.Empty)).ToArray() ?? [],
            document.Permissions ?? []);
        ModuleManifestValidator.Validate(manifest);
        return manifest;
    }

    private sealed class ManifestDocument
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("author")] public string? Author { get; init; }
        [JsonPropertyName("license")] public string? License { get; init; }
        [JsonPropertyName("minPlatformVersion")] public string? MinimumPlatformVersion { get; init; }
        [JsonPropertyName("dependencies")] public DependencyDocument[]? Dependencies { get; init; }
        [JsonPropertyName("permissions")] public string[]? Permissions { get; init; }
    }

    private sealed class DependencyDocument
    {
        [JsonPropertyName("module")] public string? ModuleId { get; init; }
        [JsonPropertyName("version")] public string? VersionRange { get; init; }
    }
}

internal static partial class ModuleManifestValidator
{
    public static void Validate(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!ModuleIdPattern().IsMatch(manifest.Id))
            throw new InvalidOperationException($"Module ID '{manifest.Id}' must contain lowercase dot-separated identifiers.");
        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Description) ||
            string.IsNullOrWhiteSpace(manifest.Author) || string.IsNullOrWhiteSpace(manifest.License))
            throw new InvalidOperationException($"Module '{manifest.Id}' is missing required metadata.");
        if (!SemanticVersion.TryParse(manifest.Version, out _) || !SemanticVersion.TryParse(manifest.MinimumPlatformVersion, out _))
            throw new InvalidOperationException($"Module '{manifest.Id}' has an invalid semantic version.");
        if (manifest.Dependencies.GroupBy(x => x.ModuleId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidOperationException($"Module '{manifest.Id}' contains duplicate dependencies.");
        if (manifest.Permissions.Any(permission => !PermissionPattern().IsMatch(permission)) ||
            manifest.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Permissions.Count)
            throw new InvalidOperationException($"Module '{manifest.Id}' contains invalid or duplicate permissions.");
        foreach (var dependency in manifest.Dependencies)
        {
            if (!ModuleIdPattern().IsMatch(dependency.ModuleId))
                throw new InvalidOperationException($"Module '{manifest.Id}' contains an invalid dependency ID.");
            _ = SemanticVersionRange.Contains(dependency.VersionRange, new SemanticVersion(0, 0, 0, null));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9_-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionPattern();
}
