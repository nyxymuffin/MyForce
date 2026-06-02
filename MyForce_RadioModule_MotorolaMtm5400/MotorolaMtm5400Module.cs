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
using System.Text.Json.Nodes;
using MyForce.Contracts.Radio;

namespace MyForce.RadioModules.MotorolaMtm5400;

/// <summary>
/// Discovery entry point for the Motorola MTM5400 radio module. The Audio Processor loads
/// this assembly from its plugins folder and instantiates this factory by reflection.
/// </summary>
public sealed class MotorolaMtm5400ModuleFactory : IRadioModuleFactory
{
	public string TypeId => "motorola_mtm5400";

	public string DisplayName => "Motorola MTM5400";

	public string Version => "0.1.0";

	public int ContractVersion => RadioContract.Version;

	// RM-owned settings sub-schema. The AP wraps this with the common keying/detect/device
	// sections via RadioModuleSchemaBuilder. Add MTM5400-specific settings here as they exist.
	public string ConfigSchema => """
		{
		  "$schema": "https://json-schema.org/draft/2020-12/schema",
		  "type": "object",
		  "properties": {},
		  "additionalProperties": false
		}
		""";

	// Motorola MTM5400 uses AP relay keying and AP VOX detection, and does not provide its own audio.
	public RadioCapabilities Capabilities { get; } = new(
		Keying: [KeyingMethod.Relay],
		Detect: [DetectMethod.Vox],
		ProvidesAudio: false,
		Controls: ["channel_select", "zone_select", "set_power"]);

	public IRadioModule Create(IModuleHost host) => new MotorolaMtm5400Module(host);
}

/// <summary>
/// Runtime behavior for a single Motorola MTM5400 instance. This is a skeleton: it loads and
/// reports cleanly so the AP can discover and host it, but performs no hardware control yet.
/// </summary>
public sealed class MotorolaMtm5400Module : IRadioModule
{
	private readonly IModuleHost _host;

	private JsonObject _config = [];

	public MotorolaMtm5400Module(IModuleHost host)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_host.Log(LogLevel.Info, "Motorola MTM5400 module started (skeleton; no hardware control implemented yet).");
		// TODO: open the control transport and begin polling/reporting channel, zone, and power state.
		return Task.CompletedTask;
	}

	public Task<OperationResult> ApplyConfigAsync(JsonObject configuration, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		_config = (JsonObject)configuration.DeepClone();
		// TODO: validate the RM settings section and apply it to the radio.
		return Task.FromResult(OperationResult.Ok());
	}

	public JsonObject GetConfig() => (JsonObject)_config.DeepClone();

	public Task<OperationResult> ExecuteControlAsync(string action, JsonObject? args, CancellationToken cancellationToken)
	{
		// TODO: implement "channel_select", "zone_select", and "set_power".
		return Task.FromResult(OperationResult.Error($"Control '{action}' is not implemented yet."));
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		// TODO: release the control transport and any hardware handles.
		return Task.CompletedTask;
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
