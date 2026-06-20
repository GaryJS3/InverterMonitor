let inverterDefinitions = [];

async function loadSettings() {
  inverterDefinitions = await fetch("/api/inverter-definitions").then(r => r.json());
  renderDefinitionOptions();
  const settings = await fetch("/api/settings").then(r => r.json());
  setForm(settings);
}

function renderDefinitionOptions() {
  inverterDefinitionId.innerHTML = "";
  for (const definition of inverterDefinitions) {
    const option = document.createElement("option");
    option.value = definition.id;
    option.textContent = `${definition.brand} ${definition.model}`;
    inverterDefinitionId.appendChild(option);
  }
}

function setForm(settings) {
  gatewayHost.value = settings.gatewayHost;
  gatewayPort.value = settings.gatewayPort;
  slaveId.value = settings.slaveId;
  pollIntervalMs.value = settings.pollIntervalMs;
  inverterDefinitionId.value = settings.inverterDefinitionId;
  brand.value = settings.brand;
  model.value = settings.model;
  pollingEnabled.checked = settings.pollingEnabled;
  mqttEnabled.checked = settings.mqtt.enabled;
  mqttHost.value = settings.mqtt.host;
  mqttPort.value = settings.mqtt.port;
  mqttClientId.value = settings.mqtt.clientId;
  mqttTopicPrefix.value = settings.mqtt.topicPrefix;
  mqttUsername.value = settings.mqtt.username;
  mqttPassword.value = settings.mqtt.password ?? "";
  mqttRetain.checked = settings.mqtt.retain;
  mqttHomeAssistantDiscovery.checked = settings.mqtt.homeAssistantDiscovery;
  mqttHomeAssistantDiscoveryPrefix.value = settings.mqtt.homeAssistantDiscoveryPrefix ?? "homeassistant";
  subtitle.textContent = `${settings.brand} ${settings.model}`;
}

function getForm() {
  return {
    gatewayHost: gatewayHost.value,
    gatewayPort: Number(gatewayPort.value),
    slaveId: Number(slaveId.value),
    pollIntervalMs: Number(pollIntervalMs.value),
    inverterDefinitionId: inverterDefinitionId.value,
    brand: brand.value,
    model: model.value,
    pollingEnabled: pollingEnabled.checked,
    mqtt: {
      enabled: mqttEnabled.checked,
      host: mqttHost.value,
      port: Number(mqttPort.value),
      clientId: mqttClientId.value,
      topicPrefix: mqttTopicPrefix.value,
      username: mqttUsername.value,
      password: mqttPassword.value,
      retain: mqttRetain.checked,
      homeAssistantDiscovery: mqttHomeAssistantDiscovery.checked,
      homeAssistantDiscoveryPrefix: mqttHomeAssistantDiscoveryPrefix.value
    }
  };
}

inverterDefinitionId.addEventListener("change", () => {
  const selected = inverterDefinitions.find(definition => definition.id === inverterDefinitionId.value);
  if (selected) {
    brand.value = selected.brand;
    model.value = selected.model;
    if (selected.protocol?.slaveId) {
      slaveId.value = selected.protocol.slaveId;
    }
  }
});

async function saveSettings() {
  saveFeedback.textContent = "Saving...";
  const response = await fetch("/api/settings", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(getForm())
  });
  if (!response.ok) {
    saveFeedback.textContent = `Save failed: ${response.status}`;
    return;
  }
  setForm(await response.json());
  saveFeedback.textContent = `Saved ${new Date().toLocaleTimeString()}`;
}

save.addEventListener("click", saveSettings);
saveMqtt.addEventListener("click", saveSettings);

function render(snapshot) {
  status.textContent = snapshot.connected ? "Connected" : snapshot.status;
  status.className = `status ${snapshot.connected ? "ok" : "bad"}`;
  renderMqttStatus(snapshot.mqtt);
  subtitle.textContent = `${snapshot.settings.brand} ${snapshot.settings.model}`;

  values.innerHTML = "";
  for (const reading of snapshot.readings) {
    const node = document.createElement("div");
    node.className = "value";
    node.innerHTML = `
      <div class="name">${escapeHtml(reading.name)}</div>
      <div class="reading">${escapeHtml(reading.formattedValue)}</div>
      <div class="raw">0x${reading.address.toString(16).padStart(4, "0").toUpperCase()} raw ${reading.rawValue}</div>
    `;
    values.appendChild(node);
  }

  debug.textContent = snapshot.debugLog.join("\n");
}

function renderMqttStatus(mqtt) {
  if (!mqtt?.enabled) {
    mqttStatus.textContent = "MQTT Disabled";
    mqttStatus.className = "status";
    return;
  }

  if (mqtt.connected && !mqtt.lastError) {
    mqttStatus.textContent = `MQTT ${mqtt.status}`;
    mqttStatus.className = "status ok";
    return;
  }

  mqttStatus.textContent = mqtt.lastError ? `MQTT ${mqtt.status}: ${mqtt.lastError}` : `MQTT ${mqtt.status}`;
  mqttStatus.className = mqtt.connected ? "status warn" : "status bad";
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

loadSettings();

const events = new EventSource("/api/events");
events.onmessage = event => render(JSON.parse(event.data));
events.onerror = () => {
  status.textContent = "Event stream disconnected";
  status.className = "status bad";
};
