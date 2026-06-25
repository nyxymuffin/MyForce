// MyForce Siren Interface Controller, ESP32 DevKit V1 + W5500 Lite firmware
// Hardware: ESP32 DevKit V1, Wiznet W5500 Lite Ethernet module (SPI),
//           XL9535-K16V5 16-channel I2C relay board driving the siren/lightbar.
//           Hardware platform is identical to the GPIO Relay Controller (§3.2, §3.3).
// Role: external MQTT control-plane client. Receives siren/lightbar commands over
//       MQTT and drives the physical siren via relays (PROJECT_FRAMEWORK.md §3.2,
//       §3.3, §5.2, §11.2 "Siren Interface Controller").
//
// NOTE: This controller drives NON-radio, latency-tolerant emergency hardware
//       (siren tones, air horn, lightbar). It never handles radio PTT; that keying
//       is AP-owned over an AP-local relay board (§3.4, §3.6.3).
//
// Libraries required (Arduino Library Manager):
//   - "Ethernet"     (Wiznet W5100/W5200/W5500 driver)
//   - "PubSubClient" by Nick O'Leary  (MQTT client)
//   - "ArduinoJson"  by Benoit Blanchon (v7+, JSON payloads, §5.8)
//   - "Wire"         (Arduino core I2C, used by the XL9535 relay backend)

#include <SPI.h>
#include <Ethernet.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <Wire.h>   // I2C master for the XL9535-K16V5 relay backend

// ---------------------------------------------------------------------------
// SPI pin mapping (ESP32 DevKit V1 -> W5500 Lite, VSPI bus). Identical to the
// GPIO controller because the hardware platform is the same (§3.2).
// ---------------------------------------------------------------------------
#define PIN_W5500_SCK   18   // VSPI SCK  -> W5500 SCLK
#define PIN_W5500_MISO  19   // VSPI MISO -> W5500 MISO
#define PIN_W5500_MOSI  23   // VSPI MOSI -> W5500 MOSI
#define PIN_W5500_CS    33   // Chip select -> W5500 SCS
#define PIN_W5500_RST   26   // Hardware reset -> W5500 RSTn (optional)

// ---------------------------------------------------------------------------
// Network configuration
// W5500 Lite has no on-board MAC, so supply a locally administered one. The low
// byte differs from the GPIO controller so the two ESP32s never collide on the LAN.
// ---------------------------------------------------------------------------
static byte g_macAddress[] = { 0xDE, 0xAD, 0xBE, 0xEF, 0xFE, 0x02 };

// ---------------------------------------------------------------------------
// MQTT broker configuration (project default: unencrypted 1883).
// ---------------------------------------------------------------------------
static IPAddress      g_brokerAddress(10, 43, 2, 2);   // MQTT broker IPv4
static const uint16_t MQTT_BROKER_PORT = 1883;         // Unencrypted (project default)

// ---------------------------------------------------------------------------
// Module identity (§5.2). All topics are addressed by this instance <id>.
// ---------------------------------------------------------------------------
static const char* MODULE_ID   = "siren1";        // unique per controller on the bus
static const char* MODULE_KIND = "external";      // radio_module | radio_resource | external
static const char* MODULE_CAT  = "siren";         // radio | media | siren | scada | gpio
static const int   PAYLOAD_VERSION = 1;           // envelope "v" (§5.8.1)

// Admin credential for config-changing commands (§4.6). Dummy gate, not security.
static const char* ADMIN_PIN = "2135";

// ---------------------------------------------------------------------------
// Relay output backend (§3.2). Same selectable backend as the GPIO controller.
// The siren's relays live on the XL9535-K16V5 16-channel I2C board by default;
// the direct-GPIO path is kept for bench testing without the relay board.
// ---------------------------------------------------------------------------
enum RelayLogic {
  RELAY_ACTIVE_HIGH = 0,   // GPIO HIGH energises the relay coil
  RELAY_ACTIVE_LOW  = 1    // GPIO LOW  energises the relay coil (common on opto boards)
};
static const RelayLogic RELAY_LOGIC = RELAY_ACTIVE_LOW;   // only used by the GPIO backend

enum RelayBackend {
  RELAY_BACKEND_GPIO   = 0,   // direct ESP32 GPIO pins via digitalWrite()
  RELAY_BACKEND_XL9535 = 1    // XL9535-K16V5 16-channel I2C relay board
};
static const RelayBackend RELAY_BACKEND = RELAY_BACKEND_XL9535;  // siren ships on the relay board

// ---------------------------------------------------------------------------
// Siren relay channels & functions (§4.5). Channel index is the array position
// + 1 (1-based on the bus), matching the physical wiring spec:
//
//   Relay  1 -> DirectionalLeft  (also half of CenterOut)
//   Relay  2 -> DirectionalRight (also half of CenterOut)
//   Relay  3 -> Code1            }
//   Relay  4 -> Code2            } mutually-exclusive siren-code group
//   Relay  5 -> Code3            }
//   Relay  6 -> Airhorn          (momentary)
//   Relay  7 -> AlleyLeft        (toggle)
//   Relay  8 -> AlleyRight       (toggle)
//   Relay  9 -> Takedown         (toggle)
//   Relay 10 -> PA               (toggle)
//   Relay 11 -> Cruise           (toggle)
//
// The DIRECTIONAL "centre" position is NOT its own relay: it is the Left and
// Right relays energised together (see applyDirectional()). The Code1/2/3 lines
// share one siren amplifier, so energising one code de-energises the others
// (interlock, see applyCodeMode()). The array length defines RELAY_COUNT and
// bounds the XL9535 channels used (channel N drives expander bit N-1; board
// exposes 16).
// ---------------------------------------------------------------------------
static const char* g_relayFunctions[] = {
  "directional_left",    // 1  - DirectionalLeft  / CenterOut half
  "directional_right",   // 2  - DirectionalRight / CenterOut half
  "code1",               // 3  - Code1  (interlocked)
  "code2",               // 4  - Code2  (interlocked)
  "code3",               // 5  - Code3  (interlocked)
  "airhorn",             // 6  - Airhorn (momentary)
  "alley_left",          // 7  - AlleyLeft
  "alley_right",         // 8  - AlleyRight
  "takedown",            // 9  - Takedown
  "pa",                  // 10 - PA
  "cruise"               // 11 - Cruise
};
static const uint8_t RELAY_COUNT = sizeof(g_relayFunctions) / sizeof(g_relayFunctions[0]);

// Relay channel pins, used ONLY by the GPIO bench-test backend (ignored for the
// XL9535 board). This list may be SHORTER than RELAY_COUNT: the ESP32 + W5500
// cannot spare 11 safe GPIOs, so the GPIO backend only drives the channels for
// which a pin exists here; the XL9535 board is required for the full mapping.
// Avoid strapping/boot pins (0, 2, 12, 15) and the W5500 SPI pins.
static const uint8_t g_relayPins[]   = { 13, 14, 27, 16, 17, 4, 5, 25 };
static const uint8_t RELAY_PIN_COUNT = sizeof(g_relayPins) / sizeof(g_relayPins[0]);

// The mutually-exclusive siren-code functions. Selecting one code clears the
// others so the single siren amplifier is never driven by two code lines at once.
static const char* g_codeFunctions[] = { "code1", "code2", "code3" };
static const uint8_t CODE_COUNT = sizeof(g_codeFunctions) / sizeof(g_codeFunctions[0]);

// The two directional relays whose combination encodes the directional position:
// left only, right only, or both (= centre-out). Resolved once at boot.
static const char* DIRECTIONAL_LEFT_FUNCTION  = "directional_left";
static const char* DIRECTIONAL_RIGHT_FUNCTION = "directional_right";

// ---------------------------------------------------------------------------
// Digital inputs (read with internal pull-ups where the pin supports them).
// A physical horn-ring button is wired here so it can drive the air horn with
// zero MQTT round-trip latency (§3.2 latency-tolerant note still wants the horn
// to feel instant under the operator's hand).
// ---------------------------------------------------------------------------
static const uint8_t g_inputPins[]       = { 32, 39 };           // 39 is input-only, no pull-up
static const char*   g_inputFunctions[]  = { "horn_ring", "aux_in" };
static const uint8_t INPUT_COUNT = sizeof(g_inputPins) / sizeof(g_inputPins[0]);

// Local horn-ring passthrough: while the named input below is active, drive the
// air_horn relay directly on the device (no MQTT hop). Set to false to make the
// horn ring a report-only input handled entirely in the UI/AP.
static const bool   HORN_RING_LOCAL_DRIVE = true;
static const uint8_t HORN_RING_INPUT_INDEX = 0;       // index into g_inputPins/g_inputFunctions
static const char*   HORN_RING_RELAY_FUNCTION = "airhorn";

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

// Shadow of the 16 output bits (bit n = relay channel n+1, 1 = energised). We
// keep the full word locally and push both ports so a single-channel change
// never disturbs the others.
uint16_t g_xlOutputShadow = 0x0000;

// Last-published relay/input state, so we only emit on change.
bool g_relayState[RELAY_COUNT];
bool g_inputState[INPUT_COUNT];

// ---------------------------------------------------------------------------
// Timing constants (ms) instead of bare magic numbers.
// ---------------------------------------------------------------------------
enum TimingMs {
  MQTT_RECONNECT_INTERVAL = 5000,   // wait between failed MQTT connect attempts
  STATE_PUBLISH_INTERVAL  = 10000,  // periodic full state refresh (retained heartbeat)
  INPUT_POLL_INTERVAL     = 20      // debounced input sampling cadence (snappier for horn ring)
};

// Pulse bookkeeping: a relay can be commanded to auto-release after N ms (e.g. a
// timed air-horn blast). millis() deadline, 0 = no active pulse.
unsigned long g_pulseExpiry[RELAY_COUNT];

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
// ports to outputs. Outputs are written BEFORE configuring direction so no relay
// glitches on (the latch holds 0 = de-energised; the board is active high).
void xlBegin() {
  Wire.begin(PIN_XL9535_SDA, PIN_XL9535_SCL);
  g_xlOutputShadow = 0x0000;        // all off
  xlPushOutputs();
  xlWriteReg(XL9535_CONFIG_PORT0, 0x00);  // port 0 pins = outputs
  xlWriteReg(XL9535_CONFIG_PORT1, 0x00);  // port 1 pins = outputs
}

// Drive one relay channel (1-based) to energised/de-energised. Routes to the
// selected backend: direct GPIO or the XL9535 I2C board. Updates the cache.
void setRelay(uint8_t channel, bool energised) {
  if (channel < 1 || channel > RELAY_COUNT) {
    return;  // out of range, ignored (the command path reports rejected)
  }
  uint8_t idx = channel - 1;

  if (RELAY_BACKEND == RELAY_BACKEND_XL9535) {
    // The XL9535-K16V5 board is active high (bit = 1 energises the relay), so we
    // map energised directly to the shadow bit and push the change.
    uint16_t mask = (uint16_t)1 << idx;
    if (energised) { g_xlOutputShadow |= mask; }
    else           { g_xlOutputShadow &= ~mask; }
    xlPushOutputs();
  } else if (idx < RELAY_PIN_COUNT) {
    // Direct GPIO drive, honouring the opto board's active-high/low wiring. Only
    // channels that have a pin in g_relayPins are physically driven in this mode.
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

// True if the given 1-based channel is one of the mutually-exclusive code lines.
bool isCodeChannel(uint8_t channel) {
  if (channel < 1 || channel > RELAY_COUNT) {
    return false;
  }
  const char* fn = g_relayFunctions[channel - 1];
  for (uint8_t i = 0; i < CODE_COUNT; i++) {
    if (strcmp(fn, g_codeFunctions[i]) == 0) {
      return true;
    }
  }
  return false;
}

// De-energise every code channel except the one given (0 = clear all codes).
// Implements the single-amplifier interlock so two codes never sound together.
void clearOtherCodes(uint8_t keepChannel) {
  for (uint8_t i = 0; i < CODE_COUNT; i++) {
    uint8_t ch = channelForFunction(g_codeFunctions[i]);
    if (ch != 0 && ch != keepChannel) {
      g_pulseExpiry[ch - 1] = 0;    // selecting a code cancels any pulse on the others
      setRelay(ch, false);
    }
  }
}

// Return the name of the currently active code, or "off" if none is energised.
const char* activeCodeName() {
  for (uint8_t i = 0; i < CODE_COUNT; i++) {
    uint8_t ch = channelForFunction(g_codeFunctions[i]);
    if (ch != 0 && g_relayState[ch - 1]) {
      return g_codeFunctions[i];
    }
  }
  return "off";
}

// Apply a siren code (§11.5). "off"/"none" silences all codes; "code1".."code3"
// selects that code and interlocks the others off. Returns false if the value
// is not a known code (or "off").
bool applyCodeMode(const char* code) {
  if (strcmp(code, "off") == 0 || strcmp(code, "none") == 0) {
    clearOtherCodes(0);   // clear every code
    return true;
  }
  uint8_t ch = channelForFunction(code);
  if (ch == 0 || !isCodeChannel(ch)) {
    return false;         // not a code function
  }
  clearOtherCodes(ch);    // interlock the others off first
  g_pulseExpiry[ch - 1] = 0;
  setRelay(ch, true);     // then energise the requested code
  return true;
}

// Report the directional position from the two directional relays: "left" =
// left only, "right" = right only, "center" = BOTH energised together, "off" =
// neither (per the wiring spec: centre-out drives Left and Right at once).
const char* activeDirectionalName() {
  uint8_t leftCh  = channelForFunction(DIRECTIONAL_LEFT_FUNCTION);
  uint8_t rightCh = channelForFunction(DIRECTIONAL_RIGHT_FUNCTION);
  bool left  = (leftCh  != 0) && g_relayState[leftCh  - 1];
  bool right = (rightCh != 0) && g_relayState[rightCh - 1];
  if (left && right) { return "center"; }
  if (left)          { return "left";   }
  if (right)         { return "right";  }
  return "off";
}

// Apply a directional position (§ wiring spec). "left"/"right" drive a single
// relay; "center"/"centre" drive BOTH directional relays together; "off"/"none"
// release both. Returns false for an unknown direction.
bool applyDirectional(const char* dir) {
  bool left, right;
  if (strcmp(dir, "left") == 0)                                      { left = true;  right = false; }
  else if (strcmp(dir, "right") == 0)                                { left = false; right = true;  }
  else if (strcmp(dir, "center") == 0 || strcmp(dir, "centre") == 0) { left = true;  right = true;  }
  else if (strcmp(dir, "off") == 0 || strcmp(dir, "none") == 0)      { left = false; right = false; }
  else { return false; }

  uint8_t leftCh  = channelForFunction(DIRECTIONAL_LEFT_FUNCTION);
  uint8_t rightCh = channelForFunction(DIRECTIONAL_RIGHT_FUNCTION);
  if (leftCh  != 0) { g_pulseExpiry[leftCh  - 1] = 0; setRelay(leftCh,  left);  }
  if (rightCh != 0) { g_pulseExpiry[rightCh - 1] = 0; setRelay(rightCh, right); }
  return true;
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
  doc["msg_id"] = msgId;             // echo the command's msg_id
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

// Publish the retained registry: self-describing capabilities the UI renders its
// siren admin/control surface from (§4.5, §5.8.4).
void publishRegistry() {
  JsonDocument doc;
  doc["v"]         = PAYLOAD_VERSION;
  doc["ts"]        = "";
  doc["id"]        = MODULE_ID;
  doc["kind"]      = MODULE_KIND;       // external
  doc["category"]  = MODULE_CAT;        // siren
  doc["removable"] = true;

  JsonObject caps = doc["capabilities"].to<JsonObject>();
  caps["relay_channels"] = RELAY_COUNT;
  caps["input_channels"] = INPUT_COUNT;
  // Which physical relay backend is driving the channels (§3.2).
  caps["relay_backend"] = (RELAY_BACKEND == RELAY_BACKEND_XL9535) ? "xl9535_k16v5" : "esp32_gpio";

  // Named relay functions the UI can assign/trigger (§4.5).
  JsonArray fns = caps["relay_functions"].to<JsonArray>();
  for (uint8_t i = 0; i < RELAY_COUNT; i++) {
    JsonObject f = fns.add<JsonObject>();
    f["name"]    = g_relayFunctions[i];
    f["channel"] = i + 1;
    f["code"]    = isCodeChannel(i + 1);   // flags the interlocked siren-code group
  }
  // The mutually-exclusive siren codes the UI offers as a single selector (§11.5).
  JsonArray codes = caps["code_modes"].to<JsonArray>();
  codes.add("off");
  for (uint8_t i = 0; i < CODE_COUNT; i++) {
    codes.add(g_codeFunctions[i]);
  }
  // The directional positions; "center" energises both directional relays at once.
  JsonArray dirs = caps["directional_modes"].to<JsonArray>();
  dirs.add("off");
  dirs.add("left");
  dirs.add("center");
  dirs.add("right");
  // Supported operating actions on cmd/<action>.
  JsonArray acts = caps["actions"].to<JsonArray>();
  acts.add("set");          // { channel|function, state: on|off }
  acts.add("pulse");        // { channel|function, ms } (e.g. a timed airhorn blast)
  acts.add("code");         // { code: off|code1|code2|code3 } (interlocked code selector)
  acts.add("directional");  // { direction: off|left|center|right } (center = both relays)

  char buf[768];
  size_t n = serializeJson(doc, buf, sizeof(buf));
  g_mqttClient.publish(g_topicRegistry, (const uint8_t*)buf, n, true);  // retained
}

// Publish the retained runtime state: every relay and input channel plus the
// current interlocked code + directional selection (§5.2 state).
void publishState() {
  JsonDocument doc;
  doc["v"]  = PAYLOAD_VERSION;
  doc["ts"] = "";
  doc["id"] = MODULE_ID;
  doc["code_mode"]   = activeCodeName();         // convenience summary of the code group
  doc["directional"] = activeDirectionalName();  // off | left | center | right

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
    in["channel"]  = i + 1;
    in["function"] = g_inputFunctions[i];
    in["state"]    = g_inputState[i] ? "active" : "inactive";
  }
  char buf[640];
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

  // --- Operating command: set (no admin auth) ---------------------------
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
    // Energising a code line interlocks the other codes off (single amplifier, §11.5).
    if (energised && isCodeChannel(ch)) {
      clearOtherCodes(ch);
    }
    setRelay(ch, energised);
    publishAck(fullTopic, msgId, "ok", nullptr);
    publishState();
    return;
  }

  // --- Operating command: pulse (no admin auth) -------------------------
  if (strcmp(action, "pulse") == 0) {
    // { channel|function, ms: <duration> }, energise, then auto-release. Handy
    // for a fixed-length air-horn blast.
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
    if (isCodeChannel(ch)) {
      clearOtherCodes(ch);   // a pulsed code still interlocks the others off
    }
    setRelay(ch, true);
    g_pulseExpiry[ch - 1] = millis() + (unsigned long)ms;  // serviced in loop()
    publishAck(fullTopic, msgId, "ok", nullptr);
    publishState();
    return;
  }

  // --- Operating command: code (interlocked siren-code selector, §11.5) -
  if (strcmp(action, "code") == 0) {
    // { code: "off"|"code1"|"code2"|"code3" } , one call sets the whole code group.
    const char* code = doc["code"] | "";
    if (code[0] == '\0') {
      publishAck(fullTopic, msgId, "rejected", "code_required");
      return;
    }
    if (!applyCodeMode(code)) {
      publishAck(fullTopic, msgId, "rejected", "unknown_code");
      return;
    }
    publishAck(fullTopic, msgId, "ok", nullptr);
    publishState();
    return;
  }

  // --- Operating command: directional (left/center/right, § wiring spec) -
  if (strcmp(action, "directional") == 0) {
    // { direction: "off"|"left"|"center"|"right" }. "center" energises BOTH the
    // left and right directional relays at the same time.
    const char* dir = doc["direction"] | "";
    if (dir[0] == '\0') {
      publishAck(fullTopic, msgId, "rejected", "direction_required");
      return;
    }
    if (!applyDirectional(dir)) {
      publishAck(fullTopic, msgId, "rejected", "unknown_direction");
      return;
    }
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
    // acknowledge and re-publish the retained registry snapshot as the new truth.
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
// Poll inputs; drive the local horn-ring passthrough and publish a fresh state
// snapshot on any change (debounced).
// ---------------------------------------------------------------------------
void pollInputs() {
  bool changed = false;
  for (uint8_t i = 0; i < INPUT_COUNT; i++) {
    // Inputs are wired active-low to ground with pull-ups (where available), so a
    // LOW reading means the contact is closed / active.
    bool active = (digitalRead(g_inputPins[i]) == LOW);
    if (active != g_inputState[i]) {
      g_inputState[i] = active;
      changed = true;

      // Zero-latency horn ring: drive the air-horn relay locally on edge so the
      // operator's horn-ring button does not wait on an MQTT round-trip (§3.2).
      if (HORN_RING_LOCAL_DRIVE && i == HORN_RING_INPUT_INDEX) {
        uint8_t hornCh = channelForFunction(HORN_RING_RELAY_FUNCTION);
        if (hornCh != 0) {
          g_pulseExpiry[hornCh - 1] = 0;   // manual hold, not a timed pulse
          setRelay(hornCh, active);        // pressed = horn on, released = horn off
        }
      }
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

  // Retained Last Will on the status topic, so the broker marks us offline if the
  // process drops unexpectedly (§5.2 presence / §5.8.4 LWT).
  static const char* LWT_PAYLOAD =
    "{\"v\":1,\"ts\":\"\",\"id\":\"siren1\",\"online\":false,"
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
  Serial.println("MyForce Siren Interface Controller starting.");

  // Bring the relay backend up first so every siren/lightbar channel is
  // de-energised before anything can command it (§3.2). The XL9535 board needs
  // its I2C bus and port direction; the direct-GPIO backend just needs OUTPUT pins.
  if (RELAY_BACKEND == RELAY_BACKEND_XL9535) {
    xlBegin();
  } else {
    // GPIO bench-test backend only drives the channels that have a pin defined.
    for (uint8_t i = 0; i < RELAY_PIN_COUNT; i++) {
      pinMode(g_relayPins[i], OUTPUT);
    }
  }
  // Initialise relay outputs to de-energised before anything else.
  for (uint8_t i = 0; i < RELAY_COUNT; i++) {
    g_relayState[i]  = false;
    g_pulseExpiry[i] = 0;
    setRelay(i + 1, false);
  }
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
    // Even off the bus, keep servicing local inputs so the horn ring still works.
    now = millis();
    if (now - g_lastInputPoll >= INPUT_POLL_INTERVAL) {
      g_lastInputPoll = now;
      pollInputs();
    }
    servicePulses();
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
