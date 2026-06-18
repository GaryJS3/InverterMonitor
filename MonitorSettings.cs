namespace InverterMonitor;

public sealed record MonitorSettings
{
    public string GatewayHost { get; init; } = "10.44.0.173";
    public int GatewayPort { get; init; } = 4196;
    public byte SlaveId { get; init; } = 1;
    public int PollIntervalMs { get; init; } = 2000;
    public string InverterDefinitionId { get; init; } = "srne-sun-gold-sph8048";
    public string Brand { get; init; } = "SRNE / Sun Gold Power";
    public string Model { get; init; } = "SPH8048";
    public bool PollingEnabled { get; init; } = true;
    public MqttSettings Mqtt { get; init; } = new();
}

public sealed record MqttSettings
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 1883;
    public string ClientId { get; init; } = "invertermonitor";
    public string TopicPrefix { get; init; } = "invertermonitor";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public bool Retain { get; init; } = true;
    public bool HomeAssistantDiscovery { get; init; } = true;
}
