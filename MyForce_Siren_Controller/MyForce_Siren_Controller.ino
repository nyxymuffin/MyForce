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

// MyForce Siren Interface Controller, ESP32 DevKit V1 + W5500 Lite firmware.
// Drives the siren/lightbar via an XL9535-K16V5 16-channel I2C relay board.
// External MQTT control-plane client (§3.2, §3.3, §5.2). All relay outputs are on
// the I2C board, the ESP32 drives no relay pins directly.
//
// Libraries (Arduino Library Manager): Ethernet (W5500), PubSubClient, ArduinoJson
// v7+, Wire (I2C for the XL9535 board).

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
// Network configuration. W5500 Lite has no on-board MAC, so supply a locally
// administered one. The low byte differs from the GPIO controller so the two
// ESP32s never collide on the LAN.
// ---------------------------------------------------------------------------
static byte g_macAddress[] = { 0xDE, 0xAD, 0xBE, 0xEF, 0xFE, 0x02 };

// ---------------------------------------------------------------------------
// MQTT broker configuration (project default: unencrypted 1883).
// ---------------------------------------------------------------------------
static IPAddress      g_brokerAddress(10, 43, 2, 220);   // MQTT broker IPv4
static const uint16_t MQTT_BROKER_PORT = 1883;           // Unencrypted (project default)

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

// The mutually-exclusive siren-code functions. Selecting one code clears the
// others so the single siren amplifier is never driven by two code lines at once.
static const char* g_codeFunctions[] = { "code1", "code2", "code3" };
static const uint8_t CODE_COUNT = sizeof(g_codeFunctions) / sizeof(g_codeFunctions[0]);

// The two directional relays whose combination encodes the directional position:
// left only, right only, or both (= centre-out). Resolved once at boot.
static const char* DIRECTIONAL_LEFT_FUNCTION = "directional_left";
static const char* DIRECTIONAL_RIGHT_FUNCTION = "directional_right";

// ---------------------------------------------------------------------------
// Digital inputs (read with internal pull-ups where the pin supports them).
// A physical horn-ring button is wired here so it can drive the air horn with
// zero MQTT round-trip latency (§3.2 latency-tolerant note still wants the horn
// to feel instant under the operator's hand).
// ---------------------------------------------------------------------------
// Only pins with an internal pull-up are used so an unwired input reads a stable
// inactive level (no spurious state spam). GPIO32 supports a pull-up.
static const uint8_t g_inputPins[] = { 32 };               // horn-ring button to ground
static const char* g_inputFunctions[] = { "horn_ring" };
static const uint8_t INPUT_COUNT = sizeof(g_inputPins) / sizeof(g_inputPins[0]);

// Local horn-ring passthrough: while the named input below is active, drive the
// air_horn relay directly on the device (no MQTT hop). Set to false to make the
// horn ring a report-only input handled entirely in the UI/AP.
static const bool   HORN_RING_LOCAL_DRIVE = true;
static const uint8_t HORN_RING_INPUT_INDEX = 0;       // index into g_inputPins/g_inputFunctions
static const char* HORN_RING_RELAY_FUNCTION = "airhorn";

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

// Shadow of the 16 output bits (bit n = relay channel n+1, 1 = energised). We
// keep the full word locally and push both ports so a single-channel change
// never disturbs the others.
uint16_t g_xlOutputShadow = 0x0000;

// ---------------------------------------------------------------------------
// Power-on relay self-test. On boot we briefly energise one relay on the XL9535
// board to prove the I2C path and the board are alive (an audible click / lit
// LED). Channel 16 (bit 15) is used because it sits OUTSIDE the mapped function
// channels (the siren uses 1-11), so the test never drives a real siren output.
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
	INPUT_POLL_INTERVAL = 20      // debounced input sampling cadence (snappier for horn ring)
};

// MQTT liveness tuning. The keepalive makes the client PING the broker on an idle
// link, so a silently dropped broker/LAN is detected within ~1-2 keepalive windows
// even when no siren commands are flowing; connected() then goes false and loop()
// reconnects. The socket timeout bounds how long a connect()/read may block so a
// dead broker never stalls the control loop (§5.2 presence).
static const uint16_t MQTT_KEEPALIVE_SECONDS = 15;  // PINGREQ cadence on idle link
static const uint8_t  MQTT_SOCKET_TIMEOUT_SECONDS = 4;  // bound blocking socket ops

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
	uint8_t leftCh = channelForFunction(DIRECTIONAL_LEFT_FUNCTION);
	uint8_t rightCh = channelForFunction(DIRECTIONAL_RIGHT_FUNCTION);
	bool left = (leftCh != 0) && g_relayState[leftCh - 1];
	bool right = (rightCh != 0) && g_relayState[rightCh - 1];
	if (left && right) { return "center"; }
	if (left) { return "left"; }
	if (right) { return "right"; }
	return "off";
}

// Apply a directional position (§ wiring spec). "left"/"right" drive a single
// relay; "center"/"centre" drive BOTH directional relays together; "off"/"none"
// release both. Returns false for an unknown direction.
bool applyDirectional(const char* dir) {
	bool left, right;
	if (strcmp(dir, "left") == 0) { left = true;  right = false; }
	else if (strcmp(dir, "right") == 0) { left = false; right = true; }
	else if (strcmp(dir, "center") == 0 || strcmp(dir, "centre") == 0) { left = true;  right = true; }
	else if (strcmp(dir, "off") == 0 || strcmp(dir, "none") == 0) { left = false; right = false; }
	else { return false; }

	uint8_t leftCh = channelForFunction(DIRECTIONAL_LEFT_FUNCTION);
	uint8_t rightCh = channelForFunction(DIRECTIONAL_RIGHT_FUNCTION);
	if (leftCh != 0) { g_pulseExpiry[leftCh - 1] = 0; setRelay(leftCh, left); }
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
	doc["v"] = PAYLOAD_VERSION;
	doc["ts"] = "";                // populate via NTP if wall-clock time is required
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

// Publish the retained registry: self-describing capabilities the UI renders its
// siren admin/control surface from (§4.5, §5.8.4).
void publishRegistry() {
	JsonDocument doc;
	doc["v"] = PAYLOAD_VERSION;
	doc["ts"] = "";
	doc["id"] = MODULE_ID;
	doc["kind"] = MODULE_KIND;       // external
	doc["category"] = MODULE_CAT;        // siren
	doc["removable"] = true;

	JsonObject caps = doc["capabilities"].to<JsonObject>();
	caps["relay_channels"] = RELAY_COUNT;
	caps["input_channels"] = INPUT_COUNT;

	// Which physical relay backend is driving the channels (§3.2).
	caps["relay_backend"] = "xl9535_k16v5";

	// Named relay functions the UI can assign/trigger (§4.5).
	JsonArray fns = caps["relay_functions"].to<JsonArray>();
	for (uint8_t i = 0; i < RELAY_COUNT; i++) {
		JsonObject f = fns.add<JsonObject>();
		f["name"] = g_relayFunctions[i];
		f["channel"] = i + 1;
		f["code"] = isCodeChannel(i + 1);   // flags the interlocked siren-code group
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
	doc["v"] = PAYLOAD_VERSION;
	doc["ts"] = "";
	doc["id"] = MODULE_ID;
	doc["code_mode"] = activeCodeName();         // convenience summary of the code group
	doc["directional"] = activeDirectionalName();  // off | left | center | right

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
		in["function"] = g_inputFunctions[i];
		in["state"] = g_inputState[i] ? "active" : "inactive";
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

	// LOG: show the dispatched action + key fields so we can see commands being acted on.
	if (LOG_VERBOSE) {
		Serial.print("[CMD] action=");
		Serial.print(action);
		Serial.print(" direction=");
		Serial.print(doc["direction"] | "(none)");
		Serial.print(" code=");
		Serial.print(doc["code"] | "(none)");
		Serial.print(" function=");
		Serial.print(doc["function"] | "(none)");
		Serial.print(" state=");
		Serial.println(doc["state"] | "(none)");
	}

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
		if (strcmp(state, "on") == 0) { energised = true; }
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
	Serial.println("MyForce Siren Interface Controller starting.");

	// Bring the XL9535 relay board up first (I2C bus + port direction) so every
	// siren/lightbar channel is de-energised before anything can command it (§3.2).
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