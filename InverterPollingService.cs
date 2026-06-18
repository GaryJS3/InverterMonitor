namespace InverterMonitor;

public sealed class InverterPollingService(
    MonitorState state,
    RegisterCatalog catalog,
    ModbusRtuOverTcpClient modbus,
    MqttPublisher mqttPublisher,
    ILogger<InverterPollingService> logger) : BackgroundService
{
    private readonly Queue<string> debugLog = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = state.Settings;
            if (!settings.PollingEnabled)
            {
                state.Publish(CreateSnapshot(false, "Polling disabled.", []));
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            try
            {
                var started = DateTimeOffset.UtcNow;
                var definition = catalog.ActiveDefinition(settings);
                var rawValues = await modbus.ReadHoldingRegistersAsync(settings, definition, stoppingToken);
                var readings = definition.Registers
                    .Where(definition => rawValues.ContainsKey(definition.Address))
                    .Select(definition => ToReading(definition, rawValues[definition.Address]))
                    .ToList();
                readings.AddRange(CreateDerivedReadings(definition, readings));

                AddLog($"Poll OK: {readings.Count} {definition.Id} values from {settings.GatewayHost}:{settings.GatewayPort} in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:N0} ms.");
                var snapshot = CreateSnapshot(true, "Connected", readings);
                var mqttResult = await mqttPublisher.PublishAsync(snapshot, stoppingToken);
                snapshot = snapshot with { Mqtt = mqttResult.Status };
                if (!string.IsNullOrWhiteSpace(mqttResult.Message))
                {
                    AddLog(mqttResult.Message);
                    snapshot = snapshot with { DebugLog = debugLog.Reverse().ToArray() };
                }

                state.Publish(snapshot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Poll failed");
                AddLog($"Poll failed: {ex.Message}");
                var snapshot = CreateSnapshot(false, ex.Message, state.GetSnapshot().Readings);
                var mqttResult = await mqttPublisher.PublishAsync(snapshot, stoppingToken);
                snapshot = snapshot with { Mqtt = mqttResult.Status };
                if (!string.IsNullOrWhiteSpace(mqttResult.Message))
                {
                    AddLog(mqttResult.Message);
                    snapshot = snapshot with { DebugLog = debugLog.Reverse().ToArray() };
                }

                state.Publish(snapshot);
            }

            await Task.Delay(settings.PollIntervalMs, stoppingToken);
        }
    }

    private MonitorSnapshot CreateSnapshot(bool connected, string status, IReadOnlyList<RegisterReading> readings)
    {
        return new MonitorSnapshot(
            DateTimeOffset.UtcNow,
            state.Settings,
            connected,
            status,
            mqttPublisher.Status,
            readings,
            debugLog.Reverse().ToArray());
    }

    private void AddLog(string message)
    {
        debugLog.Enqueue($"{DateTimeOffset.Now:HH:mm:ss} {message}");
        while (debugLog.Count > 80)
        {
            debugLog.Dequeue();
        }
    }

    private static RegisterReading ToReading(RegisterDefinition definition, ushort raw)
    {
        var sourceValue = definition.ValueKind switch
        {
            RegisterValueKind.SignedWord => unchecked((short)raw),
            RegisterValueKind.HighByte => (raw >> 8) & 0xFF,
            RegisterValueKind.LowByte => raw & 0xFF,
            _ => raw
        };
        var value = sourceValue * definition.Scale;
        var mappedText = definition.ValueMap is not null && definition.ValueMap.TryGetValue((int)sourceValue, out var label)
            ? label
            : null;
        var formatted = mappedText is not null
            ? $"{mappedText} ({sourceValue})"
            : string.IsNullOrEmpty(definition.Unit)
            ? value.ToString("0.###")
            : $"{value:0.###} {definition.Unit}";

        return new RegisterReading(
            definition.Key,
            definition.Name,
            definition.Address,
            raw,
            value,
            definition.Unit,
            formatted,
            definition.Notes);
    }

    private static IEnumerable<RegisterReading> CreateDerivedReadings(InverterDefinition definition, IReadOnlyList<RegisterReading> readings)
    {
        var byKey = readings.ToDictionary(reading => reading.Key, StringComparer.OrdinalIgnoreCase);
        var created = new List<RegisterReading>();

        foreach (var derived in definition.DerivedReadings)
        {
            if (!string.Equals(derived.Operation, "sum", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(derived.Operation, "product", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceReadings = derived.Sources
                .Select(source => byKey.TryGetValue(source, out var reading) ? reading : null)
                .ToArray();

            if (sourceReadings.Any(reading => reading?.Value is null))
            {
                continue;
            }

            var total = string.Equals(derived.Operation, "product", StringComparison.OrdinalIgnoreCase)
                ? sourceReadings.Aggregate(1d, (current, reading) => current * reading!.Value!.Value)
                : sourceReadings.Sum(reading => reading!.Value!.Value);
            var formatted = string.IsNullOrEmpty(derived.Unit)
                ? total.ToString("0.###")
                : $"{total:0.###} {derived.Unit}";

            var reading = new RegisterReading(
                derived.Key,
                derived.Name,
                -1,
                0,
                total,
                derived.Unit,
                formatted,
                derived.Notes);
            byKey[reading.Key] = reading;
            created.Add(reading);
        }

        return created;
    }
}
