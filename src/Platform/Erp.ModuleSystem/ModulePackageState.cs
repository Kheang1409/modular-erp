using System.Text.Json;

namespace Erp.ModuleSystem;

internal static class ModulePackageState
{
    internal const string FileName = "modules.state.json";

    internal static Dictionary<string, bool> Read(string rootPath)
    {
        var path = Path.Combine(rootPath, FileName);
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new InvalidDataException("The module state file is not a regular file.");
            return state;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The module state file must contain an object of module enablement flags.");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("The module state file contains an invalid enablement flag.");
                if (!state.TryAdd(property.Name, property.Value.GetBoolean()))
                    throw new InvalidDataException("The module state file contains duplicate module IDs.");
            }
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The module state file contains invalid JSON.", exception);
        }
    }
}
