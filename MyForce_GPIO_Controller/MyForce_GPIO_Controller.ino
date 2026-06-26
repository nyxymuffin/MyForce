// MyForce GPIO Relay Controller, ESP32 DevKit V1 + W5500 Lite firmware
// Hardware: ESP32 DevKit V1, Wiznet W5500 Lite Ethernet module (SPI)
// Role: external MQTT control-plane client that drives aux relay outputs and
//       reads input channels (PROJECT_FRAMEWORK.md §3.2, §4.5, §5.2).
//
// NOTE: This controller drives NON-radio, latency-tolerant aux hardware only.
//       It never handles radio PTT, that keying is AP-owned (§3.4, §3.6.3).
//
// Libraries required (Arduino Library Manager):
//   - "Ethernet"     (Wiznet W5100/W5200/W5500 driver)
//   - "PubSubClient" by Nick O'Leary  (MQTT client)
//   - "ArduinoJson"  by Benoit Blanchon (v7+, JSON payloads, §5.8)
//   - "Wire"         (Arduino core I2C, only used by the XL9535 relay backend)

#include <SPI.h>
#include <Ethernet.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <Wire.h>   // I2C master for the XL9535-K16V5 relay backend

// ---------------------------------------------------------------------------
// SPI pin mapping (ESP32 DevKit V1 -> W5500 Lite, VSPI bus)
// ---------------------------------------------------------------------------
#define PIN_W5500_SCK   18   // VSPI SCK  -> W5500 SCLK
#define PIN_W5500_MISO  19   // VSPI MISO -> W5500 MISO
#define PIN_W5500_MOSI  23   // VSPI MOSI -> W5500 MOSI
#define PIN_W5500_CS    33   // Chip select -> W5500 SCS
#define PIN_W5500_RST   26   // Hardware reset -> W5500 RSTn (optional)

// ---------------------------------------------------------------------------
// Network configuration
// W5500 Lite has no on-board MAC, so supply a locally administered one.
// ---------------------------------------------------------------------------
static byte g_macAddress[] = { 0xDE, 0xAD, 0xBE, 0xEF, 0xFE, 0x01 };

// ---------------------------------------------------------------------------
// MQTT broker configuration
// NOTE: 10.4.32.0 is normally a network address. Verify this is the actual
// broker host; change here if it should be e.g. 10.4.32.1.
// ---------------------------------------------------------------------------
static IPAddress      g_brokerAddress(10, 43, 2, 2);   // MQTT broker IPv4
static const uint16_t MQTT_BROKER_PORT = 1883;         // Unencrypted (project default)

// ---------------------------------------------------------------------------
// Module identity (§5.2). All topics are addressed by this instance <id>.
// ---------------------------------------------------------------------------
static const char* MODULE_ID   = "gpio.relay1";   // unique per controller on the bus
static const char* MODULE_KIND = "external";      // radio_module | radio_resource | external
static const char* MODULE_CAT  = "gpio";          // radio | media | siren | scada | gpio
static const int   PAYLOAD_VERSION = 1;           // envelope "v" (§5.8.1)

// Admin credential for config-changing commands (§4.6). Dummy gate, not security.
static const char* ADMIN_PIN = "2135";

// ---------------------------------------------------------------------------
// Relay outputs & digital inputs
// Map each logical channel to an ESP32 GPIO. The number of channels and the
// named relay "functions" are what the controller self-describes so the UI can
// build its admin surface with no UI change (§4.5).
// Avoid strapping/boot pins (0, 2, 12, 15) for relays where possible.
// ---------------------------------------------------------------------------
enum RelayLogic {
  RELAY_ACTIVE_HIGH = 0,   // GPIO HIGH energises the relay coil
  RELAY_ACTIVE_LOW  = 1    // GPIO LOW  energises the relay coil (common on opto boards)
};
static const RelayLogic RELAY_LOGIC = RELAY_ACTIVE_LOW;

// ---------------------------------------------------------------------------
// Relay output backend (§3.2). The controller can drive its relay channels
// either through direct ESP32 GPIO pins or through an XL9535-K16V5 16-channel
// I2C relay board. The wire protocol (MQTT registry/state/cmd) is identical
// for both, only the physical drive differs, selected here at compile time.
// ---------------------------------------------------------------------------
enum RelayBackend {
  RELAY_BACKEND_GPIO   = 0,   // direct ESP32 GPIO pins via digitalWrite()
  RELAY_BACKEND_XL9535 = 1    // XL9535-K16V5 16-channel I2C relay board
};
static const RelayBackend RELAY_BACKEND = RELAY_BACKEND_GPIO;  // existing wiring default

// Relay channel pins (channel index is the array position + 1, i.e. 1-based on the bus).
// Only used when RELAY_BACKEND == RELAY_BACKEND_GPIO; ignored for the XL9535 board.
static const uint8_t g_relayPins[]  = { 13, 14, 27, 16 };
// Input channel pins (read with internal pull-ups; index is position + 1).
static const uint8_t g_inputPins[]  = { 32, 39, 34, 35 };  // 34/35/39 are input-only, no pull-up

// Human-facing relay function names, parallel to g_relayPins (§4.5). The array
// length defines RELAY_COUNT, so it also bounds the XL9535 channels used
// (channel N drives expander bit N-1; the board exposes up to 16).
static const char* g_relayFunctions[] = { "beacon", "floodlight", "horn", "aux" };

static const uint8_t RELAY_COUNT = sizeof(g_relayFunctions) / sizeof(g_relayFunctions[0]);
static const uint8_t INPUT_COUNT = sizeof(g_inputPins) / sizeof(g_inputPins[0]);

// ---------------------------------------------------------------------------
// XL9535-K16V5 16-channel I2C relay board (only used by RELAY_BACKEND_XL9535).
// The XL9535 is a 16-bit I2C I/O expander (PCA9555-compatible register map):
// two 8-bit ports drive the 16 relays. The board is ACTIVE HIGH, a 1 bit
// energises the relay coil. Default 7-bit I2C address is 0x20 (A0/A1/A2 open).
// ESP32 default I2C pins: SDA = GPIO21, SCL = GPIO22 (clear of the W5500 VSPI
// bus on 18/19/23/33/26).
// ---------------------------------------------------------------------------
static const uint8_t XL9535_I2C_ADDR = 0x20;   // A0/A1/A2 jumpers all open
static const uint8_t PIN_XL9535_SDA  = 21;     // I2C data  -> board SDA
static const uint8_t PIN_XL9535_SCL  = 22;     // I2C clock -> board SCL

// XL9535 / PCA9555 register addresses (named to avoid magic numbers).
enum Xl9535Reg {
  XL9535_INPUT_PORT0  = 0x00,   // relays 1-8  read-back
  XL9535_INPUT_PORT1  = 0x01,   // relays 9-16 read-back
  XL9535_OUTPUT_PORT0 = 0x02,   // relays 1-8  output latch
  XL9535_OUTPUT_PORT1 = 0x03,   // relays 9-16 output latch
  XL9535_CONFIG_PORT0 = 0x06,   // relays 1-8  direction (0 = output)
  XL9535_CONFIG_PORT1 = 0x07    // relays 9-16 direction (0 = output)
};

// Shadow of the 16 output bits (bit n = relay channel n+1, 1 = energised).
// We keep the full word locally and push both ports so a single-channel change
// never disturbs the others.
uint16_t g_xlOutputShadow = 0x0000;

// ---------------------------------------------------------------------------
// Power-on relay self-test. On boot we briefly energise one relay on the XL9535
// board to prove the I2C path and the board are alive (an audible click / lit
// LED). Channel 16 (bit 15) is used because it sits OUTSIDE the mapped function
// channels, so the test never drives a real wired output. Disable with the flag.
// ---------------------------------------------------------------------------
static const bool     BOOT_RELAY_TEST_ENABLED = true;
static const uint8_t  BOOT_RELAY_TEST_CHANNEL  = 16;    // 1-based; XL9535 bit 15
static const uint16_t BOOT_RELAY_TEST_MS       = 750;   // pulse duration

// Last-published relay/input state, so we only emit on change.
bool g_relayState[RELAY_COUNT];
bool g_inputState[INPUT_COUNT];

// ---------------------------------------------------------------------------
// Timing constants (ms) instead of bare magic numbers.
// ---------------------------------------------------------------------------
enum TimingMs {
  MQTT_RECONNECT_INTERVAL = 5000,   // wait between failed MQTT connect attempts
  STATE_PUBLISH_INTERVAL  = 10000,  // periodic full state refresh (retained heartbeat)
  INPUT_POLL_INTERVAL     = 50      // debounced input sampling cadence
};

// MQTT liveness tuning. The keepalive makes the client PING the broker on an idle
// link, so a silently dropped broker/LAN is detected within ~1-2 keepalive windows
// even when no commands are flowing; connected() then goes false and loop()
// reconnects. The socket timeout bounds how long a connect()/read may block so a
// dead broker never stalls the control loop (§5.2 presence).
static const uint16_t MQTT_KEEPALIVE_SECONDS     = 15;  // PINGREQ cadence on idle link
static const uint8_t  MQTT_SOCKET_TIMEOUT_SECONDS = 4;  // bound blocking socket ops

// Pulse bookkeeping: a relay can be commanded to auto-release after N ms.
unsigned long g_pulseExpiry[RELAY_COUNT];   // millis() deadline, 0 = no active pulse

// ---------------------------------------------------------------------------
// MQTT topic buffers, built once at boot from MODULE_ID.
// ---------------------------------------------------------------------------
char g_topicBase[64];      // myforce/module/<id>
char g_topicStatus[80];    // .../status   (LWT target)
char g_topicRegistry[80];  // .../registry
char g_topicConfig[80];    // .../config
char g_topicState[80];     // .../state
char g_topicCmdSub[80];    // .../cmd/#     (subscription wildcard)

// Ethernet + MQTT client objects layered on the W5500 SPI driver.
EthernetClient g_ethClient;
PubSubClient   g_mqttClient(g_ethClient);

// Non-blocking timers.
unsigned long g_lastReconnectAttempt = 0;
unsigned long g_lastStatePublish     = 0;
unsigned long g_lastInputPoll        = 0;

// ===========================================================================
// Helpers
// ===========================================================================

// --- XL9535 I2C relay backend -------------------------------------------------

// Write a single 8-bit register on the XL9535 expander.
void xlWriteReg(uint8_t reg, uint8_t value) {
  Wire.beginTransmission(XL9535_I2C_ADDR);
  Wire.write(reg);
  Wire.write(value);
  Wire.endTransmission();
}

// Push the 16-bit output shadow to both XL9535 output ports.
void xlPushOutputs() {
  xlWriteReg(XL9535_OUTPUT_PORT0, (uint8_t)(g_xlOutputShadow & 0xFF));   // relays 1-8
  xlWriteReg(XL9535_OUTPUT_PORT1, (uint8_t)(g_xlOutputShadow >> 8));     // relays 9-16
}

// Bring the XL9535 up: start I2C, drive all relays de-energised, then set both
// ports to outputs. Outputs are written BEFORE configuring direction so no
// relay glitches on (the latch holds 0 = de-energised, board is active high).
void xlBegin() {
  Wire.begin(PIN_XL9535_SDA, PIN_XL9535_SCL);
  g_xlOutputShadow = 0x0000;        // all off
  xlPushOutputs();
  xlWriteReg(XL9535_CONFIG_PORT0, 0x00);  // port 0 pins = outputs
  xlWriteReg(XL9535_CONFIG_PORT1, 0x00);  // port 1 pins = outputs
}

// Power-on self-test: pulse the test relay on the XL9535 board to prove relay
// function, with a serial banner so the behaviour is never a surprise. Drives the
// expander bit directly (the test channel is outside the mapped function range,
// so it bypasses setRelay()/g_relayState). No-op unless the XL9535 backend is in use.
void bootRelaySelfTest() {
  if (!BOOT_RELAY_TEST_ENABLED) {
    return;
  }
  if (RELAY_BACKEND != RELAY_BACKEND_XL9535) {
    Serial.println("BOOT SELF-TEST: skipped (XL9535 relay backend not selected).");
    return;
  }

  uint16_t mask = (uint16_t)1 << (BOOT_RELAY_TEST_CHANNEL - 1);   // 0-based expander bit
  Serial.print("BOOT SELF-TEST: energising relay ");
  Serial.print(BOOT_RELAY_TEST_CHANNEL);
  Serial.print(" on the XL9535 board for ");
  Serial.print(BOOT_RELAY_TEST_MS);
  Serial.println(" ms to prove relay function.");

  g_xlOutputShadow |= mask;    // energise the test relay
  xlPushOutputs();
  delay(BOOT_RELAY_TEST_MS);
  g_xlOutputShadow &= ~mask;   // release it again
  xlPushOutputs();

  Serial.print("BOOT SELF-TEST: relay ");
  Serial.print(BOOT_RELAY_TEST_CHANNEL);
  Serial.println(" released. (Disable with BOOT_RELAY_TEST_ENABLED = false.)");
}

// Drive one relay channel (1-based) to energised/de-energised, honouring the
// active-high/low wiring. Updates the cached state. Routes to the selected
// backend: direct GPIO or the XL9535 I2C board.
void setRelay(uint8_t channel, bool energised) {
  if (channel < 1 || channel > RELAY_COUNT) {
    return;  // out of range, ignored (the command path reports rejected)
  }
  uint8_t idx = channel - 1;

  if (RELAY_BACKEND == RELAY_BACKEND_XL9535) {
    // The XL9535-K16V5 board is active high (bit = 1 energises the relay), so
    // we map energised directly to the shadow bit and push the change.
    uint16_t mask = (uint16_t)1 << idx;
    if (energised) { g_xlOutputShadow |= mask; }
    else           { g_xlOutputShadow &= ~mask; }
    xlPushOutputs();
  } else {
    // Direct GPIO drive, honouring the opto board's active-high/low wiring.
    bool level = (RELAY_LOGIC == RELAY_ACTIVE_LOW) ? !energised : energised;
    digitalWrite(g_relayPins[idx], level ? HIGH : LOW);
  }

  g_relayState[idx] = energised;
}

// Resolve a relay "function" name (§4.5) to its 1-based channel, or 0 if unknown.
uint8_t channelForFunction(const char* function) {
  for (uint8_t i = 0; i < RELAY_COUNT; i++) {
    if (strcmp(function, g_relayFunctions[i]) == 0) {
      return i + 1;
    }
  }
  return 0;
}

// Publish a command acknowledgement on the sibling .../ack topic (§5.8.2).
// status is one of: "ok" | "rejected" | "error". errMsg is optional context.
void publishAck(const char* cmdTopic, const char* msgId,
                const char* status, const char* errMsg) {
  if (msgId == nullptr || msgId[0] == '\0') {
    return;  // no msg_id, cannot be individually acknowledged (§5.8.2)
  }
  char ackTopic[96];
  snprintf(ackTopic, sizeof(ackTopic), "%s/ack", cmdTopic);

  JsonDocument doc;
  doc["v"]      = PAYLOAD_VERSION;
  doc["ts"]     = "";                // populate via NTP if wall-clock time is required
  doc["msg_id"] = msgId;            // echo the command's msg_id
  doc["status"] = status;
  if (errMsg != nullptr) {
    JsonArray errors = doc["errors"].to<JsonArray>();
    JsonObject e = errors.add<JsonObject>();
    e["message"] = errMsg;
  }
  char buf[256];
  size_t n = serializeJson(doc, buf, sizeof(buf));
  g_mqttClient.publish(ackTopic, (const uint8_t*)buf, n, false);  // ack: not retained
}

// Publish the retained status/presence message (birth) (§5.8.4).
void publishStatus(bool online) {
  JsonDocument doc;
  doc["v"]      = PAYLOAD_VERSION;
  doc["ts"]     = "";
  doc["id"]     = MODULE_ID;
  doc["online"] = online;
  doc["health"] = online ? "available" : "unavailable";
  if (!online) {
    doc["reason"] = "offline";
  }
  char buf[160];
  size_t n = serializeJson(doc, buf, sizeof(buf));
  g_mqttClient.publish(g_topicStatus, (const uint8_t*)buf, n, true);  // retained
}

// Publish the retained registry: self-describing capabilities the UI renders
// its GPIO admin/control surface from (§4.5, §5.8.4).
void publishRegistry() {
  JsonDocument doc;
  doc["v"]         = PAYLOAD_VERSION;
  doc["ts"]        = "";
  doc["id"]        = MODULE_ID;
  doc["kind"]      = MODULE_KIND;       // external
  doc["category"]  = MODULE_CAT;        // gpio
  doc["removable"] = true;

  JsonObject caps = doc["capabilities"].to<JsonObject>();
  caps["relay_channels"] = RELAY_COUNT;
  caps["input_channels"] = INPUT_COUNT;
  // Which physical relay backend is driving the channels (§3.2), so the UI/admin
  // surface can show the hardware in use without any UI change (§4.5).
  caps["relay_backend"] = (RELAY_BACKEND == RELAY_BACKEND_XL9535) ? "xl9535_k16v5" : "esp32_gpio";
  // Named relay functions the UI can assign/trigger (§4.5).
  JsonArray fns = caps["relay_functions"].to<JsonArray>();
  for (uint8_t i = 0; i < RELAY_COUNT; i++) {
    JsonObject f = fns.add<JsonObject>();
    f["name"]    = g_relayFunctions[i];
    f["channel"] = i + 1;
  }
  // Supported operating actions on cmd/<action>.
  JsonArray acts = caps["actions"].to<JsonArray>();
  acts.add("set");     // { channel|function, state: on|off }
  acts.add("pulse");   // { channel|function, ms }

  char buf[512];
  size_t n = serializeJson(doc, buf, sizeof(buf));
  g_mqttClient.publish(g_topicRegistry, (const uint8_t*)buf, n, true);  // retained
}

// Publish the retained runtime state: every relay and input channel (§5.2 state).
void publishState() {
  JsonDocument doc;
  doc["v"]  = PAYLOAD_VERSION;
  doc["ts"] = "";
  doc["id"] = MODULE_ID;

  JsonArray relays = doc["relays"].to<JsonArray>();
  for (uint8_t i = 0; i < RELAY_COUNT; i++) {
    JsonObject r = relays.add<JsonObject>();
    r["channel"]  = i + 1;
    r["function"] = g_relayFunctions[i];
    r["state"]    = g_relayState[i] ? "on" : "off";
  }
  JsonArray inputs = doc["inputs"].to<JsonArray>();
  for (uint8_t i = 0; i < INPUT_COUNT; i++) {
    JsonObject in = inputs.add<JsonObject>();
    in["channel"] = i + 1;
    in["state"]   = g_inputState[i] ? "active" : "inactive";
  }
  char buf[512];
  size_t n = serializeJson(doc, buf, sizeof(buf));
  g_mqttClient.publish(g_topicState, (const uint8_t*)buf, n, true);  // retained
  g_lastStatePublish = millis();
}

// Resolve a command payload's target to a 1-based relay channel, accepting
// either an explicit "channel" int or a "function" name (§4.5). Returns 0 if
// neither resolves to a valid channel.
uint8_t resolveTargetChannel(JsonDocument& doc) {
  if (doc["channel"].is<int>()) {
    int ch = doc["channel"].as<int>();
    if (ch >= 1 && ch <= RELAY_COUNT) {
      return (uint8_t)ch;
    }
    return 0;
  }
  if (doc["function"].is<const char*>()) {
    return channelForFunction(doc["function"].as<const char*>());
  }
  return 0;
}

// ===========================================================================
// Command dispatch (§5.2 cmd/<action>, §5.8.2 ack)
// Topic shape: myforce/module/<id>/cmd/<action>
// ===========================================================================
void handleCommand(const char* action, const char* fullTopic,
                   byte* payload, unsigned int length) {
  // Parse the JSON envelope + body.
  JsonDocument doc;
  DeserializationError err = deserializeJson(doc, payload, length);
  if (err) {
    publishAck(fullTopic, "", "error", "malformed_json");
    return;
  }
  const char* msgId = doc["msg_id"] | "";  // may be empty

  // --- Operating commands (no admin auth) -------------------------------
  if (strcmp(action, "set") == 0) {
    // { channel|function, state: "on"|"off" }
    uint8_t ch = resolveTargetChannel(doc);
    if (ch == 0) {
      publishAck(fullTopic, msgId, "rejected", "unknown_channel");
      return;
    }
    const char* state = doc["state"] | "";
    bool energised;
    if (strcmp(state, "on") == 0)       { energised = true;  }
    else if (strcmp(state, "off") == 0) { energised = false; }
    else { publishAck(fullTopic, msgId, "rejected", "state_must_be_on_or_off"); return; }

    g_pulseExpiry[ch - 1] = 0;   // an explicit set cancels any active pulse
    setRelay(ch, energised);
    publishAck(fullTopic, msgId, "ok", nullptr);
    publishState();
    return;
  }

  if (strcmp(action, "pulse") == 0) {
    // { channel|function, ms: <duration> }  , energise, then auto-release.
    uint8_t ch = resolveTargetChannel(doc);
    if (ch == 0) {
      publishAck(fullTopic, msgId, "rejected", "unknown_channel");
      return;
    }
    long ms = doc["ms"] | 0;
    if (ms <= 0) {
      publishAck(fullTopic, msgId, "rejected", "ms_must_be_positive");
      return;
    }
    setRelay(ch, true);
    g_pulseExpiry[ch - 1] = millis() + (unsigned long)ms;  // serviced in loop()
    publishAck(fullTopic, msgId, "ok", nullptr);
    publishState();
    return;
  }

  // --- Admin command: config (requires admin PIN, §4.6) -----------------
  if (strcmp(action, "config") == 0) {
    const char* auth = doc["auth"] | "";
    if (strcmp(auth, ADMIN_PIN) != 0) {
      publishAck(fullTopic, msgId, "rejected", "auth_required");  // §4.6 / §5.8.2
      return;
    }
    // Apply config here (e.g. relay function names, active-low). For now we
    // acknowledge and re-publish the retained config snapshot as the new truth.
    publishAck(fullTopic, msgId, "ok", nullptr);
    publishRegistry();
    return;
  }

  // Unknown action.
  publishAck(fullTopic, msgId, "rejected", "unknown_action");
}

// ---------------------------------------------------------------------------
// mqttCallback, routes an inbound message to handleCommand by trailing action.
// ---------------------------------------------------------------------------
void mqttCallback(char* topic, byte* payload, unsigned int length) {
  // Topic shape: myforce/module/<id>/cmd/<action>. Extract <action>.
  const char* marker = strstr(topic, "/cmd/");
  if (marker == nullptr) {
    return;  // not a command topic
  }
  const char* action = marker + 5;          // skip "/cmd/"
  // Ignore our own ack echoes (…/cmd/<action>/ack).
  if (strstr(action, "/ack") != nullptr) {
    return;
  }
  handleCommand(action, topic, payload, length);
}

// ---------------------------------------------------------------------------
// Poll inputs; publish a fresh state snapshot on any change (debounced).
// ---------------------------------------------------------------------------
void pollInputs() {
  bool changed = false;
  for (uint8_t i = 0; i < INPUT_COUNT; i++) {
    // Inputs are wired active-low to ground with pull-ups (where available),
    // so a LOW reading means the contact is closed / active.
    bool active = (digitalRead(g_inputPins[i]) == LOW);
    if (active != g_inputState[i]) {
      g_inputState[i] = active;
      changed = true;
    }
  }
  if (changed) {
    publishState();
  }
}

// ---------------------------------------------------------------------------
// Service any relays running a timed pulse; auto-release when expired.
// ---------------------------------------------------------------------------
void servicePulses() {
  unsigned long now = millis();
  bool changed = false;
  for (uint8_t i = 0; i < RELAY_COUNT; i++) {
    if (g_pulseExpiry[i] != 0 && (long)(now - g_pulseExpiry[i]) >= 0) {
      g_pulseExpiry[i] = 0;
      setRelay(i + 1, false);   // release
      changed = true;
    }
  }
  if (changed) {
    publishState();
  }
}

// ---------------------------------------------------------------------------
// resetW5500, pulse the W5500 hardware reset line for a clean start.
// ---------------------------------------------------------------------------
void resetW5500() {
  pinMode(PIN_W5500_RST, OUTPUT);
  digitalWrite(PIN_W5500_RST, LOW);   // assert reset
  delay(50);
  digitalWrite(PIN_W5500_RST, HIGH);  // release reset
  delay(200);                         // allow PHY to come up
}

// ---------------------------------------------------------------------------
// Build all topic strings once from MODULE_ID.
// ---------------------------------------------------------------------------
void buildTopics() {
  snprintf(g_topicBase,     sizeof(g_topicBase),     "myforce/module/%s", MODULE_ID);
  snprintf(g_topicStatus,   sizeof(g_topicStatus),   "%s/status",   g_topicBase);
  snprintf(g_topicRegistry, sizeof(g_topicRegistry), "%s/registry", g_topicBase);
  snprintf(g_topicConfig,   sizeof(g_topicConfig),   "%s/config",   g_topicBase);
  snprintf(g_topicState,    sizeof(g_topicState),    "%s/state",    g_topicBase);
  snprintf(g_topicCmdSub,   sizeof(g_topicCmdSub),   "%s/cmd/#",    g_topicBase);
}

// ---------------------------------------------------------------------------
// mqttReconnect, single non-blocking connect attempt with LWT + birth.
// ---------------------------------------------------------------------------
bool mqttReconnect() {
  Serial.print("Connecting to MQTT broker ");
  Serial.print(g_brokerAddress);
  Serial.print(":");
  Serial.print(MQTT_BROKER_PORT);
  Serial.print(" ... ");

  // Retained Last Will on the status topic, so the broker marks us offline if
  // the process drops unexpectedly (§5.2 presence / §5.8.4 LWT).
  static const char* LWT_PAYLOAD =
    "{\"v\":1,\"ts\":\"\",\"id\":\"gpio.relay1\",\"online\":false,"
    "\"health\":\"unavailable\",\"reason\":\"offline\"}";

  bool connected = g_mqttClient.connect(
      MODULE_ID,                 // client id
      nullptr, nullptr,          // no broker username/password
      g_topicStatus,             // will topic
      1,                         // will QoS 1
      true,                      // will retained
      LWT_PAYLOAD);              // will payload

  if (connected) {
    Serial.println("connected.");
    publishStatus(true);                  // birth (§5.2)
    publishRegistry();                    // self-describe (§4.5)
    publishState();                       // current relay/input snapshot
    g_mqttClient.subscribe(g_topicCmdSub, 1);  // all commands, QoS 1
    return true;
  }

  Serial.print("failed, state=");
  Serial.println(g_mqttClient.state());
  return false;
}

// ===========================================================================
// setup
// ===========================================================================
void setup() {
  Serial.begin(115200);
  delay(100);
  Serial.println();
  Serial.println("MyForce GPIO Relay Controller starting.");

  // Bring the relay backend up first so every channel is de-energised before
  // anything else can command it (§3.2). The XL9535 board needs its I2C bus and
  // port-direction set up; the direct-GPIO backend just needs its pins as OUTPUT.
  if (RELAY_BACKEND == RELAY_BACKEND_XL9535) {
    xlBegin();
  } else {
    for (uint8_t i = 0; i < RELAY_COUNT; i++) {
      pinMode(g_relayPins[i], OUTPUT);
    }
  }
  // Initialise relay outputs to de-energised before anything else.
  for (uint8_t i = 0; i < RELAY_COUNT; i++) {
    g_relayState[i]  = false;
    g_pulseExpiry[i] = 0;
    setRelay(i + 1, false);
  }
  // Prove relay function before bringing the network up (§3.2 hardware bring-up).
  bootRelaySelfTest();
  // Initialise inputs (pull-ups where the pin supports them; 34/35/36/39 do not).
  for (uint8_t i = 0; i < INPUT_COUNT; i++) {
    pinMode(g_inputPins[i], INPUT_PULLUP);
    g_inputState[i] = (digitalRead(g_inputPins[i]) == LOW);
  }

  buildTopics();

  // Bring the W5500 up and bind the VSPI bus to our pins.
  resetW5500();
  SPI.begin(PIN_W5500_SCK, PIN_W5500_MISO, PIN_W5500_MOSI, PIN_W5500_CS);
  Ethernet.init(PIN_W5500_CS);

  // DHCP. Ethernet.begin(mac) returns 1 on success.
  Serial.println("Requesting IP address via DHCP...");
  if (Ethernet.begin(g_macAddress) == 0) {
    Serial.println("DHCP failed.");
    if (Ethernet.hardwareStatus() == EthernetNoHardware) {
      Serial.println("ERROR: W5500 not detected. Check wiring/CS pin.");
    } else if (Ethernet.linkStatus() == LinkOFF) {
      Serial.println("ERROR: Ethernet cable not connected.");
    }
    while (true) { delay(1000); }   // halt: no network, nothing to do
  }
  Serial.print("DHCP success. IP address: ");
  Serial.println(Ethernet.localIP());

  // MQTT setup. Bump the buffer so retained registry/state payloads fit
  // (PubSubClient defaults to 256 bytes, too small for our JSON).
  g_mqttClient.setBufferSize(1024);
  g_mqttClient.setServer(g_brokerAddress, MQTT_BROKER_PORT);
  g_mqttClient.setCallback(mqttCallback);
  // Detect idle broker/LAN drops via keepalive, and keep reconnect attempts from
  // blocking the loop on a dead broker (§5.2 presence / idle reconnect).
  g_mqttClient.setKeepAlive(MQTT_KEEPALIVE_SECONDS);
  g_mqttClient.setSocketTimeout(MQTT_SOCKET_TIMEOUT_SECONDS);
}

// ===========================================================================
// loop
// ===========================================================================
void loop() {
  Ethernet.maintain();   // renew DHCP lease (non-blocking)

  if (!g_mqttClient.connected()) {
    unsigned long now = millis();
    if (now - g_lastReconnectAttempt >= MQTT_RECONNECT_INTERVAL) {
      g_lastReconnectAttempt = now;
      mqttReconnect();
    }
    return;   // nothing else to do until the bus is back
  }

  g_mqttClient.loop();   // service inbound commands / keepalive
  servicePulses();       // auto-release timed pulses

  // Poll inputs at a fixed cadence and emit state on change.
  unsigned long now = millis();
  if (now - g_lastInputPoll >= INPUT_POLL_INTERVAL) {
    g_lastInputPoll = now;
    pollInputs();
  }

  // Periodic retained state refresh so late subscribers stay correct.
  if (now - g_lastStatePublish >= STATE_PUBLISH_INTERVAL) {
    publishState();
  }
}
