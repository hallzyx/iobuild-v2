using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IoBuild.Api.Devices;

public sealed record TelemetryMessage(int DeviceId, string EventId, DateTimeOffset OccurredAt, string Status, string ReportedJson, double EnergyKwh, double TemperatureC = 0, double VoltageV = 0);
public interface IInfluxTelemetrySink { Task WriteAsync(TelemetryMessage message, CancellationToken cancellationToken = default); }
public interface IDeviceMqttPublisher { Task PublishAsync(string topic, string payload, bool qos1, bool retain, CancellationToken cancellationToken = default); }

public sealed class DisabledInfluxTelemetrySink : IInfluxTelemetrySink
{
    public Task WriteAsync(TelemetryMessage message, CancellationToken cancellationToken = default) => throw new HttpRequestException("InfluxDB is not configured.");
}

public sealed class DisabledDeviceMqttPublisher : IDeviceMqttPublisher
{
    public Task PublishAsync(string topic, string payload, bool qos1, bool retain, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class DeviceRegistryService(IoBuildDbContext db, IDeviceMqttPublisher mqtt, Microsoft.Extensions.Configuration.IConfiguration configuration)
{
    public Task AnnounceAsync(Device device, CancellationToken cancellationToken = default) =>
        PublishWhenEnabledAsync($"registry/{device.Id}", JsonSerializer.Serialize(new { deviceId = device.Id, type = device.Type }), cancellationToken);

    public void QueueTombstone(int deviceId)
    {
        if (!db.DeviceRegistryTombstones.Local.Any(item => item.DeviceId == deviceId)) db.DeviceRegistryTombstones.Add(new DeviceRegistryTombstone { DeviceId = deviceId });
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        foreach (var device in await db.Devices.OrderBy(device => device.Id).ToListAsync(cancellationToken)) await AnnounceAsync(device, cancellationToken);
        var tombstones = await db.DeviceRegistryTombstones.Where(item => item.PublishedAt == null).OrderBy(item => item.CreatedAt).ToListAsync(cancellationToken);
        foreach (var tombstone in tombstones)
        {
            await PublishWhenEnabledAsync($"registry/{tombstone.DeviceId}", string.Empty, cancellationToken);
            tombstone.PublishedAt = DateTimeOffset.UtcNow;
            tombstone.PublishAttempts++;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task AnnounceAllAsync(CancellationToken cancellationToken = default) => ReconcileAsync(cancellationToken);

    private Task PublishWhenEnabledAsync(string topic, string payload, CancellationToken cancellationToken) =>
        configuration.GetValue<bool>("Mqtt:Enabled")
            ? mqtt.PublishAsync(topic, payload, qos1: true, retain: true, cancellationToken)
            : Task.CompletedTask;
}

public sealed class DeviceRegistryAnnouncer(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DeviceRegistryService>().ReconcileAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<DeviceCommandService>().RepublishPendingAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class DeviceCommandService(IoBuildDbContext db, IDeviceMqttPublisher mqtt)
{
    public Task<DeviceCommand> SendAsync(int deviceId, int requestingOwnerId, string attribute, string value, CancellationToken cancellationToken = default) =>
        SendAsync(deviceId, requestingOwnerId, attribute, JsonSerializer.SerializeToElement(value), cancellationToken);

    public async Task<DeviceCommand> SendAsync(int deviceId, int requestingOwnerId, string attribute, JsonElement value, CancellationToken cancellationToken = default)
    {
        using var lease = await DeviceStateLocks.EnterAsync(deviceId, cancellationToken);
        return await SendLockedAsync(deviceId, requestingOwnerId, attribute, value, cancellationToken);
    }

    public async Task<DeviceCommand> SendAuthorizedAsync(int deviceId, int requestingOwnerId, string? requestingRole, string attribute, JsonElement value, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(requestingRole, "Owner", StringComparison.Ordinal)) throw new UnauthorizedAccessException("Only unit owners may send device commands.");
        using var lease = await DeviceStateLocks.EnterAsync(deviceId, cancellationToken);
        var device = await db.Devices.FindAsync([deviceId], cancellationToken) ?? throw new KeyNotFoundException("Device not found.");
        if (!device.UnitId.HasValue) throw new UnauthorizedAccessException("Device is not assigned to a unit.");
        if (!await db.UnitOwnerProjections.AnyAsync(item => item.UnitId == device.UnitId && item.OwnerUserId == requestingOwnerId, cancellationToken)) throw new UnauthorizedAccessException("You do not own this unit or ownership has not yet propagated.");
        return await SendLockedAsync(deviceId, requestingOwnerId, attribute, value, cancellationToken);
    }

    private async Task<DeviceCommand> SendLockedAsync(int deviceId, int requestingOwnerId, string attribute, JsonElement value, CancellationToken cancellationToken)
    {
        var device = await db.Devices.FindAsync([deviceId], cancellationToken) ?? throw new KeyNotFoundException("Device not found.");
        if (device.OwnerId != requestingOwnerId) throw new UnauthorizedAccessException("Device owner is required.");
        ValidateCommand(device.Type, attribute, value);
        var shadow = await db.DeviceShadows.FindAsync([deviceId], cancellationToken);
        var desired = shadow?.DesiredJson is { Length: > 0 } json ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [] : [];
        desired[attribute] = value;
        var desiredJson = JsonSerializer.Serialize(desired);
        if (shadow is null)
        {
            shadow = new DeviceShadow { DeviceId = deviceId, DesiredJson = desiredJson, ShadowVersion = 1, UpdatedAt = DateTimeOffset.UtcNow };
            db.DeviceShadows.Add(shadow);
        }
        else { shadow.DesiredJson = desiredJson; shadow.ShadowVersion++; shadow.UpdatedAt = DateTimeOffset.UtcNow; }
        var command = new DeviceCommand { DeviceId = deviceId, CommandId = Guid.NewGuid().ToString("N"), DesiredJson = desiredJson, IssuedAt = DateTimeOffset.UtcNow };
        db.DeviceCommands.Add(command);
        // The command and desired shadow are durable before any broker side effect.
        await db.SaveChangesAsync(cancellationToken);
        await PublishAndMarkAsync(command, cancellationToken);
        return command;
    }

    public async Task RepublishPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await db.DeviceCommands
            .Where(command => command.AcknowledgedAt == null)
            .OrderBy(command => command.DeviceId).ThenBy(command => command.IssuedAt).ThenBy(command => command.Id)
            .ToListAsync(cancellationToken);
        foreach (var command in pending) await PublishAndMarkAsync(command, cancellationToken);
    }

    private async Task PublishAndMarkAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        await mqtt.PublishAsync($"commands/{command.DeviceId}", command.DesiredJson, qos1: true, retain: true, cancellationToken);
        command.PublishAttempts++;
        command.PublishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateCommand(string type, string attribute, JsonElement value)
    {
        var valid = type switch
        {
            "AirConditioner" => attribute is "power" or "mode" or "targetTemperature",
            "SmartLight" => attribute is "power" or "brightness",
            _ => false
        };
        if (!valid) throw new ArgumentException($"Attribute '{attribute}' is not valid for device type '{type}'.");
        if (attribute == "brightness" && (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var brightness) || brightness < 0 || brightness > 100)) throw new ArgumentException("brightness must be a number from 0 to 100.");
        if (attribute == "targetTemperature" && (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var temperature) || temperature < 16 || temperature > 30)) throw new ArgumentException("targetTemperature must be a number from 16 to 30.");
        if (attribute == "power" && value.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw new ArgumentException("power must be a boolean.");
        if (attribute == "mode" && (value.ValueKind != JsonValueKind.String || value.GetString() is not ("cooling" or "heating" or "fan"))) throw new ArgumentException("mode must be cooling, heating, or fan.");
    }
}

public sealed class DeviceTelemetryService(IoBuildDbContext db, IInfluxTelemetrySink influx)
{
    public async Task<bool> IngestAsync(TelemetryMessage message, CancellationToken cancellationToken = default)
    {
        using var lease = await DeviceStateLocks.EnterAsync(message.DeviceId, cancellationToken);
        return await IngestLockedAsync(message, cancellationToken);
    }

    private async Task<bool> IngestLockedAsync(TelemetryMessage message, CancellationToken cancellationToken)
    {
        var existing = await db.DeviceTelemetry.SingleOrDefaultAsync(item => item.EventId == message.EventId, cancellationToken);
        if (existing is not null)
        {
            if (existing.InfluxWrittenAt is null)
            {
                if (!await db.TelemetryRecoveries.AnyAsync(item => item.EventId == existing.EventId, cancellationToken))
                {
                    db.TelemetryRecoveries.Add(new TelemetryRecovery { EventId = existing.EventId, CreatedAt = DateTimeOffset.UtcNow });
                    await db.SaveChangesAsync(cancellationToken);
                }
                try { await ReplayExistingAsync(existing, cancellationToken); }
                catch (HttpRequestException) { }
            }
            return true;
        }
        if (await db.Devices.FindAsync([message.DeviceId], cancellationToken) is null) return false;
        var shadow = await db.DeviceShadows.FindAsync([message.DeviceId], cancellationToken);
        var isNewer = shadow is null || message.OccurredAt > shadow.ReportedAt;
        var record = new DeviceTelemetry { DeviceId = message.DeviceId, EventId = message.EventId, OccurredAt = message.OccurredAt, Status = message.Status, ReportedJson = message.ReportedJson, EnergyKwh = message.EnergyKwh, TemperatureC = message.TemperatureC, VoltageV = message.VoltageV };
        db.DeviceTelemetry.Add(record);
        if (isNewer)
        {
            if (shadow is null) { shadow = new DeviceShadow { DeviceId = message.DeviceId, ReportedJson = message.ReportedJson, ReportedAt = message.OccurredAt, UpdatedAt = DateTimeOffset.UtcNow }; db.DeviceShadows.Add(shadow); }
            else { shadow.ReportedJson = message.ReportedJson; shadow.ReportedAt = message.OccurredAt; }
        }
        var command = await db.DeviceCommands.SingleOrDefaultAsync(item => item.CommandId == message.EventId, cancellationToken);
        // The read-only legacy simulator does not echo a command ID. Its immediate
        // telemetry acknowledgement does echo the complete desired document, so use
        // that durable payload only for the newest matching unacknowledged command.
        if (command is null)
        {
            var pending = await db.DeviceCommands
                .Where(item => item.DeviceId == message.DeviceId && item.AcknowledgedAt == null && item.IssuedAt <= message.OccurredAt)
                .OrderByDescending(item => item.IssuedAt)
                .ToListAsync(cancellationToken);
            command = pending.FirstOrDefault(item => EquivalentJson(item.DesiredJson, message.ReportedJson));
        }
        if (command is not null) command.AcknowledgedAt = message.OccurredAt;
        // The recovery intent is committed with the telemetry identity and shadow. A process
        // crash after this save can only leave a replayable intent, never an untracked write.
        db.TelemetryRecoveries.Add(new TelemetryRecovery { EventId = message.EventId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        // Shadow ordering controls only the current reported state. Every unique telemetry
        // event remains an Influx observation, including an out-of-order event.
        try { await ReplayExistingAsync(record, cancellationToken); }
        catch (HttpRequestException) { }
        return true;
    }

    private async Task ReplayExistingAsync(DeviceTelemetry record, CancellationToken cancellationToken)
    {
        await influx.WriteAsync(new TelemetryMessage(record.DeviceId, record.EventId, record.OccurredAt, record.Status, record.ReportedJson, record.EnergyKwh, record.TemperatureC, record.VoltageV), cancellationToken);
        record.InfluxWrittenAt = DateTimeOffset.UtcNow;
        var intent = await db.TelemetryRecoveries.SingleOrDefaultAsync(item => item.EventId == record.EventId, cancellationToken);
        if (intent is not null) db.TelemetryRecoveries.Remove(intent);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool EquivalentJson(string expected, string actual)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)); }
        catch (JsonException) { return false; }
    }

    public async Task<int> ReplayInfluxAsync(CancellationToken cancellationToken = default)
    {
        var recoveries = await db.TelemetryRecoveries.OrderBy(item => item.Id).ToListAsync(cancellationToken); var replayed = 0;
        foreach (var recovery in recoveries)
        {
            var record = await db.DeviceTelemetry.SingleAsync(item => item.EventId == recovery.EventId, cancellationToken);
            try { await ReplayExistingAsync(record, cancellationToken); replayed++; }
            catch (HttpRequestException) { break; }
        }
        return replayed;
    }
}

public sealed class InfluxHttpTelemetrySink(HttpClient client, Microsoft.Extensions.Configuration.IConfiguration configuration) : IInfluxTelemetrySink
{
    public async Task WriteAsync(TelemetryMessage message, CancellationToken cancellationToken = default)
    {
        var url = configuration["Influx:Url"]; var org = configuration["Influx:Org"]; var bucket = configuration["Influx:Bucket"]; var token = configuration["Influx:Token"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(token)) throw new HttpRequestException("InfluxDB is not configured.");
        var endpoint = new Uri(new Uri(url.TrimEnd('/') + "/"), $"api/v2/write?org={Uri.EscapeDataString(org)}&bucket={Uri.EscapeDataString(bucket)}&precision=ns");
        var line = $"telemetry,deviceId={message.DeviceId} energy_kwh={message.EnergyKwh.ToString(System.Globalization.CultureInfo.InvariantCulture)},temperature_c={message.TemperatureC.ToString(System.Globalization.CultureInfo.InvariantCulture)},voltage_v={message.VoltageV.ToString(System.Globalization.CultureInfo.InvariantCulture)},status=\"{message.Status.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" {message.OccurredAt.ToUnixTimeMilliseconds() * 1_000_000}";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(line, System.Text.Encoding.UTF8, "text/plain") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", token);
        using var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
    }
}

public sealed class MqttDeviceTransport(Microsoft.Extensions.Configuration.IConfiguration configuration, IServiceScopeFactory scopes) : IDeviceMqttPublisher, IHostedService, IAsyncDisposable
{
    private readonly MQTTnet.IMqttClient client = new MQTTnet.MqttClientFactory().CreateMqttClient();
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private bool messageHandlerConfigured;
    private bool reconnectHandlerConfigured;
    private volatile bool stopping;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("Mqtt:Enabled")) return;
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (!messageHandlerConfigured)
            {
                client.ApplicationMessageReceivedAsync += async arguments =>
                {
                    var topic = arguments.ApplicationMessage.Topic ?? string.Empty;
                    if (!topic.StartsWith("telemetry/", StringComparison.Ordinal) || !int.TryParse(topic["telemetry/".Length..], out var id)) return;
                    try { var json = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(arguments.ApplicationMessage.Payload.ToArray())); var root = json.RootElement; var eventId = root.TryGetProperty("eventId", out var eventValue) ? eventValue.GetString() ?? $"{id}:{root.GetProperty("timestamp").GetString()}" : $"{id}:{root.GetProperty("timestamp").GetString()}"; var timestamp = root.TryGetProperty("timestamp", out var ts) ? DateTimeOffset.Parse(ts.GetString()!) : DateTimeOffset.UtcNow; var status = root.TryGetProperty("status", out var state) ? state.GetString() ?? "unknown" : "unknown"; var reported = root.TryGetProperty("reported", out var reportedValue) ? reportedValue.GetRawText() : "{}"; var energy = root.TryGetProperty("energy_kwh", out var energyValue) ? energyValue.GetDouble() : 0; var temperature = root.TryGetProperty("temperature_c", out var temperatureValue) ? temperatureValue.GetDouble() : 0; var voltage = root.TryGetProperty("voltage_v", out var voltageValue) ? voltageValue.GetDouble() : 0; using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<DeviceTelemetryService>().IngestAsync(new TelemetryMessage(id, eventId, timestamp, status, reported, energy, temperature, voltage)); } catch (Exception) { }
                };
                messageHandlerConfigured = true;
            }
            if (!reconnectHandlerConfigured)
            {
                client.DisconnectedAsync += async _ =>
                {
                    if (stopping || !configuration.GetValue<bool>("Mqtt:Enabled")) return;
                    // A container/broker restart can take longer than one connection attempt.
                    // Keep retrying the durable reconciliation path until the session is restored.
                    for (var attempt = 0; attempt < 30 && !stopping && !client.IsConnected; attempt++)
                    {
                        try { await StartAsync(CancellationToken.None); }
                        catch (HttpRequestException) { await Task.Delay(TimeSpan.FromSeconds(1)); }
                    }
                };
                reconnectHandlerConfigured = true;
            }
            if (client.IsConnected) return;
            var host = configuration["Mqtt:Host"] ?? "localhost"; var port = configuration.GetValue("Mqtt:Port", 1883);
            Exception? failure = null;
            for (var attempt = 0; attempt < 3 && !client.IsConnected; attempt++)
            {
                try { await client.ConnectAsync(new MQTTnet.MqttClientOptionsBuilder().WithTcpServer(host, port).WithClientId("iobuild-devices").WithCleanSession().Build(), cancellationToken); }
                catch (Exception exception) when (attempt < 2) { failure = exception; await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken); }
            }
            if (!client.IsConnected) throw new HttpRequestException("MQTT is not configured.", failure);
            await client.SubscribeAsync(new MQTTnet.MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter => filter.WithTopic("telemetry/#").WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)).Build(), cancellationToken);
            // A broker session may be empty after a restart. Re-publish durable registry and
            // every unacknowledged command only after the subscription is live.
            using (var scope = scopes.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<DeviceRegistryService>().AnnounceAllAsync(cancellationToken);
                await scope.ServiceProvider.GetRequiredService<DeviceCommandService>().RepublishPendingAsync(cancellationToken);
            }
        }
        finally { connectionGate.Release(); }
    }
    public async Task StopAsync(CancellationToken cancellationToken) { stopping = true; if (client.IsConnected) await client.DisconnectAsync(new MQTTnet.MqttClientDisconnectOptions(), cancellationToken); }
    public async Task PublishAsync(string topic, string payload, bool qos1, bool retain, CancellationToken cancellationToken = default)
    {
        if (!client.IsConnected) await StartAsync(cancellationToken);
        if (!client.IsConnected) throw new HttpRequestException("MQTT is not configured.");
        var message = new MQTTnet.MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithQualityOfServiceLevel(qos1 ? MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce : MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce).WithRetainFlag(retain).Build();
        await client.PublishAsync(message, cancellationToken);
    }
    public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
}

public sealed record DeviceCatalogEntry(string Code, string DisplayName, string Scope);
public static class DeviceCatalog
{
    private static readonly IReadOnlyDictionary<string, DeviceCatalogEntry> Entries = new Dictionary<string, DeviceCatalogEntry>(StringComparer.Ordinal)
    {
        ["SmartMeter"] = new("SmartMeter", "Smart Meter", "floor"),
        ["WaterSensor"] = new("WaterSensor", "Water Sensor", "floor"),
        ["SmokeDetector"] = new("SmokeDetector", "Smoke Detector", "floor"),
        ["AirConditioner"] = new("AirConditioner", "Air Conditioner", "unit"),
        ["SmartLight"] = new("SmartLight", "Smart Light", "unit")
    };
    public static DeviceCatalogEntry? Find(string code) => Entries.GetValueOrDefault(code);
}


internal static class DeviceStateLocks
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();
    public static async Task<IDisposable> EnterAsync(int deviceId, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(deviceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }
    private sealed class Releaser(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
}
