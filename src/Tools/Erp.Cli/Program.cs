using System.Text.Json;
using Erp.ModuleSystem;

return Run(args);

static int Run(string[] arguments)
{
    try
    {
        var values = arguments.ToList();
        var root = TakeOption(values, "--root") ?? "modules/external";
        var trustedKeysPath = TakeOption(values, "--trusted-keys");
        var requireSignature = TakeFlag(values, "--require-signature");
        if (values.Count < 2 || !string.Equals(values[0], "module", StringComparison.OrdinalIgnoreCase))
            return Usage();
        var trustedKeys = trustedKeysPath is not null && Directory.Exists(trustedKeysPath)
            ? Directory.GetFiles(trustedKeysPath, "*.pem").ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trustPolicy = requireSignature || trustedKeys.Count > 0 ? new ModuleTrustPolicy(requireSignature, trustedKeys) : null;
        var manager = new ModulePackageManager(new ModuleManifestParser(), root, trustPolicy);
        var command = values[1].ToLowerInvariant();
        var operand = values.Count > 2 ? values[2] : null;
        object result = command switch
        {
            "list" or "status" => manager.List(),
            "search" when operand is not null => manager.List().Where(module =>
                module.Id.Contains(operand, StringComparison.OrdinalIgnoreCase) ||
                module.Name.Contains(operand, StringComparison.OrdinalIgnoreCase)).ToArray(),
            "validate" when operand is not null => manager.Validate(operand),
            "install" when operand is not null => manager.Install(operand),
            "update" when operand is not null => manager.Install(operand, update: true),
            "enable" when operand is not null => manager.SetEnabled(operand, true),
            "disable" when operand is not null => manager.SetEnabled(operand, false),
            _ => throw new ArgumentException("Unknown command or missing operand.")
        };
        Console.WriteLine(JsonSerializer.Serialize(result, CliJson.Options));
        return 0;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or FileNotFoundException or KeyNotFoundException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 2;
    }
}

static string? TakeOption(List<string> arguments, string name)
{
    var index = arguments.FindIndex(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return null;
    if (index + 1 >= arguments.Count) throw new ArgumentException($"{name} requires a value.");
    var value = arguments[index + 1];
    arguments.RemoveRange(index, 2);
    return value;
}

static bool TakeFlag(List<string> arguments, string name)
{
    var index = arguments.FindIndex(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return false;
    arguments.RemoveAt(index);
    return true;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: erp module <list|status|search|validate|install|update|enable|disable> [value] [--root <path>] [--trusted-keys <path>] [--require-signature]");
    return 1;
}

file static class CliJson
{
    internal static JsonSerializerOptions Options { get; } = new() { WriteIndented = true };
}
