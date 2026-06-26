// %%%%%%    @%%%%%@
//%%%%%%%%   %%%%%%%@
//@%%%%%%%@  %%%%%%%%%        @@      @@  @@@      @@@ @@@     @@@ @@@@@@@@@@   @@@@@@@@@
//%%%%%%%%@ @%%%%%%%%       @@@@@   @@@@ @@@@@   @@@@ @@@@   @@@@ @@@@@@@@@@@@@@@@@@@@@@@ @@@@
// @%%%%%%%%  %%%%%%%%%      @@@@@@  @@@@  @@@@  @@@@   @@@@@@@@@     @@@@    @@@@         @@@@
//  %%%%%%%%%  %%%%%%%%@     @@@@@@@ @@@@   @@@@@@@@     @@@@@@       @@@@    @@@@@@@@@@@  @@@@
//   %%%%%%%%@  %%%%%%%%%    @@@@@@@@@@@@     @@@@        @@@@@       @@@@    @@@@@@@@@@@  @@@@
//    %%%%%%%%@ @%%%%%%%%    @@@@ @@@@@@@     @@@@      @@@@@@@@      @@@@    @@@@         @@@@
//    @%%%%%%%%% @%%%%%%%%   @@@@   @@@@@     @@@@     @@@@@ @@@@@    @@@@    @@@@@@@@@@@@ @@@@@@@@@@
//     @%%%%%%%%  %%%%%%%%@  @@@@    @@@@     @@@@    @@@@     @@@@   @@@@    @@@@@@@@@@@@ @@@@@@@@@@@
//      %%%%%%%%@ @%%%%%%%%
//      @%%%%%%%%  @%%%%%%%%
//       %%%%%%%%   %%%%%%%@
//         %%%%%      %%%%
//
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini

//

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
static IPAddress      g_brokerAddress(10, 43, 2, 220);   // MQTT broker IPv4
static const uint16_t MQTT_BROKER_PORT = 1883;         // Unencrypted (project default)

// ---------------------------------------------------------------------------
// Module identity (§5.2). All topics are addressed by this instance <id>.
// ---------------------------------------------------------------------------
static const char* MODULE_ID = "gpio.relay1";   // unique per controller on the bus
static const char* MODULE_KIND = "external";      // radio_module | radio_resource | external
static const char* MODULE_CAT = "gpio";          // radio | media | siren | scada | gpio
static const int   PAYLOAD_VERSION = 1;           // envelope "v" (§5.8.1)

// Admin credential for config-changing commands (§4.6). Dummy gate, not security.
static const char* ADMIN_PIN = "2135";

// ---------------------------------------------------------------------------
// Relay outputs & digital inputs. All relay outputs are driven over the XL9535
// I2C relay board (no ESP32 pins drive relays). The number of channels and the
// named relay "functions" are what the controller self-describes so the UI can
// build its admin surface with no UI change (§4.5).
// ---------------------------------------------------------------------------

// Digital input pins. Only pins with an internal pull-up are used so an unwired
// input reads a stable inactive level (no spurious state spam); GPIO32/25 support one.
static const uint8_t g_inputPins[] = { 32, 25 };

// Human-facing relay function names (§4.5). The array length defines RELAY_COUNT
// and maps each function to an XL9535 channel (channel N -> expander bit N-1; the
// board exposes up to 16).
//
// Camera/DVR control relays. These are MOMENTARY: the UI fires a short pulse to
// each one to simulate pressing the matching button on the camera head/recorder.
//   Relay 1 -> camera_record  (UI "REC"   button)
//   Relay 2 -> camera_stop    (UI "STOP"  button)
//   Relay 3 -> cam_autozoom   (UI "AUTOZ" button, front camera auto zoom)
static const char* g_relayFunctions[] = { "camera_record", "camera_stop", "cam_autozoom" };

static const uint8_t RELAY_COUNT = sizeof(g_relayFunctions) / sizeof(g_relayFunctions[0]);
static const uint8_t INPUT_COUNT = sizeof(g_inputPins) / sizeof(g_inputPins[0]);

// ---------------------------------------------------------------------------
// XL9535-K16V5 16-channel I2C relay board (the only relay output path).
// The XL9535 is a 16-bit I2C I/O expander (PCA9555-compatible register map):
// two 8-bit ports drive the 16 relays. The board is ACTIVE HIGH, a 1 bit
// energises the relay coil. Default 7-bit I2C address is 0x20 (A0/A1/A2 open).
// ESP32 default I2C pins: SDA = GPIO21, SCL = GPIO22 (clear of the W5500 VSPI
// bus on 18/19/23/33/26).
// ---------------------------------------------------------------------------
static const uint8_t XL9535_I2C_ADDR = 0x20;   // A0/A1/A2 jumpers all open
static const uint8_t PIN_XL9535_SDA = 21;     // I2C data  -> board SDA
static const uint8_t PIN_XL9535_SCL = 22;     // I2C clock -> board SCL

// XL9535 / PCA9555 register addresses (named to avoid magic numbers).
enum Xl9535Reg {
	XL9535_INPUT_PORT0 = 0x00,   // relays 1-8  read-back
	XL9535_INPUT_PORT1 = 0x01,   // relays 9-16 read-back
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
static const uint8_t  BOOT_RELAY_TEST_CHANNEL = 16;    // 1-based; XL9535 bit 15
static const uint16_t BOOT_RELAY_TEST_MS = 2000;  // pulse duration (hold to prove function)

// Last-published relay/input state, so we only emit on change.
bool g_relayState[RELAY_COUNT];
bool g_inputState[INPUT_COUNT];

// ---------------------------------------------------------------------------
// Timing constants (ms) instead of bare magic numbers.
// ---------------------------------------------------------------------------
enum TimingMs {
	MQTT_RECONNECT_INTERVAL = 5000,   // wait between failed MQTT connect attempts
	STATE_PUBLISH_INTERVAL = 10000,  // periodic full state refresh (retained heartbeat)
	INPUT_POLL_INTERVAL = 50      // debounced input sampling cadence
};

// MQTT liveness tuning. The keepalive makes the client PING the broker on an idle
// link, so a silently dropped broker/LAN is detected within ~1-2 keepalive windows
// even when no commands are flowing; connected() then goes false and loop()
// reconnects. The socket timeout bounds how long a connect()/read may block so a
// dead broker never stalls the control loop (§5.2 presence).
static const uint16_t MQTT_KEEPALIVE_SECONDS = 15;  // PINGREQ cadence on idle link
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
unsigned long g_lastStatePublish = 0;
unsigned long g_lastInputPoll = 0;
unsigned long g_lastStatusLog = 0;     // periodic connection heartbeat to serial

// Verbose serial logging for field diagnostics: every inbound MQTT message, command
// dispatch, relay/I2C writes, and a periodic connection heartbeat. Set false to quieten.
static const bool          LOG_VERBOSE = true;
static const unsigned long STATUS_LOG_INTERVAL = 5000;   // ms between heartbeat lines

// ===========================================================================
// Helpers
// ===========================================================================

// True if the ESP32 GPIO has an internal pull-up. GPIO 34-39 are input-only pads
// with no pull resistors, so INPUT_PULLUP is invalid on them.
bool pinSupportsInternalPullup(uint8_t pin) {
	return !(pin >= 34 && pin <= 39);
}

// --- XL9535 I2C relay backend -------------------------------------------------

// Bit-bang SCL up to 9 cycles to free a slave that may be holding SDA low after an
// interrupted transfer. Pins are left as plain GPIO; the caller re-inits Wire after.
void i2cClockOutStuckSlave() {
	pinMode(PIN_XL9535_SCL, OUTPUT);
	pinMode(PIN_XL9535_SDA, INPUT_PULLUP);
	for (uint8_t i = 0; i < 9 && digitalRead(PIN_XL9535_SDA) == LOW; i++) {
		digitalWrite(PIN_XL9535_SCL, LOW);  delayMicroseconds(5);
		digitalWrite(PIN_XL9535_SCL, HIGH); delayMicroseconds(5);
	}
}

// Fully reset the I2C peripheral. Wire.end() releases the driver FIRST so we never
// re-init on top of a live driver, that is what previously wedged the controller
// into a permanent endTransmission==4. Then free any stuck slave and re-init clean.
void i2cReset() {
	Wire.end();
	i2cClockOutStuckSlave();
	Wire.begin(PIN_XL9535_SDA, PIN_XL9535_SCL);
	Wire.setClock(100000);   // 100 kHz, conservative for longer/ribbon I2C runs
}

// Scan the I2C bus and log every address that ACKs. Run at boot so the serial
// monitor shows whether the relay board is actually present at 0x20. If nothing is
// found, it is a wiring / pull-up / power / address problem, not the firmware.
void i2cScan() {
	if (!LOG_VERBOSE) {
		return;
	}
	uint8_t found = 0;
	Serial.println("[I2C SCAN] scanning bus...");
	for (uint8_t addr = 1; addr < 127; addr++) {
		Wire.beginTransmission(addr);
		if (Wire.endTransmission() == 0) {
			Serial.print("[I2C SCAN] found device at 0x");
			Serial.println(addr, HEX);
			found++;
		}
	}
	Serial.print("[I2C SCAN] complete, ");
	Serial.print(found);
	Serial.println(" device(s) responding.");
}

// Write a single 8-bit register on the XL9535 expander. Returns the Wire status
// (0 = success); logs a line on any I2C error so a dead/wedged board is visible.
uint8_t xlWriteReg(uint8_t reg, uint8_t value) {
	Wire.beginTransmission(XL9535_I2C_ADDR);
	Wire.write(reg);
	Wire.write(value);
	uint8_t status = Wire.endTransmission();
	if (status != 0 && LOG_VERBOSE) {
		Serial.print("[I2C ERR] addr=0x");
		Serial.print(XL9535_I2C_ADDR, HEX);
		Serial.print(" reg=0x");
		Serial.print(reg, HEX);
		Serial.print(" endTransmission=");
		Serial.println(status);   // 2 = addr NAK (board not responding), 4 = bus error
	}
	return status;
}

// Push the 16-bit output shadow to both XL9535 output ports. On any write error,
// do ONE clean peripheral reset and retry so a transient glitch self-heals without
// hammering the driver.
uint8_t xlPushOutputs() {
	uint8_t s0 = xlWriteReg(XL9535_OUTPUT_PORT0, (uint8_t)(g_xlOutputShadow & 0xFF));   // relays 1-8
	uint8_t s1 = xlWriteReg(XL9535_OUTPUT_PORT1, (uint8_t)(g_xlOutputShadow >> 8));     // relays 9-16
	if (s0 != 0 || s1 != 0) {
		if (LOG_VERBOSE) {
			Serial.println("[I2C] output write failed; resetting peripheral and retrying once.");
		}
		i2cReset();
		s0 = xlWriteReg(XL9535_OUTPUT_PORT0, (uint8_t)(g_xlOutputShadow & 0xFF));
		s1 = xlWriteReg(XL9535_OUTPUT_PORT1, (uint8_t)(g_xlOutputShadow >> 8));
	}
	return (s0 != 0) ? s0 : s1;   // 0 = both writes succeeded
}

// Bring the XL9535 up: start I2C clean, scan the bus for diagnostics, drive all
// relays de-energised, then set both ports to outputs. Outputs are written BEFORE
// configuring direction so no relay glitches on (latch holds 0 = de-energised).
void xlBegin() {
	Wire.begin(PIN_XL9535_SDA, PIN_XL9535_SCL);
	Wire.setClock(100000);   // 100 kHz, conservative for longer/ribbon I2C runs
	delay(5);                // let the bus settle
	i2cScan();               // log what is actually on the bus
	g_xlOutputShadow = 0x0000;        // all off
	xlPushOutputs();
	xlWriteReg(XL9535_CONFIG_PORT0, 0x00);  // port 0 pins = outputs
	xlWriteReg(XL9535_CONFIG_PORT1, 0x00);  // port 1 pins = outputs
}

// Power-on self-test: pulse the test relay on the XL9535 board to prove relay
// function, with a serial banner so the behaviour is never a surprise. Drives the
// expander bit directly (the test channel is outside the mapped function range,
// so it bypasses setRelay()/g_relayState).
void bootRelaySelfTest() {
	if (!BOOT_RELAY_TEST_ENABLED) {
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

// Drive one relay channel (1-based) to energised/de-energised on the XL9535 I2C
// board, then update the cache. The board is the only output path.
void setRelay(uint8_t channel, bool energised) {
	if (channel < 1 || channel > RELAY_COUNT) {
		return;  // out of range, ignored (the command path reports rejected)
	}
	uint8_t idx = channel - 1;

	// The XL9535-K16V5 board is active high (bit = 1 energises the relay), so we
	// map energised directly to the shadow bit and push the change.
	uint16_t mask = (uint16_t)1 << idx;
	if (energised) { g_xlOutputShadow |= mask; }
	else { g_xlOutputShadow &= ~mask; }
	uint8_t writeStatus = xlPushOutputs();

	g_relayState[idx] = energised;

	// LOG: per-relay actuation result. write=ok means the output register really
	// updated, so if the relay still does not click the problem is on the relay-driver
	// / coil-power side, not the I2C bus.
	if (LOG_VERBOSE) {
		Serial.print("[RELAY] ch=");
		Serial.print(channel);
		Serial.print(" (");
		Serial.print(g_relayFunctions[idx]);
		Serial.print(") energised=");
		Serial.print(energised ? 1 : 0);
		Serial.print(" shadow=0x");
		Serial.print(g_xlOutputShadow, HEX);
		Serial.print(" write=");
		Serial.println(writeStatus == 0 ? "ok" : "FAIL");
	}
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
	doc["v"] = PAYLOAD_VERSION;
	doc["ts"] = "";                // populate via NTP if wall-clock time is required
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
	doc["v"] = PAYLOAD_VERSION;
	doc["ts"] = "";
	doc["id"] = MODULE_ID;
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
	doc["v"] = PAYLOAD_VERSION;
	doc["ts"] = "";
	doc["id"] = MODULE_ID;
	doc["kind"] = MODULE_KIND;       // external
	doc["category"] = MODULE_CAT;        // gpio
	doc["removable"] = true;

	JsonObject caps = doc["capabilities"].to<JsonObject>();
	caps["relay_channels"] = RELAY_COUNT;
	caps["input_channels"] = INPUT_COUNT;

	// Which physical relay backend is driving the channels (§3.2), so the UI/admin
	// surface can show the hardware in use without any UI change (§4.5).
	caps["relay_backend"] = "xl9535_k16v5";

	// Named relay functions the UI can assign/trigger (§4.5).
	JsonArray fns = caps["relay_functions"].to<JsonArray>();
	for (uint8_t i = 0; i < RELAY_COUNT; i++) {
		JsonObject f = fns.add<JsonObject>();
		f["name"] = g_relayFunctions[i];
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
	doc["v"] = PAYLOAD_VERSION;
	doc["ts"] = "";
	doc["id"] = MODULE_ID;

	JsonArray relays = doc["relays"].to<JsonArray>();
	for (uint8_t i = 0; i < RELAY_COUNT; i++) {
		JsonObject r = relays.add<JsonObject>();
		r["channel"] = i + 1;
		r["function"] = g_relayFunctions[i];
		r["state"] = g_relayState[i] ? "on" : "off";
	}
	JsonArray inputs = doc["inputs"].to<JsonArray>();
	for (uint8_t i = 0; i < INPUT_COUNT; i++) {
		JsonObject in = inputs.add<JsonObject>();
		in["channel"] = i + 1;
		in["state"] = g_inputState[i] ? "active" : "inactive";
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

	// LOG: show the dispatched action + key fields so we can see commands being acted on.
	if (LOG_VERBOSE) {
		Serial.print("[CMD] action=");
		Serial.print(action);
		Serial.print(" function=");
		Serial.print(doc["function"] | "(none)");
		Serial.print(" channel=");
		Serial.print(doc["channel"] | -1);
		Serial.print(" state=");
		Serial.print(doc["state"] | "(none)");
		Serial.print(" ms=");
		Serial.println(doc["ms"] | -1);
	}

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
		if (strcmp(state, "on") == 0) { energised = true; }
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

	// LOG: print every inbound message so the serial monitor shows exactly what the
	// broker delivers. If nothing prints here on a button press, the message is not
	// reaching this ESP32 (broker/subscription/topic problem, not a relay problem).
	if (LOG_VERBOSE) {
		Serial.print("[MQTT RX] topic=");
		Serial.print(topic);
		Serial.print(" len=");
		Serial.print(length);
		Serial.print(" payload=");
		for (unsigned int i = 0; i < length && i < 220; i++) {
			Serial.write(payload[i]);
		}
		Serial.println();
	}

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
	snprintf(g_topicBase, sizeof(g_topicBase), "myforce/module/%s", MODULE_ID);
	snprintf(g_topicStatus, sizeof(g_topicStatus), "%s/status", g_topicBase);
	snprintf(g_topicRegistry, sizeof(g_topicRegistry), "%s/registry", g_topicBase);
	snprintf(g_topicConfig, sizeof(g_topicConfig), "%s/config", g_topicBase);
	snprintf(g_topicState, sizeof(g_topicState), "%s/state", g_topicBase);
	snprintf(g_topicCmdSub, sizeof(g_topicCmdSub), "%s/cmd/#", g_topicBase);
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
		bool subOk = g_mqttClient.subscribe(g_topicCmdSub, 1);  // all commands, QoS 1
		// LOG: confirm we are actually subscribed to the command wildcard. If this
		// says FAILED, no commands will ever arrive.
		Serial.print("Subscribed to ");
		Serial.print(g_topicCmdSub);
		Serial.print(" -> ");
		Serial.println(subOk ? "ok" : "FAILED");
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
	// In verbose mode, pause so the serial monitor (which reopens the port a moment
	// after the reset-on-connect) catches the boot scan and self-test output.
	if (LOG_VERBOSE) {
		delay(1500);
	}
	Serial.println();
	Serial.println("MyForce GPIO Relay Controller starting.");

	// Bring the XL9535 relay board up first (I2C bus + port direction) so every
	// channel is de-energised before anything else can command it (§3.2).
	xlBegin();

	// Initialise relay outputs to de-energised before anything else.
	for (uint8_t i = 0; i < RELAY_COUNT; i++) {
		g_relayState[i] = false;
		g_pulseExpiry[i] = 0;
		setRelay(i + 1, false);
	}

	// Prove relay function before bringing the network up (§3.2 hardware bring-up).
	bootRelaySelfTest();

	// Initialise inputs. Use a pull-up where the pin supports one (ESP32 GPIO 34-39
	// are input-only and have NO internal pull, so requesting INPUT_PULLUP on them
	// throws a gpio_pullup_en error). All configured input pins support a pull-up.
	for (uint8_t i = 0; i < INPUT_COUNT; i++) {
		pinMode(g_inputPins[i], pinSupportsInternalPullup(g_inputPins[i]) ? INPUT_PULLUP : INPUT);
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
		}
		else if (Ethernet.linkStatus() == LinkOFF) {
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

	// LOG: periodic connection heartbeat so the serial monitor always shows whether
	// the ESP32 has an IP, a live MQTT link, and the relay board responding on I2C.
	if (LOG_VERBOSE && millis() - g_lastStatusLog >= STATUS_LOG_INTERVAL) {
		g_lastStatusLog = millis();
		// Probe the relay board so the heartbeat shows whether it ACKs at 0x20 (the
		// boot I2C scan scrolls past before the serial monitor reattaches on reset).
		Wire.beginTransmission(XL9535_I2C_ADDR);
		uint8_t i2cStat = Wire.endTransmission();
		Serial.print("[STATUS] ip=");
		Serial.print(Ethernet.localIP());
		Serial.print(" mqtt=");
		Serial.print(g_mqttClient.connected() ? "connected" : "DISCONNECTED");
		Serial.print(" state=");        // PubSubClient state: 0 = connected, negatives = errors
		Serial.print(g_mqttClient.state());
		Serial.print(" i2c0x20=");      // 0 = board ACKs; 2 = nobody home; 4 = bus error
		if (i2cStat == 0) { Serial.println("ok"); }
		else { Serial.print("err"); Serial.println(i2cStat); }
	}

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