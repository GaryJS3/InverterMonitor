using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace InverterMonitor;

public sealed class MonitorState
{
    private readonly object gate = new();
    private readonly Channel<MonitorSnapshot> updates = Channel.CreateUnbounded<MonitorSnapshot>();
    private MonitorSnapshot snapshot;

    public MonitorState(MonitorSettings initialSettings)
    {
        Settings = Normalize(initialSettings);
        snapshot = MonitorSnapshot.Empty(Settings);
    }

    public MonitorSettings Settings { get; private set; }

    public void UpdateSettings(MonitorSettings settings)
    {
        Settings = Normalize(settings);

        Publish(snapshot with { Settings = Settings, UpdatedAt = DateTimeOffset.UtcNow });
    }

    private static MonitorSettings Normalize(MonitorSettings settings)
    {
        return settings with
        {
            GatewayHost = string.IsNullOrWhiteSpace(settings.GatewayHost) ? "10.44.0.173" : settings.GatewayHost.Trim(),
            GatewayPort = Math.Clamp(settings.GatewayPort, 1, 65535),
            PollIntervalMs = Math.Clamp(settings.PollIntervalMs, 500, 60000),
            InverterDefinitionId = string.IsNullOrWhiteSpace(settings.InverterDefinitionId) ? "srne-sun-gold-sph8048" : settings.InverterDefinitionId.Trim(),
            Brand = string.IsNullOrWhiteSpace(settings.Brand) ? "SRNE / Sun Gold Power" : settings.Brand.Trim(),
            Model = string.IsNullOrWhiteSpace(settings.Model) ? "Unknown" : settings.Model.Trim(),
            Mqtt = settings.Mqtt with
            {
                Host = settings.Mqtt.Host.Trim(),
                Port = Math.Clamp(settings.Mqtt.Port, 1, 65535),
                ClientId = string.IsNullOrWhiteSpace(settings.Mqtt.ClientId) ? "invertermonitor" : settings.Mqtt.ClientId.Trim(),
                TopicPrefix = NormalizeTopicPrefix(settings.Mqtt.TopicPrefix),
                Username = settings.Mqtt.Username.Trim()
            }
        };
    }

    private static string NormalizeTopicPrefix(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "invertermonitor" : value.Trim();
        return trimmed.Trim('/');
    }

    public MonitorSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return snapshot;
        }
    }

    public void Publish(MonitorSnapshot next)
    {
        lock (gate)
        {
            snapshot = next;
        }

        updates.Writer.TryWrite(next);
    }

    public async IAsyncEnumerable<MonitorSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return GetSnapshot();

        while (await updates.Reader.WaitToReadAsync(cancellationToken))
        {
            while (updates.Reader.TryRead(out var next))
            {
                yield return next;
            }
        }
    }
}

public sealed record MonitorSnapshot(
    DateTimeOffset UpdatedAt,
    MonitorSettings Settings,
    bool Connected,
    string Status,
    MqttStatus Mqtt,
    IReadOnlyList<RegisterReading> Readings,
    IReadOnlyList<string> DebugLog)
{
    public static MonitorSnapshot Empty(MonitorSettings settings) =>
        new(DateTimeOffset.UtcNow, settings, false, "Waiting for first poll.", MqttStatus.Disabled, [], []);
}

public sealed record MqttStatus(
    bool Enabled,
    bool Connected,
    string Status,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError)
{
    public static MqttStatus Disabled { get; } = new(false, false, "Disabled", null, null, null);
}

public sealed record RegisterReading(
    string Key,
    string Name,
    int Address,
    int RawValue,
    double? Value,
    string Unit,
    string FormattedValue,
    string? Notes);
