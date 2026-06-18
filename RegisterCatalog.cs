using System.Text.Json;
using System.Text.Json.Serialization;

namespace InverterMonitor;

public sealed class RegisterCatalog
{
    private readonly IReadOnlyDictionary<string, InverterDefinition> definitionsById;

    public RegisterCatalog(IWebHostEnvironment environment, ILogger<RegisterCatalog> logger)
    {
        var definitionsPath = Path.Combine(environment.ContentRootPath, "InverterDefinitions");
        Directory.CreateDirectory(definitionsPath);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        var definitions = Directory
            .EnumerateFiles(definitionsPath, "*.json")
            .Select(path => LoadDefinition(path, options, logger))
            .Where(definition => definition is not null)
            .Cast<InverterDefinition>()
            .ToArray();

        definitionsById = definitions.ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase);

        if (definitionsById.Count == 0)
        {
            logger.LogWarning("No inverter definition JSON files were found in {DefinitionsPath}", definitionsPath);
        }
    }

    public IReadOnlyList<InverterDefinition> Definitions => definitionsById.Values.OrderBy(definition => definition.Brand).ThenBy(definition => definition.Model).ToArray();

    public InverterDefinition ActiveDefinition(MonitorSettings settings)
    {
        if (definitionsById.TryGetValue(settings.InverterDefinitionId, out var definition))
        {
            return definition;
        }

        return definitionsById.Values.FirstOrDefault()
            ?? new InverterDefinition(
                "empty",
                "No definitions loaded",
                "No model",
                "No JSON definitions found.",
                new ProtocolDefaults(),
                new ReadBehavior(),
                [],
                []);
    }

    private static InverterDefinition? LoadDefinition(string path, JsonSerializerOptions options, ILogger logger)
    {
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<InverterDefinitionFile>(json, options)
                ?? throw new InvalidOperationException("Definition file was empty.");

            var registers = file.Registers
                .Select(register => register.ToDefinition())
                .ToArray();

            return new InverterDefinition(
                file.Id,
                file.Brand,
                file.Model,
                file.Description,
                file.Protocol,
                file.ReadBehavior,
                registers,
                file.DerivedReadings);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load inverter definition {Path}", path);
            return null;
        }
    }
}

public sealed record InverterDefinition(
    string Id,
    string Brand,
    string Model,
    string Description,
    ProtocolDefaults Protocol,
    ReadBehavior ReadBehavior,
    IReadOnlyList<RegisterDefinition> Registers,
    IReadOnlyList<DerivedReadingDefinition> DerivedReadings);

public sealed record ProtocolDefaults
{
    public string Transport { get; init; } = "rtuOverTcp";
    public byte SlaveId { get; init; } = 1;
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "none";
    public int StopBits { get; init; } = 1;
}

public sealed record ReadBehavior
{
    public string RegisterType { get; init; } = "holding";
    public int MaxBlockSize { get; init; } = 16;
    public int MaxGapWithinBlock { get; init; } = 4;
    public int DelayBetweenBlocksMs { get; init; } = 80;
}

public sealed record RegisterDefinition(
    string Key,
    string Name,
    int Address,
    string Unit,
    double Scale,
    string? Notes = null,
    RegisterValueKind ValueKind = RegisterValueKind.UnsignedWord,
    string? Group = null,
    IReadOnlyDictionary<int, string>? ValueMap = null);

public sealed record DerivedReadingDefinition
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string? Notes { get; init; }
    public string? Group { get; init; }
    public string Operation { get; init; } = "sum";
    public IReadOnlyList<string> Sources { get; init; } = [];
}

public enum RegisterValueKind
{
    UnsignedWord,
    SignedWord,
    HighByte,
    LowByte
}

internal sealed record InverterDefinitionFile
{
    public string Id { get; init; } = "";
    public string Brand { get; init; } = "";
    public string Model { get; init; } = "";
    public string Description { get; init; } = "";
    public ProtocolDefaults Protocol { get; init; } = new();
    public ReadBehavior ReadBehavior { get; init; } = new();
    public IReadOnlyList<RegisterDefinitionFileEntry> Registers { get; init; } = [];
    public IReadOnlyList<DerivedReadingDefinition> DerivedReadings { get; init; } = [];
}

internal sealed record RegisterDefinitionFileEntry
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public string Unit { get; init; } = "";
    public double Scale { get; init; } = 1;
    public string? Notes { get; init; }
    public RegisterValueKind ValueKind { get; init; } = RegisterValueKind.UnsignedWord;
    public string? Group { get; init; }
    public IReadOnlyDictionary<string, string>? ValueMap { get; init; }

    public RegisterDefinition ToDefinition()
    {
        if (!TryParseAddress(Address, out var parsedAddress))
        {
            throw new InvalidOperationException($"Invalid register address '{Address}' for '{Key}'.");
        }

        var valueMap = ValueMap?
            .Select(pair =>
            {
                if (!TryParseAddress(pair.Key, out var key))
                {
                    throw new InvalidOperationException($"Invalid value map key '{pair.Key}' for '{Key}'.");
                }

                return new KeyValuePair<int, string>(key, pair.Value);
            })
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        return new RegisterDefinition(Key, Name, parsedAddress, Unit, Scale, Notes, ValueKind, Group, valueMap);
    }

    private static bool TryParseAddress(string value, out int address)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out address);
        }

        return int.TryParse(value, out address);
    }
}
