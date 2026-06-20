using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MQTTnet;

namespace InverterMonitor;

public sealed class MqttPublisher(ILogger<MqttPublisher> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IMqttClient? client;
    private string activeConnectionKey = "";
    private MqttStatus status = MqttStatus.Disabled;

    public MqttStatus Status => status;

    public async Task<(MqttStatus Status, string? Message)> PublishAsync(MonitorSnapshot snapshot, CancellationToken cancellationToken)
    {
        var mqtt = snapshot.Settings.Mqtt;
        if (!mqtt.Enabled)
        {
            await DisconnectAsync(cancellationToken);
            status = MqttStatus.Disabled;
            return (status, null);
        }

        if (string.IsNullOrWhiteSpace(mqtt.Host))
        {
            status = new MqttStatus(true, false, "Broker host is blank", null, DateTimeOffset.UtcNow, "Broker host is blank");
            return (status, "MQTT enabled but broker host is blank.");
        }

        try
        {
            status = status with { Enabled = true, Status = "Publishing", LastAttemptAt = DateTimeOffset.UtcNow, LastError = null };
            await EnsureConnectedAsync(mqtt, cancellationToken);

            var prefix = SanitizeTopic(mqtt.TopicPrefix);
            var publicSnapshot = snapshot.RedactSecrets();
            await PublishStringAsync($"{prefix}/status", snapshot.Connected ? "online" : "offline", mqtt.Retain, cancellationToken);
            await PublishStringAsync($"{prefix}/snapshot", JsonSerializer.Serialize(publicSnapshot, JsonOptions), mqtt.Retain, cancellationToken);

            foreach (var reading in snapshot.Readings)
            {
                var key = SanitizeTopicPart(reading.Key);
                if (mqtt.HomeAssistantDiscovery)
                {
                    await PublishHomeAssistantDiscoveryAsync(prefix, key, reading, snapshot, cancellationToken);
                }

                if (reading.Value is not null)
                {
                    await PublishStringAsync(
                        $"{prefix}/readings/{key}/value",
                        reading.Value.Value.ToString("0.###", CultureInfo.InvariantCulture),
                        mqtt.Retain,
                        cancellationToken);
                }

                await PublishStringAsync($"{prefix}/readings/{key}/formatted", reading.FormattedValue, mqtt.Retain, cancellationToken);
                await PublishStringAsync($"{prefix}/readings/{key}/raw", reading.RawValue.ToString(CultureInfo.InvariantCulture), mqtt.Retain, cancellationToken);
            }

            status = new MqttStatus(true, client?.IsConnected == true, "Published", DateTimeOffset.UtcNow, status.LastAttemptAt, null);
            var discoveryNote = mqtt.HomeAssistantDiscovery
                ? $" and {snapshot.Readings.Count} retained Home Assistant discovery configs under {mqtt.HomeAssistantDiscoveryPrefix}"
                : "";
            return (status, $"MQTT published {snapshot.Readings.Count} readings{discoveryNote} to {mqtt.Host}:{mqtt.Port}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MQTT publish failed");
            await SafeDisconnectAsync();
            status = new MqttStatus(true, false, "Publish failed", status.LastSuccessAt, DateTimeOffset.UtcNow, ex.Message);
            return (status, $"MQTT publish failed: {ex.Message}");
        }
    }

    private async Task EnsureConnectedAsync(MqttSettings settings, CancellationToken cancellationToken)
    {
        var connectionKey = $"{settings.Host}|{settings.Port}|{settings.ClientId}|{settings.Username}|{GetPasswordFingerprint(settings.Password)}";
        if (client is { IsConnected: true } && string.Equals(activeConnectionKey, connectionKey, StringComparison.Ordinal))
        {
            return;
        }

        await DisconnectAsync(cancellationToken);

        client = new MqttClientFactory().CreateMqttClient();
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(settings.ClientId)
            .WithTcpServer(settings.Host, settings.Port)
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(5));

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            builder = builder.WithCredentials(settings.Username, settings.Password);
        }

        await client.ConnectAsync(builder.Build(), cancellationToken);
        activeConnectionKey = connectionKey;
        status = status with { Enabled = true, Connected = true, Status = "Connected", LastAttemptAt = DateTimeOffset.UtcNow, LastError = null };
    }

    private async Task PublishStringAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        if (client is not { IsConnected: true })
        {
            throw new InvalidOperationException("MQTT client is not connected.");
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();

        await client.PublishAsync(message, cancellationToken);
    }

    private async Task PublishHomeAssistantDiscoveryAsync(
        string prefix,
        string key,
        RegisterReading reading,
        MonitorSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var discoveryNodeId = SanitizeTopicPart(prefix);
        var uniqueId = $"{discoveryNodeId}_{key}";
        var stateTopic = $"{prefix}/readings/{key}/value";
        var configTopic = $"{SanitizeTopic(snapshot.Settings.Mqtt.HomeAssistantDiscoveryPrefix)}/sensor/{discoveryNodeId}/{key}/config";
        var payload = new Dictionary<string, object?>
        {
            ["name"] = reading.Name,
            ["unique_id"] = uniqueId,
            ["object_id"] = uniqueId,
            ["state_topic"] = stateTopic,
            ["availability_topic"] = $"{prefix}/status",
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
            ["value_template"] = "{{ value }}",
            ["device"] = new
            {
                identifiers = new[] { prefix },
                name = "InverterMonitor",
                manufacturer = snapshot.Settings.Brand,
                model = snapshot.Settings.Model
            }
        };

        if (!string.IsNullOrWhiteSpace(reading.Unit))
        {
            payload["unit_of_measurement"] = reading.Unit;
        }

        var deviceClass = GetHomeAssistantDeviceClass(reading);
        if (deviceClass is not null)
        {
            payload["device_class"] = deviceClass;
            payload["state_class"] = deviceClass == "energy" ? "total_increasing" : "measurement";
        }

        await PublishStringAsync(configTopic, JsonSerializer.Serialize(payload, JsonOptions), true, cancellationToken);
    }

    private static string? GetHomeAssistantDeviceClass(RegisterReading reading)
    {
        return reading.Unit switch
        {
            "W" => "power",
            "VA" => "apparent_power",
            "V" => "voltage",
            "A" => "current",
            "Hz" => "frequency",
            "kWh" => "energy",
            "%" when reading.Key.Contains("soc", StringComparison.OrdinalIgnoreCase) => "battery",
            _ => null
        };
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (client is { IsConnected: true })
        {
            await client.DisconnectAsync(cancellationToken: cancellationToken);
        }

        client?.Dispose();
        client = null;
        activeConnectionKey = "";
    }

    private async Task SafeDisconnectAsync()
    {
        try
        {
            await DisconnectAsync(CancellationToken.None);
        }
        catch
        {
        }
    }

    private static string SanitizeTopic(string topic)
    {
        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeTopicPart);
        return string.Join('/', parts);
    }

    private static string GetPasswordFingerprint(string password)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(hash);
    }

    private static string SanitizeTopicPart(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_')
            .ToArray();
        var result = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "value" : result;
    }
}
