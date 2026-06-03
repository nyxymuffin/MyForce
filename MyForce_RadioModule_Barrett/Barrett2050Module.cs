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
using System.Text.Json;
using System.Text.Json.Nodes;
using MyForce.Contracts.Radio;

namespace MyForce.RadioModules.Barrett;

/// <summary>
/// Discovery entry point for the Barrett 2050 radio module. The Audio Processor loads
/// this assembly from its plugins folder and instantiates this factory by reflection.
/// </summary>
public sealed class Barrett2050ModuleFactory : IRadioModuleFactory
{
	public string TypeId => "barrett_2050";

	public string DisplayName => "Barrett 2050";

	public string Version => "0.1.0";

	public int ContractVersion => RadioContract.Version;

	// RM-owned settings sub-schema. The AP wraps this with the common keying/detect/device
	// sections via RadioModuleSchemaBuilder. Add Barrett-specific settings here as they exist.
	public string ConfigSchema => """
		{
		  "$schema": "https://json-schema.org/draft/2020-12/schema",
		  "type": "object",
		  "properties": {},
		  "additionalProperties": false
		}
		""";

	// Barrett 2050 uses AP relay keying and AP VOX detection, and does not provide its own audio.
	public RadioCapabilities Capabilities { get; } = new(
		Keying: [KeyingMethod.Relay],
		Detect: [DetectMethod.Vox],
		ProvidesAudio: false,
		Controls: ["channel_select", "zone_select"]);

	public IRadioModule Create(IModuleHost host) => new Barrett2050Module(host);
}

/// <summary>
/// Runtime behavior for a single Barrett 2050 instance. This is a skeleton: it loads and
/// reports cleanly so the AP can discover and host it, but performs no hardware control yet.
/// </summary>
public sealed class Barrett2050Module : IRadioModule
{
	private readonly IModuleHost _host;

	private JsonNode _config = new JsonObject();

	public Barrett2050Module(IModuleHost host)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_host.Log(LogLevel.Info, "Barrett 2050 module started (skeleton; no hardware control implemented yet).");
		// TODO: open the control transport and begin polling/reporting channel and zone state.
		return Task.CompletedTask;
	}

	public Task<OperationResult> ApplyConfigAsync(JsonElement settings, CancellationToken cancellationToken)
	{
		// The AP passes only the RM-owned "settings" section (3.7.8); validate and apply it here.
		_config = JsonNode.Parse(settings.GetRawText()) ?? new JsonObject();
		// TODO: validate the RM settings section and apply it to the radio.
		return Task.FromResult(OperationResult.Ok());
	}

	public JsonNode GetConfig() => _config.DeepClone();

	public Task<OperationResult> ExecuteControlAsync(string action, JsonElement args, CancellationToken cancellationToken)
	{
		// TODO: implement "channel_select" and "zone_select".
		return Task.FromResult(OperationResult.Error($"Control '{action}' is not implemented yet."));
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		// TODO: release the control transport and any hardware handles.
		return Task.CompletedTask;
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
