using System.Collections.Generic;

namespace MyForce.Models;

public enum SystemComponentKind
{
    AudioProcessor,
    SoundCardInterface,
    RadioResource,
    RadioModule,
    AdvancedRadioModule,
    OperatorMic,
    SpeakerSystem,
    MqttBroker,
    UiHandler,
    VehicleInterfaceProgram,
    VoiceControl,
    GpioRelayController,
    SirenInterfaceController,
    SirenController,
    ScadaController,
    ScadaSystem,
    CadWindowsPc,
}

public sealed record SystemComponent(
    string Name,
    string Plane,
    string Role,
    string Status,
    bool IsCore,
    SystemComponentKind Kind,
    string[] Transports)
{
    /// <summary>
    /// Section 6: creates the framework-defined component reference set used by the UI documentation surface.
    /// </summary>
    public static IReadOnlyList<SystemComponent> CreateFrameworkReference()
    {
        return
        [
            new("Audio Processor", "Audio + Control", "Central audio router/mixer, TX controller, bridge engine, built-in relay/VOX primitives, RM/ARM/resource host, and system config store.", "ONLINE", true, SystemComponentKind.AudioProcessor, ["USB", "MQTT"]),
            new("SoundCard interfaces", "Audio", "Per-radio USB audio interfaces between radios and the Audio Processor.", "REFERENCE", true, SystemComponentKind.SoundCardInterface, ["USB"]),
            new("Radio Resources", "Audio", "Built-in radios that use AP relay keying and VOX detect with no plugin.", "REFERENCE", true, SystemComponentKind.RadioResource, ["USB", "Relay"]),
            new("Radio Modules", "Audio", "Plugin radios that provide nonstandard control, keying, or detection while the AP owns normal audio.", "REFERENCE", false, SystemComponentKind.RadioModule, ["USB", "Serial", "IP"]),
            new("Advanced Radio Modules", "Audio", "Plugin radios that also provide their own audio exchange instead of using a soundcard.", "REFERENCE", false, SystemComponentKind.AdvancedRadioModule, ["IP", "PCM exchange"]),
            new("Operator Mic", "Audio", "Operator TX audio input owned by the AP and tapped for voice control.", "REFERENCE", true, SystemComponentKind.OperatorMic, ["USB"]),
            new("Speaker / Car Speaker System", "Audio", "Vehicle RX audio output sink.", "REFERENCE", true, SystemComponentKind.SpeakerSystem, ["USB", "Line out"]),
            new("MQTT Broker", "Control", "Retained command, registry, config, state, and status bus for all services.", "REFERENCE", true, SystemComponentKind.MqttBroker, ["MQTT"]),
            new("UI Handler", "Control", "Operator UI, dynamic admin, and CAD bridge front-end.", "REFERENCE", true, SystemComponentKind.UiHandler, ["MQTT", "FreeRDP"]),
            new("Vehicle Interface Program", "Control", "Physical controls and indicators front-end for buttons, switches, and head-unit functions.", "REFERENCE", false, SystemComponentKind.VehicleInterfaceProgram, ["MQTT", "Serial", "GPIO", "USB-HID"]),
            new("Voice Control", "Control", "On-device speech front-end that reads the AP mic tap and publishes non-transmit commands.", "REFERENCE", false, SystemComponentKind.VoiceControl, ["MQTT", "PipeWire"]),
            new("GPIO Relay Controller", "Control", "Owns relay outputs for non-radio auxiliary hardware only.", "REFERENCE", true, SystemComponentKind.GpioRelayController, ["MQTT", "Serial"]),
            new("Siren Interface Controller", "Control", "Bridges MQTT siren commands to the physical siren controller.", "REFERENCE", true, SystemComponentKind.SirenInterfaceController, ["MQTT", "RS232"]),
            new("Siren Controller", "External", "Physical siren hardware endpoint.", "REFERENCE", false, SystemComponentKind.SirenController, ["RS232"]),
            new("SCADA Controller", "Control", "Bridges MQTT to the SCADA system.", "REFERENCE", true, SystemComponentKind.ScadaController, ["MQTT", "TBD"]),
            new("SCADA System", "External", "Vehicle SCADA endpoint.", "REFERENCE", false, SystemComponentKind.ScadaSystem, ["TBD"]),
            new("CAD Windows PC", "External", "Computer-aided dispatch workstation on the vehicle LAN.", "REFERENCE", false, SystemComponentKind.CadWindowsPc, ["FreeRDP over LAN"]),
        ];
    }
}
