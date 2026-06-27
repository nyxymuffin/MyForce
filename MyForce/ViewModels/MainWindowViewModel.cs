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
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using MQTTnet;
using MyForce.Models;
using MyForce.Services;

namespace MyForce.ViewModels;

public enum AdminComponentStatus
{
	Unknown,

	Online,

	Offline,
}

public sealed record AdminSystemComponentStatus(
	string Id,
	string DisplayName,
	AdminComponentStatus Status,
	string Detail,
	string Topic)
{
	/// <summary>
	/// Gets the operator-facing status label shown in the System status page.
	/// </summary>
	public string StatusLabel => Status switch
	{
		AdminComponentStatus.Online => "ONLINE",
		AdminComponentStatus.Offline => "OFFLINE",
		_ => "UNKNOWN",
	};
}

public sealed record AdminFrameworkComponentReference(
	string Name,
	string Plane,
	string Role,
	string Status,
	bool IsCore,
	string TransportSummary);

internal sealed record PendingMqttCommand(string Topic, DateTimeOffset CreatedAtUtc);

public sealed record AdminSchemaModuleProjection(string Id, string TypeId, string Kind, string Category, IReadOnlyList<string> FieldPaths);

public enum MainConsoleTab
{
	Patrol,

	LightsAndSirens,

	Radio,

	Radar,

	AmFm,

	Cad,

	Camera,
}

public enum AdminSection
{
	System,

	SystemStatus,

	Audio,

	Radio,

	Network,

	Security,

	Integrations,

	IntegrationsWhat3Words,

	Diagnostics,
}

public enum DirectionalMode
{
	// Represents no active directional selection.
	Off,

	// Represents the left directional selection.
	Left,

	// Represents the center-out directional selection.
	CenterOut,

	// Represents the right directional selection.
	Right,
}

public enum AlertCodeMode
{
	// Represents no active alert code selection.
	Off,

	// Represents alert code 1.
	Code1,

	// Represents alert code 2.
	Code2,

	// Represents alert code 3.
	Code3,
}

public enum AuxiliaryAudioSourceMode
{
	Fm1,

	Am1,

	Bluetooth,

	InternetRadio,
}

/// <summary>
/// One row in the RADIO page SELECT picker: a radio's alias, its current channel, and
/// the action that selects it as this console's target (§5.4).
/// </summary>
public sealed class RadioSelectionItem
{
	private readonly MainWindowViewModel _owner;

	public RadioSelectionItem(string radioId, string alias, string channel, MainWindowViewModel owner)
	{
		RadioId = radioId;
		Alias = alias;
		Channel = channel;
		_owner = owner;
	}

	public string RadioId { get; }

	public string Alias { get; }

	public string Channel { get; }

	public string ChannelLabel => $"CH: {Channel}";

	public void Select() => _owner.SelectRadioTarget(RadioId);
}

/// <summary>
/// One row in the RADIO page CHANNELS picker: a channel's index + label, and the action that selects it
/// on the viewed radio (§3.11). Built from the radio's retained channel list (myforce/module/&lt;id&gt;/channels).
/// </summary>
public sealed class ChannelSelectionItem
{
	private readonly MainWindowViewModel _owner;

	public ChannelSelectionItem(int index, string label, MainWindowViewModel owner)
	{
		Index = index;
		Label = label;
		_owner = owner;
	}

	public int Index { get; }

	public string Label { get; }

	// "1: CT OPS 800" so the operator sees both the channel number and its name.
	public string DisplayLabel => $"{Index}: {Label}";

	public void Select() => _owner.SelectChannel(Index, Label);
}

/// <summary>
/// One configured channel center shown in the GEO AREA overlay: the channel, its
/// Lat/Long, and the action that clears it (centers are optional, §radio-page-semantics).
/// </summary>
public sealed class ChannelCenterListItem
{
	private readonly MainWindowViewModel _owner;

	public ChannelCenterListItem(string channel, double latitude, double longitude, MainWindowViewModel owner)
	{
		Channel = channel;
		Latitude = latitude;
		Longitude = longitude;
		_owner = owner;
	}

	public string Channel { get; }

	public double Latitude { get; }

	public double Longitude { get; }

	public string CoordsLabel => string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.0000}, {Longitude:0.0000}");

	public void Clear() => _owner.ClearGeoAreaCenter(Channel);
}

/// <summary>What an 8-slot radio button represents on the current page.</summary>
public enum RadioButtonSlotKind
{
	/// <summary>Blank, non-interactive filler slot.</summary>
	Empty,

	/// <summary>A module-declared function button.</summary>
	Function,

	/// <summary>Navigation: go to the previous page.</summary>
	PageBack,

	/// <summary>Navigation: go to the next page.</summary>
	PageNext,
}

/// <summary>
/// One slot in the 8-button radio panel (§3.10, v2.8). Usually a module-declared function button
/// (Label / active / enabled update live from the module state's "buttons" map, hence INPC), but a
/// slot can also be a page-back / page-next nav key or an empty filler when functions span pages.
/// </summary>
public sealed class RadioFunctionButton : INotifyPropertyChanged
{
	private readonly MainWindowViewModel _owner;
	private string _label;
	private bool _isActive;
	private bool _isEnabled = true;

	public RadioFunctionButton(string id, string label, bool opensMenu, MainWindowViewModel owner)
	{
		Id = id;
		_label = label;
		OpensMenu = opensMenu;
		_owner = owner;
	}

	public string Id { get; }

	public bool OpensMenu { get; }

	/// <summary>What this slot is (function vs nav vs empty). Function slots are the live master buttons.</summary>
	public RadioButtonSlotKind Kind { get; init; } = RadioButtonSlotKind.Function;

	/// <summary>Empty slots render blank and ignore presses.</summary>
	public bool IsInteractive => Kind != RadioButtonSlotKind.Empty;

	/// <summary>Show the normal (idle) button face: interactive and not toggled-active.</summary>
	public bool ShowNormal => IsInteractive && !_isActive;

	/// <summary>Show the active (highlighted) button face: interactive and toggled-active.</summary>
	public bool ShowActive => IsInteractive && _isActive;

	public string Label
	{
		get => _label;
		set
		{
			if (!string.Equals(_label, value, StringComparison.Ordinal))
			{
				_label = value;
				Raise(nameof(Label));
			}
		}
	}

	public bool IsActive
	{
		get => _isActive;
		set
		{
			if (_isActive != value)
			{
				_isActive = value;
				Raise(nameof(IsActive));
				Raise(nameof(ShowNormal));
				Raise(nameof(ShowActive));
			}
		}
	}

	public bool IsEnabled
	{
		get => _isEnabled;
		set
		{
			if (_isEnabled != value)
			{
				_isEnabled = value;
				Raise(nameof(IsEnabled));
			}
		}
	}

	public void Press()
	{
		switch (Kind)
		{
			case RadioButtonSlotKind.PageBack:
				_owner.RadioPageBack();
				break;
			case RadioButtonSlotKind.PageNext:
				_owner.RadioPageNext();
				break;
			case RadioButtonSlotKind.Function:
				_owner.PressRadioFunctionButton(Id);
				break;
		}
	}

	// Factories for the non-function slots.
	public static RadioFunctionButton EmptySlot(MainWindowViewModel owner) =>
		new(string.Empty, string.Empty, false, owner) { Kind = RadioButtonSlotKind.Empty, IsEnabled = false };

	public static RadioFunctionButton BackSlot(MainWindowViewModel owner) =>
		new("__back", "◀ BACK", false, owner) { Kind = RadioButtonSlotKind.PageBack };

	public static RadioFunctionButton NextSlot(MainWindowViewModel owner) =>
		new("__next", "NEXT ▶", false, owner) { Kind = RadioButtonSlotKind.PageNext };

	public event PropertyChangedEventHandler? PropertyChanged;

	private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One declared radio in the admin Radio config screen (§4.4): its id/type, an editable alias, and
/// the remove action. Built from the retained sys/definition so add/remove/rename show immediately.
/// </summary>
public sealed class RadioAdminItem : INotifyPropertyChanged
{
	private readonly MainWindowViewModel _owner;
	private string _aliasInput;

	public RadioAdminItem(string radioId, string typeId, string alias, MainWindowViewModel owner)
	{
		RadioId = radioId;
		TypeId = typeId;
		_aliasInput = alias;
		_owner = owner;
	}

	public string RadioId { get; }

	public string TypeId { get; }

	public string AliasInput
	{
		get => _aliasInput;
		set
		{
			if (!string.Equals(_aliasInput, value, StringComparison.Ordinal))
			{
				_aliasInput = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AliasInput)));
			}
		}
	}

	public void SaveAlias() => _owner.SetRadioAlias(RadioId, AliasInput);

	public void Remove() => _owner.RemoveRadio(RadioId);

	public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The widget kind a schema field renders as (§3.9.5 JSON-Schema-to-widget mapping).</summary>
public enum ConfigFieldKind
{
	Text,
	Number,
	Bool,
	Choice,
}

/// <summary>
/// One editable field in the schema-driven radio config editor (§3.9.5). Carries its dotted config
/// path (e.g. "device.rx_device"), the widget kind, choice options (enum or x-options pick-list), and
/// the live value. ToNode() serialises the edited value back into the config tree.
/// </summary>
public sealed class ConfigFieldItem : INotifyPropertyChanged
{
	private string _stringValue = string.Empty;
	private bool _boolValue;

	public ConfigFieldItem(string path, string title, ConfigFieldKind kind, IReadOnlyList<ResourceOptionMessage>? choices = null)
	{
		Path = path;
		Title = title;
		Kind = kind;
		Choices = choices ?? Array.Empty<ResourceOptionMessage>();
	}

	public string Path { get; }

	public string Title { get; }

	public ConfigFieldKind Kind { get; }

	public IReadOnlyList<ResourceOptionMessage> Choices { get; }

	// Widget-visibility helpers for the DataTemplate (one row renders exactly one control).
	public bool IsText => Kind == ConfigFieldKind.Text;

	public bool IsNumber => Kind == ConfigFieldKind.Number;

	public bool IsBool => Kind == ConfigFieldKind.Bool;

	public bool IsChoice => Kind == ConfigFieldKind.Choice;

	public bool IsTextOrNumber => IsText || IsNumber;

	public string StringValue
	{
		get => _stringValue;
		set
		{
			if (!string.Equals(_stringValue, value, StringComparison.Ordinal))
			{
				_stringValue = value ?? string.Empty;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StringValue)));
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedValue)));
			}
		}
	}

	public bool BoolValue
	{
		get => _boolValue;
		set
		{
			if (_boolValue != value)
			{
				_boolValue = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BoolValue)));
			}
		}
	}

	/// <summary>The selected choice's stored value, two-way bound to the ComboBox SelectedValue.</summary>
	public string SelectedValue
	{
		get => _stringValue;
		set => StringValue = value ?? string.Empty;
	}

	/// <summary>Serialises the current value to the JSON node the config tree expects for this field.</summary>
	public JsonNode? ToNode()
	{
		switch (Kind)
		{
			case ConfigFieldKind.Bool:
				return JsonValue.Create(_boolValue);
			case ConfigFieldKind.Number:
				if (long.TryParse(_stringValue, out var l))
				{
					return JsonValue.Create(l);
				}

				return double.TryParse(_stringValue, out var d) ? JsonValue.Create(d) : JsonValue.Create(0);
			default:
				return JsonValue.Create(_stringValue);
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
	private const int PresetCount = 6;

	private const int MqttCommandSchemaVersion = 1;

	private const string AdminPinCode = "2135";

	private const string AudioProcessorStatusTopic = "myforce/ap/status/service";

	private const string AudioProcessorChannelGainCommandTopic = InternetRadioMqttTopics.SpecGainCommandTopic;

	private const string AudioProcessorEntertainmentChannelId = "entertainment";

	private const string DefaultAudioOutputSpeakerId = "cabin-speaker";

	private const string GpioControllerStatusTopic = "myforce/gpio/status/service";

	private const string SirenInterfaceStatusTopic = "myforce/siren/status/service";

	// Momentary pulse width for the camera REC/STOP/AUTOZ relays. Long enough for the
	// camera/DVR to register a button press, short enough to feel instant.
	private const int CameraTriggerPulseMs = 300;

	// How long a camera button shows its press-flash highlight, confirming the press
	// registered and the GPIO pulse was published. Slightly longer than the pulse so
	// it is clearly visible.
	private const int CameraFeedbackFlashMs = 450;

	private static readonly TimeSpan ComponentHeartbeatTimeout = TimeSpan.FromSeconds(15);

	private const int InternetStationViewportSize = 6;

	private readonly DispatcherTimer _clockTimer;

	// ~1 s tick that refreshes the siren active-function lease while anything is active (§5.10.1).
	private readonly DispatcherTimer _sirenLeaseTimer;

	private readonly InternetRadioCatalogService _internetRadioCatalogService;

	private readonly AmFmUiStateStore _amFmUiStateStore;

	private readonly MqttConnectionService _mqttConnectionService;

	private readonly What3WordsService _what3WordsService;

	// Persisted per-channel geographic centers (set via radio GEO AREA) that drive the
	// patrol PROXIMITY LIST. Groundwork: populated once the per-radio channel list exists.
	private readonly ChannelCenterStore _channelCenterStore = new();

	private double _locationLatitude = 30.5422d;

	private double _locationLongitude = -97.6384d;

	private double _vehicleHeadingDegrees = 22d;

	private bool _isRadarFollowEnabled = true;

	private string _what3WordsDisplay = "CONFIG API KEY";

	private string _adminWhat3WordsApiKey = string.Empty;

	private string _adminWhat3WordsStatus = "Enter the what3words API key and save it to enable dashboard lookups.";

	private double _lastWhat3WordsLatitude = double.NaN;

	private double _lastWhat3WordsLongitude = double.NaN;

	private string _clock = string.Empty;

	private string _date = string.Empty;

	// The operator-designated talk radio (the single radio future PTT triggers will key). Resolved to the
	// first declared radio on startup; "---" until any radio is declared. Multiple radios can be "listened"
	// to (RX monitor) at once, but only one is the talk radio at a time.
	private string _currentTalkRadio = "---";

	private string _currentRadioChannel = string.Empty;

	// Stores the current alert light and siren status shown in the status panel.
	private string _alertLightSiren = "OFF";

	// Stores the current directional status shown in the status panel.
	private string _directionalStatus = "OFF";

	// Mirrors the master output volume (_masterVolume) so the radio-page VOL display matches it.
	private string _currentTalkRadioVolume = "18";

	private decimal _amFmFrequency = 97.5m;

	private int _amFmVolume = 25;

	// Master output volume (0..25) driven by the PATROL screen volume buttons; sets the master sink level.
	private int _masterVolume = 18;

	private decimal _amFrequency = 87.5m;

	private bool _isAmFmMuted;

	private bool _isAmFmStereoEnabled = true;

	private AuxiliaryAudioSourceMode _selectedAuxiliarySourceMode = AuxiliaryAudioSourceMode.Fm1;

	private bool _isInternetChannelListVisible;

	private string _internetGenreFilter = "ALL";

	private string _internetLanguageFilter = "ALL";

	private InternetRadioStation? _selectedInternetStation;

	private string _bluetoothDisplayLabel = "BT AUDIO";

	private bool _isAmFmChannelSetArmed;

	private decimal?[] _fmPresetStations = new decimal?[PresetCount];

	private decimal?[] _amPresetStations = new decimal?[PresetCount];

	private string?[] _internetPresetStationNames = new string?[PresetCount];

	private IReadOnlyList<InternetRadioStation> _internetRadioStations = Array.Empty<InternetRadioStation>();

	private int _internetStationViewportStartIndex;

	// Patrol "Radio Current Channel List": one slot per declared radio (positional RADIO 1..6); a slot with
	// no declared radio reads "NOT USED" rather than filler data.
	private const string RadioSlotNotUsed = "NOT USED";

	private string _radio1ChannelName = RadioSlotNotUsed;

	private string _radio2ChannelName = RadioSlotNotUsed;

	private string _radio3ChannelName = RadioSlotNotUsed;

	private string _radio4ChannelName = RadioSlotNotUsed;

	private string _radio5ChannelName = RadioSlotNotUsed;

	private string _radio6ChannelName = RadioSlotNotUsed;

	// Patrol PROXIMITY LIST slots start blank; they fill only once channel centers are configured and
	// ranked by distance (no filler data).
	private string _proximityChannel1 = string.Empty;

	private string _proximityChannel2 = string.Empty;

	private string _proximityChannel3 = string.Empty;

	private string _proximityChannel4 = string.Empty;

	private string _mqttStatus = "OFFLINE";

	private string _mqttEndpoint = "127.0.0.1:1883";

	private string _mqttDetail = "Broker not connected.";

	private bool _isMqttConnectionBannerVisible = true;

	private string _mqttConnectionBannerText = "MQTT broker is offline. System state may be stale.";

	private readonly Dictionary<string, PendingMqttCommand> _pendingMqttCommands = new(StringComparer.OrdinalIgnoreCase);

	private string _mqttCommandFeedback = "No pending MQTT commands.";

	private static readonly JsonSerializerOptions MqttJsonSerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false,
	};

	private static readonly IReadOnlyList<SystemComponent> FrameworkReferenceComponents = SystemComponent.CreateFrameworkReference();

	private MainConsoleTab _selectedTab = MainConsoleTab.Patrol;

	private bool _isAdminOverlayVisible;

	private bool _isAdminAuthenticated;

	private AdminSection _selectedAdminSection = AdminSection.System;

	private string _adminSectionTitle = "SYSTEM";

	private string _adminSectionDescription = "Core console configuration and startup settings will live here.";

	private string _adminPinEntry = string.Empty;

	private string? _adminSessionCredential;

	private string _adminPinStatus = "Enter PIN to unlock admin controls.";

	private IReadOnlyList<AudioDeviceStateMessage> _audioOutputDevices = Array.Empty<AudioDeviceStateMessage>();

	private IReadOnlyList<RadioRegistryEntryMessage> _availableRadioTypes = Array.Empty<RadioRegistryEntryMessage>();

	private IReadOnlyList<RadioRuntimeEntryMessage> _radioRuntimeEntries = Array.Empty<RadioRuntimeEntryMessage>();

	private IReadOnlyList<AdminSchemaModuleProjection> _adminSchemaModules = Array.Empty<AdminSchemaModuleProjection>();

	private string _apConfiguredOutputSpeakerId = DefaultAudioOutputSpeakerId;

	private string _selectedAdminOutputSpeakerId = DefaultAudioOutputSpeakerId;

	private string _adminAudioStatus = "Waiting for AP audio routing state.";

	// The System Status page lists the fixed infrastructure components (UI + the three controllers) plus
	// any CONFIGURED radio (added when its retained status arrives). It must NOT list radio module plugin
	// types or unconfigured/unloaded modules - only a radio the operator actually declared shows up.
	private IReadOnlyList<AdminSystemComponentStatus> _systemComponentStatuses =
	[
		new("ui", "Main UI", AdminComponentStatus.Online, "Local console is running.", "LOCAL"),
		new("audio-processor", "Audio Processor", AdminComponentStatus.Unknown, "Waiting for retained MQTT status.", AudioProcessorStatusTopic),
		new("gpio-controller", "GPIO Controller", AdminComponentStatus.Unknown, "Waiting for retained MQTT status.", GpioControllerStatusTopic),
		new("siren-interface", "Siren Controller", AdminComponentStatus.Unknown, "Waiting for retained MQTT status.", SirenInterfaceStatusTopic),
	];

	// The fixed infrastructure component ids that always remain on the System Status page (never removed,
	// only their status changes). Everything else is a configured radio that comes and goes.
	private static readonly HashSet<string> InfrastructureComponentIds =
		new(["ui", "audio-processor", "gpio-controller", "siren-interface"], StringComparer.OrdinalIgnoreCase);

	// Tracks the currently selected directional mode.
	private DirectionalMode _selectedDirectional = DirectionalMode.Off;

	// Tracks the currently selected alert code mode.
	private AlertCodeMode _selectedAlertCode = AlertCodeMode.Off;

	public string _CurSelChExt1;

	public string _CurSelChExt2;

	public string _CurSelChExt3;

	public string _CurSelChExt4;

	public MainWindowViewModel()
	{
		_amFmUiStateStore = new AmFmUiStateStore();
		_internetRadioCatalogService = new InternetRadioCatalogService();
		_mqttConnectionService = new MqttConnectionService();
		_what3WordsService = new What3WordsService();
		_mqttConnectionService.StateChanged += OnMqttStateChanged;
		_mqttConnectionService.MessageReceived += OnMqttMessageReceived;
		_clockTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1),
		};

		_clockTimer.Tick += OnClockTimerTick;
		_sirenLeaseTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1),
		};
		_sirenLeaseTimer.Tick += OnSirenLeaseTick;
		UpdateClock();
		LoadInternetRadioCatalog();
		RestoreAmFmUiState();
		// Persisted channel centers survive a UI reboot, but the patrol PROXIMITY LIST only re-ranks on a
		// location change. Rank once at startup so the list is populated immediately instead of staying
		// blank until the next GPS update.
		RefreshProximityList();
		ApplyMqttState(_mqttConnectionService.CurrentState);
		_adminWhat3WordsApiKey = _what3WordsService.GetConfiguredApiKey() ?? string.Empty;
		_ = RefreshWhat3WordsAsync();
		_clockTimer.Start();
		_sirenLeaseTimer.Start();
		_ = InitializeMqttAsync();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Title => "MyForce Main Console";

	public string SpeedLabel => "SPD";

	public string SpeedValue => "55";

	public string LocationLabel => "LOCATION";

	public double LocationLatitude
	{
		get => _locationLatitude;
		set
		{
			if (!SetProperty(ref _locationLatitude, value))
			{
				return;
			}

			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocationValue)));
			_ = RefreshWhat3WordsAsync();
			RefreshProximityList();
		}
	}

	public double LocationLongitude
	{
		get => _locationLongitude;
		set
		{
			if (!SetProperty(ref _locationLongitude, value))
			{
				return;
			}

			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocationValue)));
			_ = RefreshWhat3WordsAsync();
			RefreshProximityList();
		}
	}

	public string LocationValue => string.Create(
		CultureInfo.InvariantCulture,
		$"{LocationLatitude:0.0000}, {LocationLongitude:0.0000}");

	public string What3WordsDisplay
	{
		get => _what3WordsDisplay;
		private set => SetProperty(ref _what3WordsDisplay, value);
	}

	public double VehicleHeadingDegrees
	{
		get => _vehicleHeadingDegrees;
		set => SetProperty(ref _vehicleHeadingDegrees, value);
	}

	public bool IsRadarFollowEnabled
	{
		get => _isRadarFollowEnabled;
		set => SetProperty(ref _isRadarFollowEnabled, value);
	}

	public string RadarFollowButtonText => IsRadarFollowEnabled ? "FOLLOW ON" : "FOLLOW OFF";

	public void ToggleRadarFollow()
	{
		if (!SetProperty(ref _isRadarFollowEnabled, !_isRadarFollowEnabled, nameof(IsRadarFollowEnabled)))
		{
			return;
		}

		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadarFollowButtonText)));
	}

	public string CurrentTalkRadio
	{
		get => _currentTalkRadio;
		set => SetProperty(ref _currentTalkRadio, value);
	}

	public string CurrentRadioChannel
	{
		get => _currentRadioChannel;
		set => SetProperty(ref _currentRadioChannel, value);
	}

	public string AlertLightSiren
	{
		get => _alertLightSiren;
		set => SetProperty(ref _alertLightSiren, value);
	}

	public string DirectionalStatus
	{
		get => _directionalStatus;
		set => SetProperty(ref _directionalStatus, value);
	}

	public string EntertainmentModePrefix => _selectedAuxiliarySourceMode switch
	{
		AuxiliaryAudioSourceMode.Fm1 => "FM1:",
		AuxiliaryAudioSourceMode.Am1 => "AM1:",
		AuxiliaryAudioSourceMode.Bluetooth => "BT:",
		AuxiliaryAudioSourceMode.InternetRadio => "INT:",
		_ => throw new ArgumentOutOfRangeException(),
	};

	public string EntertainmentModeName => _isAmFmMuted ? "MUTE" : _selectedAuxiliarySourceMode switch
	{
		AuxiliaryAudioSourceMode.Fm1 => AmFmFrequencyDisplay,
		AuxiliaryAudioSourceMode.Am1 => _amFrequency.ToString("0.0", CultureInfo.InvariantCulture),
		AuxiliaryAudioSourceMode.Bluetooth => _bluetoothDisplayLabel,
		AuxiliaryAudioSourceMode.InternetRadio => _selectedInternetStation?.DisplayName ?? "NO CHANNEL",
		_ => throw new ArgumentOutOfRangeException(),
	};

	//Extra Info Provided by the Currently Selected channel. could be currently talking rid or somehting
	public string CurSelChExt1 { get => _CurSelChExt1; set => SetProperty(ref _CurSelChExt1, value); }

	public string CurSelChExt2 { get => _CurSelChExt2; set => SetProperty(ref _CurSelChExt2, value); }

	public string CurSelChExt3 { get => _CurSelChExt3; set => SetProperty(ref _CurSelChExt3, value); }

	public string CurSelChExt4 { get => _CurSelChExt4; set => SetProperty(ref _CurSelChExt4, value); }

	// Hand grip controller (HCD) + soft-key scaffolding. The physical hand grip selects a mode
	// (Lights / Radio / Patrol) which contextualizes the soft keys; the RX RID is the radio id
	// received on the current channel. Sources are wired as the HCD and radio RID land.
	private string? _rxRid;

	private HandGripMode _handGripMode = HandGripMode.Patrol;

	/// <summary>The radio id received on the currently selected channel (who is transmitting).</summary>
	public string? RxRid
	{
		get => _rxRid;
		set
		{
			if (SetProperty(ref _rxRid, value))
			{
				OnPropertyChanged(nameof(RxRidDisplay));
			}
		}
	}

	/// <summary>The physical hand grip controller's current mode; drives the active soft-key set.</summary>
	public HandGripMode HandGripMode
	{
		get => _handGripMode;
		set
		{
			if (SetProperty(ref _handGripMode, value))
			{
				OnPropertyChanged(nameof(HandGripModeDisplay));
				OnPropertyChanged(nameof(SoftKeysDisplay));
			}
		}
	}

	public string RxRidDisplay => $"RX RID: {(string.IsNullOrWhiteSpace(_rxRid) ? "---" : _rxRid)}";

	public string HandGripModeDisplay => $"Hand Grip Mode: {_handGripMode}";

	public string SoftKeysDisplay => $"Soft Keys: {_handGripMode}";

	// Soft keys 1-6 under the directionals. 1-4 are visible (L/S, EXT AUDIO, MEMO, AM/FM); 5-6 are
	// hidden placeholders (under MEMO and AM/FM) for custom soft keys revealed per hand grip mode.
	public const int SoftKeyCount = 6;

	private bool _isSoftKey5Visible;

	private bool _isSoftKey6Visible;

	public bool IsSoftKey5Visible
	{
		get => _isSoftKey5Visible;
		set => SetProperty(ref _isSoftKey5Visible, value);
	}

	public bool IsSoftKey6Visible
	{
		get => _isSoftKey6Visible;
		set => SetProperty(ref _isSoftKey6Visible, value);
	}

	/// <summary>
	/// Activates a soft key (1-6) from the touchscreen or the physical hand grip controller. Performs the
	/// key's built-in local action and republishes the activation so downstream mappings can react.
	/// </summary>
	public void TriggerSoftKey(int index)
	{
		if (index < 1 || index > SoftKeyCount)
		{
			return;
		}

		// Built-in local actions for keys that already drive UI behaviour today.
		switch (index)
		{
			case 1:
				// L/S button (touch or VIP/HCD): kill all active siren/light functions (§5.10.1).
				AllSirenLightsOff();
				break;
			case 4:
				ToggleAmFmMute();
				break;
			default:
				break;
		}

		PublishSoftKeyCommand(index);
	}

	/// <summary>Applies an HCD-published hand grip mode (lights / radio / patrol).</summary>
	public void ApplyHandGripMode(string? mode)
	{
		if (Enum.TryParse<HandGripMode>(mode, ignoreCase: true, out var parsed))
		{
			HandGripMode = parsed;
		}
	}

	private void PublishSoftKeyCommand(int index)
	{
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: true);
		var command = new SoftKeyCommandMessage(
			envelope.V,
			envelope.Ts,
			envelope.MsgId,
			envelope.Auth,
			index,
			_handGripMode.ToString());
		_ = PublishCommandAsync(InternetRadioMqttTopics.SoftKeyCommandTopic, command);
	}

	public string MqttStatus
	{
		get => _mqttStatus;
		private set => SetProperty(ref _mqttStatus, value);
	}

	public string MqttEndpoint
	{
		get => _mqttEndpoint;
		private set => SetProperty(ref _mqttEndpoint, value);
	}

	public string MqttDetail
	{
		get => _mqttDetail;
		private set => SetProperty(ref _mqttDetail, value);
	}

	public bool IsMqttConnectionBannerVisible
	{
		get => _isMqttConnectionBannerVisible;
		private set => SetProperty(ref _isMqttConnectionBannerVisible, value);
	}

	public string MqttConnectionBannerText
	{
		get => _mqttConnectionBannerText;
		private set => SetProperty(ref _mqttConnectionBannerText, value);
	}

	public string MqttCommandFeedback
	{
		get => _mqttCommandFeedback;
		private set => SetProperty(ref _mqttCommandFeedback, value);
	}

	public string Clock
	{
		get => _clock;
		private set => SetProperty(ref _clock, value);
	}

	public string Date
	{
		get => _date;
		private set => SetProperty(ref _date, value);
	}

	public string CurrentTalkRadioVolume
	{
		get => _currentTalkRadioVolume;
		set => SetProperty(ref _currentTalkRadioVolume, value);
	}

	public string AmFmFrequencyDisplay => _amFmFrequency.ToString("0.0", CultureInfo.InvariantCulture);

	/// <summary>
	/// Gets the source-aware title shown above the primary AM/FM display area.
	/// </summary>
	public string AmFmModuleTitle => _selectedAuxiliarySourceMode switch
	{
		AuxiliaryAudioSourceMode.Fm1 => "FM RADIO",
		AuxiliaryAudioSourceMode.Am1 => "AM RADIO",
		AuxiliaryAudioSourceMode.Bluetooth => "BLUETOOTH",
		AuxiliaryAudioSourceMode.InternetRadio => "INTERNET RADIO",
		_ => throw new ArgumentOutOfRangeException(),
	};

	/// <summary>
	/// Gets the primary source display, switching to the selected station name for INT mode.
	/// </summary>
	public string AmFmPrimaryDisplay => _selectedAuxiliarySourceMode switch
	{
		AuxiliaryAudioSourceMode.InternetRadio => _selectedInternetStation?.DisplayName ?? "NO CHANNEL",
		AuxiliaryAudioSourceMode.Bluetooth => _bluetoothDisplayLabel,
		AuxiliaryAudioSourceMode.Am1 => _amFrequency.ToString("0.0", CultureInfo.InvariantCulture),
		_ => AmFmFrequencyDisplay,
	};

	/// <summary>
	/// Gets the font size for the primary display so long INT station names remain visible.
	/// </summary>
	public double AmFmPrimaryDisplayFontSize => _selectedAuxiliarySourceMode == AuxiliaryAudioSourceMode.InternetRadio
		? 24d
		: 42d;

	public string AmFmBandLabel => _selectedAuxiliarySourceMode switch
	{
		AuxiliaryAudioSourceMode.Fm1 => "FM1",
		AuxiliaryAudioSourceMode.Am1 => "AM1",
		AuxiliaryAudioSourceMode.Bluetooth => "BT",
		AuxiliaryAudioSourceMode.InternetRadio => "INT",
		_ => throw new ArgumentOutOfRangeException(),
	};

	public string AmFmStereoLabel => _isAmFmStereoEnabled ? "STEREO" : "MONO";

	public string AmFmVolumeDisplay => $"VOL: {_amFmVolume}";

	/// <summary>
	/// Gets the entertainment source volume readout shown under the primary display.
	/// </summary>
	public string AmFmDetailLine => AmFmVolumeDisplay;

	/// <summary>
	/// Gets the INT-only metadata line shown beneath the volume readout.
	/// </summary>
	public string AmFmSecondaryDetailLine => _selectedAuxiliarySourceMode switch
	{
		AuxiliaryAudioSourceMode.InternetRadio => _selectedInternetStation is null
			? "NO CHANNEL"
			: $"{_selectedInternetStation.Genre} / {_selectedInternetStation.Language}",
		_ => string.Empty,
	};

	/// <summary>
	/// Indicates whether the INT-only metadata line should be shown for the current mode.
	/// </summary>
	public bool IsAmFmDetailVisible => _selectedAuxiliarySourceMode == AuxiliaryAudioSourceMode.InternetRadio;

	/// <summary>
	/// Indicates whether the seek controls should be visible for tuner-based sources.
	/// </summary>
	public bool IsSeekControlsVisible => _selectedAuxiliarySourceMode is AuxiliaryAudioSourceMode.Fm1 or AuxiliaryAudioSourceMode.Am1;

	public bool IsInternetChannelListVisible
	{
		get => _isInternetChannelListVisible;
		private set => SetProperty(ref _isInternetChannelListVisible, value);
	}

	public bool IsInternetChannelListButtonVisible => IsInternetSourceSelected;

	/// <summary>
	/// Gets the touch-friendly station subset displayed inside the INT popup.
	/// </summary>
	public IReadOnlyList<InternetRadioStation> VisibleInternetRadioStations => FilteredInternetRadioStations
		.Skip(_internetStationViewportStartIndex)
		.Take(InternetStationViewportSize)
		.ToArray();

	public bool CanScrollInternetStationsUp => _internetStationViewportStartIndex > 0;

	public bool CanScrollInternetStationsDown => _internetStationViewportStartIndex + InternetStationViewportSize < FilteredInternetRadioStations.Count;

	public string InternetGenreFilter => _internetGenreFilter;

	public string InternetLanguageFilter => _internetLanguageFilter;

	public string InternetSelectedStationName => _selectedInternetStation?.DisplayName ?? "NO CHANNEL";

	public string InternetSelectedStreamUrl => _selectedInternetStation?.StreamUrl ?? "NO STREAM URL";

	public string InternetSelectedMetadata => _selectedInternetStation is null
		? "Select an INT channel."
		: $"{_selectedInternetStation.Genre} / {_selectedInternetStation.Language} / {_selectedInternetStation.Bitrate} kbps";

	public IReadOnlyList<string> InternetGenreOptions => ["ALL", .. _internetRadioStations
		.Select(static station => station.Genre)
		.Where(static genre => !string.IsNullOrWhiteSpace(genre))
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.OrderBy(static genre => genre, StringComparer.OrdinalIgnoreCase)];

	public IReadOnlyList<string> InternetLanguageOptions => ["ALL", .. _internetRadioStations
		.Select(static station => station.Language)
		.Where(static language => !string.IsNullOrWhiteSpace(language))
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.OrderBy(static language => language, StringComparer.OrdinalIgnoreCase)];

	public IReadOnlyList<InternetRadioStation> FilteredInternetRadioStations => _internetRadioStations
		.Where(station => string.Equals(_internetGenreFilter, "ALL", StringComparison.OrdinalIgnoreCase) || string.Equals(station.Genre, _internetGenreFilter, StringComparison.OrdinalIgnoreCase))
		.Where(station => string.Equals(_internetLanguageFilter, "ALL", StringComparison.OrdinalIgnoreCase) || string.Equals(station.Language, _internetLanguageFilter, StringComparison.OrdinalIgnoreCase))
		.ToArray();

	public string AmFmActiveSourceSummary => _selectedAuxiliarySourceMode switch
	{
		AuxiliaryAudioSourceMode.Fm1 => "SOURCE: FM/AM -> AP MIX",
		AuxiliaryAudioSourceMode.Am1 => "SOURCE: AM -> AP MIX",
		AuxiliaryAudioSourceMode.Bluetooth => "SOURCE: BT -> AP MIX",
		AuxiliaryAudioSourceMode.InternetRadio => "SOURCE: INT -> AP MIX",
		_ => throw new ArgumentOutOfRangeException(),
	};

	public bool IsAmFmMuted => _isAmFmMuted;

	public bool IsAmFmChannelSetArmed => _isAmFmChannelSetArmed;

	public bool IsFm1SourceSelected => _selectedAuxiliarySourceMode == AuxiliaryAudioSourceMode.Fm1;

	public bool IsAm1SourceSelected => _selectedAuxiliarySourceMode == AuxiliaryAudioSourceMode.Am1;

	public bool IsBluetoothSourceSelected => _selectedAuxiliarySourceMode == AuxiliaryAudioSourceMode.Bluetooth;

	public bool IsInternetSourceSelected => _selectedAuxiliarySourceMode == AuxiliaryAudioSourceMode.InternetRadio;

	public string AuxSourceStatusLine1 => _selectedAuxiliarySourceMode switch
	{
		AuxiliaryAudioSourceMode.Fm1 => "FM/AM READY",
		AuxiliaryAudioSourceMode.Am1 => "AM READY",
		AuxiliaryAudioSourceMode.Bluetooth => "BT A2DP",
		AuxiliaryAudioSourceMode.InternetRadio => "NET STREAM",
		_ => throw new ArgumentOutOfRangeException(),
	};

	public string AuxSourceStatusLine2 => _isAmFmMuted ? "DUCKED / MUTE" : "LIVE TO AP";

	public string AmFmPreset1Label => GetPresetLabel(0);

	public string AmFmPreset2Label => GetPresetLabel(1);

	public string AmFmPreset3Label => GetPresetLabel(2);

	public string AmFmPreset4Label => GetPresetLabel(3);

	public string AmFmPreset5Label => GetPresetLabel(4);

	public string AmFmPreset6Label => GetPresetLabel(5);

	public string Radio1ChannelName
	{
		get => _radio1ChannelName;
		set => SetProperty(ref _radio1ChannelName, value);
	}

	public string Radio2ChannelName
	{
		get => _radio2ChannelName;
		set => SetProperty(ref _radio2ChannelName, value);
	}

	public string Radio3ChannelName
	{
		get => _radio3ChannelName;
		set => SetProperty(ref _radio3ChannelName, value);
	}

	public string Radio4ChannelName
	{
		get => _radio4ChannelName;
		set => SetProperty(ref _radio4ChannelName, value);
	}

	public string Radio5ChannelName
	{
		get => _radio5ChannelName;
		set => SetProperty(ref _radio5ChannelName, value);
	}

	public string Radio6ChannelName
	{
		get => _radio6ChannelName;
		set => SetProperty(ref _radio6ChannelName, value);
	}

	public string ProximityChannel1
	{
		get => _proximityChannel1;
		set => SetProperty(ref _proximityChannel1, value);
	}

	public string ProximityChannel2
	{
		get => _proximityChannel2;
		set => SetProperty(ref _proximityChannel2, value);
	}

	public string ProximityChannel3
	{
		get => _proximityChannel3;
		set => SetProperty(ref _proximityChannel3, value);
	}

	public string ProximityChannel4
	{
		get => _proximityChannel4;
		set => SetProperty(ref _proximityChannel4, value);
	}

	public MainConsoleTab SelectedTab
	{
		get => _selectedTab;
		private set
		{
			if (!SetProperty(ref _selectedTab, value))
			{
				return;
			}

			RaiseTabStateChanged();
		}
	}

	public bool IsPatrolTabSelected => SelectedTab == MainConsoleTab.Patrol;

	public bool IsLightsAndSirensTabSelected => SelectedTab == MainConsoleTab.LightsAndSirens;

	public bool IsRadioTabSelected => SelectedTab == MainConsoleTab.Radio;

	public bool IsRadarTabSelected => SelectedTab == MainConsoleTab.Radar;

	public bool IsAmFmTabSelected => SelectedTab == MainConsoleTab.AmFm;

	public bool IsCadTabSelected => SelectedTab == MainConsoleTab.Cad;

	public bool IsCameraTabSelected => SelectedTab == MainConsoleTab.Camera;

	public bool IsPatrolContentVisible => IsPatrolTabSelected;

	public bool IsLightsAndSirensContentVisible => IsLightsAndSirensTabSelected;

	public bool IsRadioContentVisible => IsRadioTabSelected;

	public bool IsRadarContentVisible => IsRadarTabSelected;

	public bool IsAmFmContentVisible => IsAmFmTabSelected;

	public bool IsCadContentVisible => IsCadTabSelected;

	public bool IsCameraContentVisible => IsCameraTabSelected;

	public bool IsAdminOverlayVisible
	{
		get => _isAdminOverlayVisible;
		private set => SetProperty(ref _isAdminOverlayVisible, value);
	}

	public AdminSection SelectedAdminSection
	{
		get => _selectedAdminSection;
		private set => SetProperty(ref _selectedAdminSection, value);
	}

	public string AdminSectionTitle
	{
		get => _adminSectionTitle;
		private set => SetProperty(ref _adminSectionTitle, value);
	}

	public string AdminSectionDescription
	{
		get => _adminSectionDescription;
		private set => SetProperty(ref _adminSectionDescription, value);
	}

	/// <summary>
	/// Indicates whether the admin overlay is unlocked for configuration changes.
	/// </summary>
	public bool IsAdminAuthenticated
	{
		get => _isAdminAuthenticated;
		private set => SetProperty(ref _isAdminAuthenticated, value);
	}

	/// <summary>
	/// Indicates whether the PIN entry keypad should be shown.
	/// </summary>
	public bool IsAdminPinPromptVisible => IsAdminOverlayVisible && !IsAdminAuthenticated;

	/// <summary>
	/// Indicates whether the authenticated admin content should be shown.
	/// </summary>
	public bool IsAdminContentVisible => IsAdminOverlayVisible && IsAdminAuthenticated;

	/// <summary>
	/// Gets the masked PIN entry displayed above the virtual numpad.
	/// </summary>
	public string AdminPinMaskedEntry => string.IsNullOrEmpty(_adminPinEntry)
		? "----"
		: string.Concat(Enumerable.Repeat("*", _adminPinEntry.Length).Concat(Enumerable.Repeat("-", Math.Max(AdminPinCode.Length - _adminPinEntry.Length, 0))));

	/// <summary>
	/// Gets the current admin PIN prompt status text.
	/// </summary>
	public string AdminPinStatus
	{
		get => _adminPinStatus;
		private set => SetProperty(ref _adminPinStatus, value);
	}

	public IReadOnlyList<AudioDeviceStateMessage> AudioOutputDevices
	{
		get => _audioOutputDevices;
		private set => SetProperty(ref _audioOutputDevices, value);
	}

	public IReadOnlyList<RadioRuntimeEntryMessage> RadioRuntimeEntries
	{
		get => _radioRuntimeEntries;
		private set => SetProperty(ref _radioRuntimeEntries, value);
	}

	public IReadOnlyList<RadioRegistryEntryMessage> AvailableRadioTypes
	{
		get => _availableRadioTypes;
		private set => SetProperty(ref _availableRadioTypes, value);
	}

	public bool HasAvailableRadioTypes => AvailableRadioTypes.Count > 0;

	public string AdminAvailableRadioTypeSummary => HasAvailableRadioTypes
		? $"AVAILABLE TYPES: {AvailableRadioTypes.Count}  BUILT-IN RESOURCES: {AvailableRadioTypes.Count(static radio => string.Equals(radio.Kind, "Resource", StringComparison.OrdinalIgnoreCase))}"
		: "Waiting for retained AP radio type availability.";

	public int AdminRadioEntryCount => RadioRuntimeEntries.Count;

	public bool HasAdminRadioEntries => AdminRadioEntryCount > 0;

	public bool IsAdminRadioSectionSelected => SelectedAdminSection == AdminSection.Radio;

	public string AdminRadioSummary => HasAdminRadioEntries
		? $"RADIOS: {AdminRadioEntryCount}  MODULES: {RadioRuntimeEntries.Count(static radio => string.Equals(radio.Kind, "Module", StringComparison.OrdinalIgnoreCase) || string.Equals(radio.Kind, "AdvancedModule", StringComparison.OrdinalIgnoreCase))}  RESOURCES: {RadioRuntimeEntries.Count(static radio => string.Equals(radio.Kind, "Resource", StringComparison.OrdinalIgnoreCase))}"
		: HasAvailableRadioTypes
			? "No declared radios are active yet. Use the addable type list below to add built-in resources or RM-backed radios."
			: "Waiting for retained AP radio runtime data.";

	public IReadOnlyList<AdminSchemaModuleProjection> AdminSchemaModules
	{
		get => _adminSchemaModules;
		private set => SetProperty(ref _adminSchemaModules, value);
	}

	// --- Radio config screen (§4.4): add / remove / alias declared radios -------
	// Sourced from the retained sys/definition (so add/remove/rename reflect immediately, even before
	// the AP re-hydrates the live instance). Commands are admin-class (carry the admin credential).

	private IReadOnlyList<RadioAdminItem> _radioAdminItems = Array.Empty<RadioAdminItem>();

	public IReadOnlyList<RadioAdminItem> RadioAdminItems
	{
		get => _radioAdminItems;
		private set => SetProperty(ref _radioAdminItems, value);
	}

	public bool HasRadioAdminItems => RadioAdminItems.Count > 0;

	// Add a radio of the given type (alias optional). The AP assigns the id and seeds defaults (§4.4).
	public void AddRadio(string typeId, string? alias)
	{
		if (string.IsNullOrWhiteSpace(typeId))
		{
			return;
		}

		var envelope = CreateCommandEnvelope(isAdminCommand: true, includeMessageId: true);
		var command = new AddModuleCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, typeId, string.IsNullOrWhiteSpace(alias) ? null : alias);
		_ = PublishCommandAsync(InternetRadioMqttTopics.AddModuleCommandTopic, command);
	}

	public void RemoveRadio(string radioId)
	{
		if (string.IsNullOrWhiteSpace(radioId))
		{
			return;
		}

		var envelope = CreateCommandEnvelope(isAdminCommand: true, includeMessageId: true);
		var command = new RemoveModuleCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, radioId);
		_ = PublishCommandAsync(InternetRadioMqttTopics.RemoveModuleCommandTopic, command);
	}

	public void SetRadioAlias(string radioId, string alias)
	{
		if (string.IsNullOrWhiteSpace(radioId) || string.IsNullOrWhiteSpace(alias))
		{
			return;
		}

		var envelope = CreateCommandEnvelope(isAdminCommand: true, includeMessageId: true);
		var command = new SetAliasCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, radioId, alias);
		_ = PublishCommandAsync(InternetRadioMqttTopics.SetAliasCommandTopic, command);
	}

	// Rebuild the editable radio list from the declared system definition (radio-category modules).
	private void ApplySystemDefinition(SystemDefinitionMessage definition)
	{
		var radios = (definition.Modules ?? Array.Empty<SystemDefinitionModuleMessage>())
			.Where(module => string.Equals(module.Category, "radio", StringComparison.OrdinalIgnoreCase))
			.Select(module => new RadioAdminItem(module.Id, module.TypeId, module.Alias, this))
			.ToArray();
		RadioAdminItems = radios;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRadioAdminItems)));
	}

	// --- Schema-driven config editor (§3.9.5) -----------------------------------
	// Renders a radio's config_schema as widgets, resolving x-options/device pick-lists against the
	// retained resource lists (sys/audio_devices, sys/relay_sets), prefilled from its current config.

	private readonly Dictionary<string, JsonElement> _radioSchemas = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, JsonElement> _radioConfigs = new(StringComparer.OrdinalIgnoreCase);
	private IReadOnlyList<ResourceOptionMessage> _captureDevices = Array.Empty<ResourceOptionMessage>();
	private IReadOnlyList<ResourceOptionMessage> _playbackDevices = Array.Empty<ResourceOptionMessage>();
	private IReadOnlyList<ResourceOptionMessage> _relaySetOptions = Array.Empty<ResourceOptionMessage>();
	private IReadOnlyList<ResourceOptionMessage> _serialPortOptions = Array.Empty<ResourceOptionMessage>();

	private bool _isRadioConfigEditorVisible;
	private string _editingRadioId = string.Empty;
	private string _editingRadioTitle = string.Empty;
	private IReadOnlyList<ConfigFieldItem> _radioConfigFields = Array.Empty<ConfigFieldItem>();

	public bool IsRadioConfigEditorVisible
	{
		get => _isRadioConfigEditorVisible;
		private set => SetProperty(ref _isRadioConfigEditorVisible, value);
	}

	public string EditingRadioTitle
	{
		get => _editingRadioTitle;
		private set => SetProperty(ref _editingRadioTitle, value);
	}

	public IReadOnlyList<ConfigFieldItem> RadioConfigFields
	{
		get => _radioConfigFields;
		private set => SetProperty(ref _radioConfigFields, value);
	}

	public bool HasRadioConfigFields => RadioConfigFields.Count > 0;

	// Resource lists feeding the x-options / device pick-lists (sys/audio_devices, sys/relay_sets).
	private void ApplyAudioDevices(SystemAudioDevicesMessage devices)
	{
		_captureDevices = devices.Capture ?? Array.Empty<ResourceOptionMessage>();
		_playbackDevices = devices.Playback ?? Array.Empty<ResourceOptionMessage>();
	}

	private void ApplyRelaySets(SystemRelaySetsMessage relaySets)
		=> _relaySetOptions = (relaySets.RelaySets ?? Array.Empty<RelaySetOptionMessage>())
			.Select(set => new ResourceOptionMessage(set.Value, $"{set.Label} ({set.Channels} ch)")).ToArray();

	// Host serial ports for the com_port pick-list (sys/serial_ports, §5.1).
	private void ApplySerialPorts(SystemSerialPortsMessage serialPorts)
		=> _serialPortOptions = serialPorts.Ports ?? Array.Empty<ResourceOptionMessage>();

	// Open the editor for a radio: parse its schema + current config into editable fields.
	public void OpenRadioConfigEditor(string radioId)
	{
		if (string.IsNullOrWhiteSpace(radioId) || !_radioSchemas.TryGetValue(radioId, out var schema))
		{
			MqttCommandFeedback = $"No config schema available for '{radioId}' yet.";
			return;
		}

		_editingRadioId = radioId;
		EditingRadioTitle = $"CONFIGURE: {radioId}";
		_radioConfigs.TryGetValue(radioId, out var config);
		RadioConfigFields = BuildConfigFields(schema, _radioConfigs.ContainsKey(radioId) ? config : (JsonElement?)null);
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRadioConfigFields)));
		IsRadioConfigEditorVisible = true;
	}

	public void CancelRadioConfigEditor() => IsRadioConfigEditorVisible = false;

	// Save: rebuild the nested config object from the field values and publish cmd/config (admin-class).
	public void SaveRadioConfig()
	{
		if (string.IsNullOrWhiteSpace(_editingRadioId))
		{
			return;
		}

		// Config is an admin-class command, so it needs the admin session credential. Guard explicitly so a
		// lost admin session shows feedback instead of throwing (which previously crashed the UI on save).
		if (!IsAdminAuthenticated)
		{
			MqttCommandFeedback = "Admin session required to save config.";
			return;
		}

		var config = new JsonObject();
		foreach (var field in RadioConfigFields)
		{
			SetJsonByPath(config, field.Path, field.ToNode());
		}

		var envelope = CreateCommandEnvelope(isAdminCommand: true, includeMessageId: true);
		var command = new ModuleConfigCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, _editingRadioId, config);
		_ = PublishCommandAsync(InternetRadioMqttTopics.ModuleConfigCommandTopic(_editingRadioId), command);
		IsRadioConfigEditorVisible = false;
	}

	// Walk the schema's object properties into a flat list of dotted-path fields (§3.9.5).
	private IReadOnlyList<ConfigFieldItem> BuildConfigFields(JsonElement schema, JsonElement? currentConfig)
	{
		var fields = new List<ConfigFieldItem>();
		WalkSchema(schema, string.Empty, currentConfig, fields);
		return fields;
	}

	private void WalkSchema(JsonElement schema, string prefix, JsonElement? config, List<ConfigFieldItem> fields)
	{
		if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		foreach (var property in properties.EnumerateObject())
		{
			var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
			var fieldSchema = property.Value;
			var type = fieldSchema.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;

			// Nested object -> recurse so the path stays dotted (e.g. "device.rx_device").
			if (string.Equals(type, "object", StringComparison.OrdinalIgnoreCase))
			{
				WalkSchema(fieldSchema, path, config, fields);
				continue;
			}

			var title = fieldSchema.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
				? titleElement.GetString()!
				: path;
			// Use the saved value if present, else the schema's "default" so a fresh radio prefills sensibly.
			var defaultValue = fieldSchema.TryGetProperty("default", out var defaultElement) && defaultElement.ValueKind != JsonValueKind.Undefined
				? defaultElement
				: (JsonElement?)null;
			var currentValue = ResolveConfigValue(config, path) ?? defaultValue;

			// x-options (or a known device/relay leaf) -> dynamic pick-list resolved at runtime.
			var resourceChoices = ResolvePickListChoices(fieldSchema, property.Name);
			if (resourceChoices is not null)
			{
				fields.Add(new ConfigFieldItem(path, title, ConfigFieldKind.Choice, resourceChoices)
				{
					StringValue = currentValue?.ToString() ?? string.Empty,
				});
				continue;
			}

			// enum -> static pick-list.
			if (fieldSchema.TryGetProperty("enum", out var enumElement) && enumElement.ValueKind == JsonValueKind.Array)
			{
				var choices = enumElement.EnumerateArray()
					.Select(item => item.ToString())
					.Select(value => new ResourceOptionMessage(value, value))
					.ToArray();
				fields.Add(new ConfigFieldItem(path, title, ConfigFieldKind.Choice, choices)
				{
					StringValue = currentValue?.ToString() ?? string.Empty,
				});
				continue;
			}

			switch (type)
			{
				case "boolean":
					fields.Add(new ConfigFieldItem(path, title, ConfigFieldKind.Bool)
					{
						BoolValue = currentValue?.ValueKind == JsonValueKind.True,
					});
					break;
				case "integer":
				case "number":
					fields.Add(new ConfigFieldItem(path, title, ConfigFieldKind.Number)
					{
						StringValue = currentValue?.ToString() ?? string.Empty,
					});
					break;
				default:
					fields.Add(new ConfigFieldItem(path, title, ConfigFieldKind.Text)
					{
						StringValue = currentValue?.ToString() ?? string.Empty,
					});
					break;
			}
		}
	}

	// Resolve a field's pick-list options from x-options or a well-known device/relay leaf name.
	private IReadOnlyList<ResourceOptionMessage>? ResolvePickListChoices(JsonElement fieldSchema, string leafName)
	{
		string? resource = null;
		if (fieldSchema.TryGetProperty("x-options", out var xOptions) && xOptions.ValueKind == JsonValueKind.String)
		{
			resource = xOptions.GetString();
		}

		// Map by resource hint or by the v3.0 device/relay leaf names (§3.7.8) so pick-lists work even
		// before schemas declare x-options.
		var key = resource ?? leafName;
		if (key.Contains("relay", StringComparison.OrdinalIgnoreCase))
		{
			return _relaySetOptions;
		}

		// Serial-port fields (e.g. Barrett's com_port) resolve against the host's enumerated ports.
		if (key.Contains("com_port", StringComparison.OrdinalIgnoreCase)
			|| key.Contains("serial", StringComparison.OrdinalIgnoreCase))
		{
			return _serialPortOptions;
		}

		if (key.Contains("rx_device", StringComparison.OrdinalIgnoreCase) || key.Contains("capture", StringComparison.OrdinalIgnoreCase))
		{
			return _captureDevices;
		}

		if (key.Contains("tx_device", StringComparison.OrdinalIgnoreCase) || key.Contains("playback", StringComparison.OrdinalIgnoreCase) || string.Equals(leafName, "soundcard", StringComparison.OrdinalIgnoreCase))
		{
			return _playbackDevices;
		}

		return null;
	}

	// Read the current value at a dotted path from the radio's config JSON, or null if absent.
	private static JsonElement? ResolveConfigValue(JsonElement? config, string path)
	{
		if (config is not JsonElement element || element.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		var current = element;
		foreach (var segment in path.Split('.'))
		{
			if (current.ValueKind != JsonValueKind.Object || !TryGetPropertyFlexible(current, segment, out var next))
			{
				return null;
			}

			current = next;
		}

		return current;
	}

	// Match a schema field name against a config property tolerating snake_case vs camelCase vs casing
	// (e.g. schema "ptt_lead_ms" matches a config "pttLeadMs"), so the editor prefills regardless of how
	// the config was serialized.
	private static bool TryGetPropertyFlexible(JsonElement obj, string name, out JsonElement value)
	{
		if (obj.TryGetProperty(name, out value))
		{
			return true;
		}

		var target = NormalizeKey(name);
		foreach (var property in obj.EnumerateObject())
		{
			if (string.Equals(NormalizeKey(property.Name), target, StringComparison.Ordinal))
			{
				value = property.Value;
				return true;
			}
		}

		value = default;
		return false;

		static string NormalizeKey(string key) => key.Replace("_", string.Empty).ToLowerInvariant();
	}

	// Write a value into a nested JsonObject by dotted path, creating intermediate objects as needed.
	private static void SetJsonByPath(JsonObject root, string path, JsonNode? value)
	{
		var segments = path.Split('.');
		var current = root;
		for (var i = 0; i < segments.Length - 1; i++)
		{
			if (current[segments[i]] is not JsonObject child)
			{
				child = new JsonObject();
				current[segments[i]] = child;
			}

			current = child;
		}

		current[segments[^1]] = value;
	}

	public string AdminSchemaSummary => AdminSchemaModules.Count == 0
		? "Waiting for module registry schemas."
		: $"SCHEMA MODULES: {AdminSchemaModules.Count}  FIELDS: {AdminSchemaModules.Sum(static module => module.FieldPaths.Count)}";

	public bool IsAdminAudioSectionSelected => SelectedAdminSection == AdminSection.Audio;

	public bool IsAdminNetworkSectionSelected => SelectedAdminSection == AdminSection.Network;

	public bool IsAdminIntegrationsSectionSelected => SelectedAdminSection == AdminSection.Integrations;

	public bool IsAdminIntegrationsWhat3WordsSectionSelected => SelectedAdminSection == AdminSection.IntegrationsWhat3Words;

	public string ApConfiguredOutputSpeakerId
	{
		get => _apConfiguredOutputSpeakerId;
		private set => SetProperty(ref _apConfiguredOutputSpeakerId, value);
	}

	public string SelectedAdminOutputSpeakerId
	{
		get => _selectedAdminOutputSpeakerId;
		private set => SetProperty(ref _selectedAdminOutputSpeakerId, value);
	}

	public string AdminAudioStatus
	{
		get => _adminAudioStatus;
		private set => SetProperty(ref _adminAudioStatus, value);
	}

	public bool HasAudioOutputDevices => AudioOutputDevices.Count > 0;

	public bool IsSelectedSpeakerInSync => string.Equals(SelectedAdminOutputSpeakerId, ApConfiguredOutputSpeakerId, StringComparison.OrdinalIgnoreCase);

	public string SelectedAudioOutputSpeakerLabel => ResolveAudioOutputSpeakerLabel(SelectedAdminOutputSpeakerId);

	public string ApConfiguredOutputSpeakerLabel => ResolveAudioOutputSpeakerLabel(ApConfiguredOutputSpeakerId);

	public string AppliedAudioOutputSpeakerLabel => ResolveAudioOutputSpeakerLabel(ApConfiguredOutputSpeakerId);

	public string AdminAudioDeviceSummary => $"MASTER: {AppliedAudioOutputSpeakerLabel}  CHANNELS: {Math.Max(AudioOutputDevices.Count - 1, 0)}";

	public string AdminWhat3WordsApiKey
	{
		get => _adminWhat3WordsApiKey;
		set => SetProperty(ref _adminWhat3WordsApiKey, value);
	}

	public string AdminWhat3WordsStatus
	{
		get => _adminWhat3WordsStatus;
		private set => SetProperty(ref _adminWhat3WordsStatus, value);
	}

	/// <summary>
	/// Gets the summary line shown in the System status page.
	/// </summary>
	public string AdminSystemStatusSummary => $"ONLINE: {SystemComponentStatuses.Count(component => component.Status == AdminComponentStatus.Online)}  OFFLINE: {SystemComponentStatuses.Count(component => component.Status == AdminComponentStatus.Offline)}  UNKNOWN: {SystemComponentStatuses.Count(component => component.Status == AdminComponentStatus.Unknown)}";

	/// <summary>
	/// Gets the component status rows displayed in the System status page.
	/// </summary>
	public IReadOnlyList<AdminSystemComponentStatus> SystemComponentStatuses
	{
		get => _systemComponentStatuses;
		private set => SetProperty(ref _systemComponentStatuses, value);
	}

	/// <summary>
	/// Section 6 component reference used to document the framework-defined system topology in the admin UI.
	/// </summary>
	public IReadOnlyList<AdminFrameworkComponentReference> AdminFrameworkComponentReferences => FrameworkReferenceComponents
		.Select(static component => new AdminFrameworkComponentReference(
			component.Name,
			component.Plane,
			component.Role,
			component.Status,
			component.IsCore,
			string.Join(", ", component.Transports)))
		.ToArray();

	public bool IsAdminSystemGeneralSectionSelected => SelectedAdminSection == AdminSection.System;

	public bool IsAdminSystemStatusSectionSelected => SelectedAdminSection == AdminSection.SystemStatus;

	public bool IsAdminNonSystemSectionSelected => SelectedAdminSection is not (AdminSection.System or AdminSection.SystemStatus or AdminSection.Audio or AdminSection.Radio or AdminSection.Integrations or AdminSection.IntegrationsWhat3Words);

	// Indicates whether the left directional button is active.
	public bool IsDirectionalLeftSelected => SelectedDirectional == DirectionalMode.Left;

	// Indicates whether the center-out directional button is active.
	public bool IsDirectionalCenterOutSelected => SelectedDirectional == DirectionalMode.CenterOut;

	// Indicates whether the right directional button is active.
	public bool IsDirectionalRightSelected => SelectedDirectional == DirectionalMode.Right;

	// Indicates whether the Code 1 button is active.
	public bool IsCode1Selected => SelectedAlertCode == AlertCodeMode.Code1;

	// Indicates whether the Code 2 button is active.
	public bool IsCode2Selected => SelectedAlertCode == AlertCodeMode.Code2;

	// Indicates whether the Code 3 button is active.
	public bool IsCode3Selected => SelectedAlertCode == AlertCodeMode.Code3;

	// Returns the current display text for the Code 1 button.
	public string Code1StateText => IsCode1Selected ? "ON" : "OFF";

	// Returns the current display text for the Code 2 button.
	public string Code2StateText => IsCode2Selected ? "ON" : "OFF";

	// Returns the current display text for the Code 3 button.
	public string Code3StateText => IsCode3Selected ? "ON" : "OFF";

	// Stores the selected directional mode and updates the derived UI state.
	private DirectionalMode SelectedDirectional
	{
		get => _selectedDirectional;
		set
		{
			if (!SetProperty(ref _selectedDirectional, value))
			{
				return;
			}

			DirectionalStatus = value switch
			{
				DirectionalMode.Off => "OFF",
				DirectionalMode.Left => "LEFT",
				DirectionalMode.CenterOut => "CENTER OUT",
				DirectionalMode.Right => "RIGHT",
				_ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
			};

			RaiseDirectionalStateChanged();

			// Drive the physical arrow/traffic-advisor relays on the Siren Interface
			// Controller. "center" energises both directional relays at once (§ siren
			// wiring spec); the UI writes to the system only via MQTT commands (§3.9.3).
			PublishSirenDirectionalCommand(value);
		}
	}

	// Stores the selected alert code mode and updates the derived UI state.
	private AlertCodeMode SelectedAlertCode
	{
		get => _selectedAlertCode;
		set
		{
			if (!SetProperty(ref _selectedAlertCode, value))
			{
				return;
			}

			AlertLightSiren = value switch
			{
				AlertCodeMode.Off => "OFF",
				AlertCodeMode.Code1 => "CODE 1",
				AlertCodeMode.Code2 => "CODE 2",
				AlertCodeMode.Code3 => "CODE 3",
				_ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
			};

			RaiseAlertCodeStateChanged();

			// Drive the interlocked Code1/2/3 relays on the Siren Interface Controller
			// (the firmware de-energises the other codes automatically). UI writes to
			// the system only via MQTT commands (§3.9.3).
			PublishSirenCodeCommand(value);
		}
	}

	public void SelectTab(MainConsoleTab tab)
	{
		SelectedTab = tab;
	}

	public void OpenAdminOverlay()
	{
		_adminPinEntry = string.Empty;
		AdminPinStatus = "Enter PIN to unlock admin controls.";
		IsAdminAuthenticated = false;
		IsAdminOverlayVisible = true;
		RaiseAdminOverlayStateChanged();
	}

	public void CloseAdminOverlay()
	{
		_adminPinEntry = string.Empty;
		AdminPinStatus = "Enter PIN to unlock admin controls.";
		IsAdminAuthenticated = false;
		IsAdminOverlayVisible = false;
		RaiseAdminOverlayStateChanged();
	}

	public void SelectAdminSection(AdminSection section)
	{
		SelectedAdminSection = section;

		switch (section)
		{
			case AdminSection.System:
				AdminSectionTitle = "SYSTEM / GENERAL";
				AdminSectionDescription = "General system settings and overview content live here.";
				break;

			case AdminSection.SystemStatus:
				AdminSectionTitle = "SYSTEM / STATUS";
				AdminSectionDescription = "Live component availability is shown here using retained MQTT state plus heartbeat freshness checks.";
				break;

			case AdminSection.Audio:
				AdminSectionTitle = "AUDIO";
				AdminSectionDescription = "Audio routing, gain staging, and operator device settings will live here.";
				break;

			case AdminSection.Radio:
				AdminSectionTitle = "RADIO";
				AdminSectionDescription = "Radio resources, channels, and talk group configuration will live here.";
				break;

			case AdminSection.Network:
				AdminSectionTitle = "NETWORK";
				AdminSectionDescription = "MQTT, LAN, broker, and remote endpoint settings are configured here.";
				break;

			case AdminSection.Security:
				AdminSectionTitle = "SECURITY";
				AdminSectionDescription = "Authentication, roles, and system access configuration will live here.";
				break;

			case AdminSection.Integrations:
				AdminSectionTitle = "INTEGRATIONS";
				AdminSectionDescription = "Select an external integration category to configure its settings.";
				break;

			case AdminSection.IntegrationsWhat3Words:
				AdminSectionTitle = "INTEGRATIONS / WHAT3WORDS";
				AdminSectionDescription = "Configure the what3words API key used for dashboard location lookups.";
				break;

			case AdminSection.Diagnostics:
				AdminSectionTitle = "DIAGNOSTICS";
				AdminSectionDescription = "Health, logging, and diagnostics configuration will live here.";
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(section), section, null);
		}

		RaiseAdminSystemStatusChanged();
		RaiseAdminAudioStateChanged();
		RaiseAdminNetworkStateChanged();
	}

	public async Task SaveAdminWhat3WordsApiKeyAsync()
	{
		try
		{
			_what3WordsService.SaveApiKey(AdminWhat3WordsApiKey);
			AdminWhat3WordsApiKey = _what3WordsService.GetConfiguredApiKey() ?? string.Empty;
			AdminWhat3WordsStatus = string.IsNullOrWhiteSpace(AdminWhat3WordsApiKey)
				? "what3words API key cleared. Dashboard lookup is disabled."
				: "what3words API key saved. Refreshing dashboard location words.";

			_lastWhat3WordsLatitude = double.NaN;
			_lastWhat3WordsLongitude = double.NaN;
			await RefreshWhat3WordsAsync().ConfigureAwait(false);
			RaiseAdminNetworkStateChanged();
		}
		catch (IOException)
		{
			AdminWhat3WordsStatus = "Unable to save the what3words API key to the UI config file.";
			RaiseAdminNetworkStateChanged();
		}
	}

	public void SelectAdminOutputSpeaker(string deviceId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

		if (!AudioOutputDevices.Any(device => string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidOperationException($"Unknown AP output speaker '{deviceId}'.");
		}

		SelectedAdminOutputSpeakerId = deviceId;
		AdminAudioStatus = IsSelectedSpeakerInSync
			? $"AP master speaker already routed to {SelectedAudioOutputSpeakerLabel}."
			: $"Selected {SelectedAudioOutputSpeakerLabel}. Push config to apply on the AP.";
		RaiseAdminAudioStateChanged();
	}

	public void PushAdminAudioConfig()
	{
		if (string.IsNullOrWhiteSpace(SelectedAdminOutputSpeakerId))
		{
			AdminAudioStatus = "Select an output speaker before pushing config.";
			RaiseAdminAudioStateChanged();
			return;
		}

		AdminAudioStatus = $"Pushing AP speaker output selection: {SelectedAudioOutputSpeakerLabel}.";
		RaiseAdminAudioStateChanged();
		_ = PublishOutputSpeakerCommandAsync(SelectedAdminOutputSpeakerId);
	}

	/// <summary>
	/// Section 4.6: adds a digit to the admin PIN entry and establishes the per-command admin credential session when the configured PIN is entered.
	/// </summary>
	public void AppendAdminPinDigit(char digit)
	{
		if (!char.IsDigit(digit) || IsAdminAuthenticated)
		{
			return;
		}

		if (_adminPinEntry.Length >= AdminPinCode.Length)
		{
			return;
		}

		_adminPinEntry += digit;
		AdminPinStatus = "Enter PIN to unlock admin controls.";
		RaiseAdminOverlayStateChanged();

		if (_adminPinEntry.Length < AdminPinCode.Length)
		{
			return;
		}

		if (string.Equals(_adminPinEntry, AdminPinCode, StringComparison.Ordinal))
		{
			IsAdminAuthenticated = true;
			_adminSessionCredential = AdminPinCode;
			AdminPinStatus = "Admin access granted.";
			RaiseAdminOverlayStateChanged();
			return;
		}

		_adminPinEntry = string.Empty;
		AdminPinStatus = "Invalid PIN. Try again.";
		RaiseAdminOverlayStateChanged();
	}

	/// <summary>
	/// Removes the last entered admin PIN digit.
	/// </summary>
	public void BackspaceAdminPin()
	{
		if (string.IsNullOrEmpty(_adminPinEntry) || IsAdminAuthenticated)
		{
			return;
		}

		_adminPinEntry = _adminPinEntry[..^1];
		AdminPinStatus = "Enter PIN to unlock admin controls.";
		RaiseAdminOverlayStateChanged();
	}

	/// <summary>
	/// Clears the current admin PIN entry.
	/// </summary>
	public void ClearAdminPin()
	{
		if (IsAdminAuthenticated)
		{
			return;
		}

		_adminPinEntry = string.Empty;
		AdminPinStatus = "Enter PIN to unlock admin controls.";
		RaiseAdminOverlayStateChanged();
	}

	// Toggles the directional selection, allowing the active choice to turn off.
	public void ToggleDirectional(DirectionalMode directional)
	{
		SelectedDirectional = SelectedDirectional == directional ? DirectionalMode.Off : directional;
	}

	// Toggles the alert code selection, allowing the active choice to turn off.
	public void ToggleAlertCode(AlertCodeMode alertCode)
	{
		SelectedAlertCode = SelectedAlertCode == alertCode ? AlertCodeMode.Off : alertCode;
	}

	public void ToggleAmFmMute()
	{
		_isAmFmMuted = !_isAmFmMuted;
		SaveAmFmUiState();
		SyncInternetRadioPlaybackAsync();
		RaiseAmFmStateChanged();
	}

	/// <summary>
	/// Moves the active INT station selection backward or forward through the filtered list.
	/// </summary>
	public void StepInternetStation(int delta)
	{
		if (!IsInternetSourceSelected)
		{
			return;
		}

		var stations = FilteredInternetRadioStations;
		if (stations.Count == 0)
		{
			_selectedInternetStation = null;
			RaiseInternetChannelListStateChanged();
			RaiseAmFmStateChanged();
			return;
		}

		var currentIndex = 0;
		if (_selectedInternetStation is not null)
		{
			for (var index = 0; index < stations.Count; index++)
			{
				if (!Equals(stations[index], _selectedInternetStation))
				{
					continue;
				}

				currentIndex = index;
				break;
			}
		}

		if (currentIndex < 0)
		{
			currentIndex = 0;
		}

		var nextIndex = currentIndex + delta;
		if (nextIndex < 0)
		{
			nextIndex = stations.Count - 1;
		}
		else if (nextIndex >= stations.Count)
		{
			nextIndex = 0;
		}

		_selectedInternetStation = stations[nextIndex];
		EnsureInternetStationVisible(nextIndex);
		SaveAmFmUiState();
		SyncInternetRadioPlaybackAsync();
		RaiseInternetChannelListStateChanged();
		RaiseAmFmStateChanged();
	}

	public void SelectAuxiliarySource(AuxiliaryAudioSourceMode sourceMode)
	{
		var previousMode = _selectedAuxiliarySourceMode;
		_selectedAuxiliarySourceMode = sourceMode;
		_isAmFmChannelSetArmed = false;
		if (sourceMode != AuxiliaryAudioSourceMode.InternetRadio)
		{
			IsInternetChannelListVisible = false;
		}

		if (sourceMode == AuxiliaryAudioSourceMode.Am1)
		{
			_isAmFmStereoEnabled = false;
		}
		else if (sourceMode == AuxiliaryAudioSourceMode.Fm1)
		{
			_isAmFmStereoEnabled = true;
		}

		SaveAmFmUiState();
		if (previousMode != sourceMode)
		{
			SyncInternetRadioPlaybackAsync();
		}

		RaiseAmFmStateChanged();
	}

	public void ToggleInternetChannelList()
	{
		if (!IsInternetSourceSelected)
		{
			return;
		}

		IsInternetChannelListVisible = !IsInternetChannelListVisible;
		RaiseInternetChannelListStateChanged();
	}

	public void CloseInternetChannelList()
	{
		if (!IsInternetChannelListVisible)
		{
			return;
		}

		IsInternetChannelListVisible = false;
		RaiseInternetChannelListStateChanged();
	}

	public void CycleInternetGenreFilter()
	{
		_internetGenreFilter = GetNextFilterValue(InternetGenreOptions, _internetGenreFilter);
		ResetInternetStationViewport(preserveSelection: true);
		RaiseInternetChannelListStateChanged();
		RaiseAmFmStateChanged();
	}

	public void CycleInternetLanguageFilter()
	{
		_internetLanguageFilter = GetNextFilterValue(InternetLanguageOptions, _internetLanguageFilter);
		ResetInternetStationViewport(preserveSelection: true);
		RaiseInternetChannelListStateChanged();
		RaiseAmFmStateChanged();
	}

	/// <summary>
	/// Scrolls the visible INT station window upward for touch-friendly browsing.
	/// </summary>
	public void ScrollInternetStationsUp()
	{
		if (!CanScrollInternetStationsUp)
		{
			return;
		}

		_internetStationViewportStartIndex = Math.Max(0, _internetStationViewportStartIndex - 1);
		RaiseInternetChannelListStateChanged();
	}

	/// <summary>
	/// Scrolls the visible INT station window downward for touch-friendly browsing.
	/// </summary>
	public void ScrollInternetStationsDown()
	{
		if (!CanScrollInternetStationsDown)
		{
			return;
		}

		_internetStationViewportStartIndex = Math.Min(FilteredInternetRadioStations.Count - InternetStationViewportSize, _internetStationViewportStartIndex + 1);
		RaiseInternetChannelListStateChanged();
	}

	public void SelectInternetStation(string streamUrl)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(streamUrl);

		var station = FilteredInternetRadioStations
			.FirstOrDefault(candidate => string.Equals(candidate.StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase));

		if (station is null)
		{
			throw new InvalidOperationException($"Unknown internet radio stream '{streamUrl}'.");
		}

		_selectedInternetStation = station;
		var stationIndex = -1;
		var filteredStations = FilteredInternetRadioStations;
		for (var index = 0; index < filteredStations.Count; index++)
		{
			if (!Equals(filteredStations[index], station))
			{
				continue;
			}

			stationIndex = index;
			break;
		}

		EnsureInternetStationVisible(stationIndex);
		IsInternetChannelListVisible = false;
		SaveAmFmUiState();
		SyncInternetRadioPlaybackAsync();
		RaiseInternetChannelListStateChanged();
		RaiseAmFmStateChanged();
	}

	public void StepAmFmTuneUp()
	{
		if (IsInternetSourceSelected)
		{
			StepInternetStation(1);
			return;
		}

		if (IsAm1SourceSelected)
		{
			_amFrequency = decimal.Round(decimal.Min(_amFrequency + 0.2m, 171.0m), 1, MidpointRounding.AwayFromZero);
			SaveAmFmUiState();
			RaiseAmFmStateChanged();
			return;
		}

		_amFmFrequency = decimal.Round(decimal.Min(_amFmFrequency + 0.2m, 107.9m), 1, MidpointRounding.AwayFromZero);
		SaveAmFmUiState();
		RaiseAmFmStateChanged();
	}

	public void StepAmFmTuneDown()
	{
		if (IsInternetSourceSelected)
		{
			StepInternetStation(-1);
			return;
		}

		if (IsAm1SourceSelected)
		{
			_amFrequency = decimal.Round(decimal.Max(_amFrequency - 0.2m, 87.5m), 1, MidpointRounding.AwayFromZero);
			SaveAmFmUiState();
			RaiseAmFmStateChanged();
			return;
		}

		_amFmFrequency = decimal.Round(decimal.Max(_amFmFrequency - 0.2m, 87.5m), 1, MidpointRounding.AwayFromZero);
		SaveAmFmUiState();
		RaiseAmFmStateChanged();
	}

	public void SeekAmFmUp()
	{
		if (IsAm1SourceSelected)
		{
			_amFrequency = decimal.Round(decimal.Min(_amFrequency + 10.0m, 171.0m), 1, MidpointRounding.AwayFromZero);
			SaveAmFmUiState();
			RaiseAmFmStateChanged();
			return;
		}

		_amFmFrequency = decimal.Round(decimal.Min(_amFmFrequency + 0.5m, 107.9m), 1, MidpointRounding.AwayFromZero);
		SaveAmFmUiState();
		RaiseAmFmStateChanged();
	}

	public void SeekAmFmDown()
	{
		if (IsAm1SourceSelected)
		{
			_amFrequency = decimal.Round(decimal.Max(_amFrequency - 10.0m, 87.5m), 1, MidpointRounding.AwayFromZero);
			SaveAmFmUiState();
			RaiseAmFmStateChanged();
			return;
		}

		_amFmFrequency = decimal.Round(decimal.Max(_amFmFrequency - 0.5m, 87.5m), 1, MidpointRounding.AwayFromZero);
		SaveAmFmUiState();
		RaiseAmFmStateChanged();
	}

	public void ScanAmFm()
	{
		SeekAmFmUp();
	}

	public void StoreCurrentAmFmChannel()
	{
		if (IsBluetoothSourceSelected)
		{
			_isAmFmChannelSetArmed = false;
			RaiseAmFmStateChanged();
			return;
		}

		_isAmFmChannelSetArmed = !_isAmFmChannelSetArmed;
		RaiseAmFmStateChanged();
	}

	// PATROL screen volume buttons control the master output volume (the master sink level).
	public string MasterVolumeDisplay => $"MASTER VOLUME: {_masterVolume}";

	public void IncreaseMasterVolume()
	{
		_masterVolume = Math.Min(_masterVolume + 1, MaxSourceVolume);
		PublishMasterVolumeCommand();
		RaiseMasterVolumeChanged();
	}

	public void DecreaseMasterVolume()
	{
		_masterVolume = Math.Max(_masterVolume - 1, 0);
		PublishMasterVolumeCommand();
		RaiseMasterVolumeChanged();
	}

	// Master and radio-page VOL are the same operator output level for now, so both displays move together
	// when either set of volume buttons is pressed.
	private void RaiseMasterVolumeChanged()
	{
		CurrentTalkRadioVolume = _masterVolume.ToString(CultureInfo.InvariantCulture);
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MasterVolumeDisplay)));
	}

	public void IncreaseAmFmVolume()
	{
		_amFmVolume = Math.Min(_amFmVolume + 1, MaxSourceVolume);
		SaveAmFmUiState();
		PublishEntertainmentVolumeCommand();
		RaiseAmFmStateChanged();
	}

	public void DecreaseAmFmVolume()
	{
		_amFmVolume = Math.Max(_amFmVolume - 1, 0);
		SaveAmFmUiState();
		PublishEntertainmentVolumeCommand();
		RaiseAmFmStateChanged();
	}

	public void SelectAmFmPreset(int presetIndex)
	{
		if (presetIndex < 0 || presetIndex >= PresetCount)
		{
			throw new ArgumentOutOfRangeException(nameof(presetIndex));
		}

		if (_isAmFmChannelSetArmed)
		{
			SaveCurrentPreset(presetIndex);
			_isAmFmChannelSetArmed = false;
			SaveAmFmUiState();
			RaiseAmFmStateChanged();
			return;
		}

		if (!RecallPreset(presetIndex))
		{
			RaiseAmFmStateChanged();
			return;
		}

		SaveAmFmUiState();
		if (IsInternetSourceSelected)
		{
			SyncInternetRadioPlaybackAsync();
			RaiseInternetChannelListStateChanged();
		}

		RaiseAmFmStateChanged();
	}

	public void Dispose()
	{
		_clockTimer.Stop();
		_clockTimer.Tick -= OnClockTimerTick;
		_sirenLeaseTimer.Stop();
		_sirenLeaseTimer.Tick -= OnSirenLeaseTick;
		_mqttConnectionService.StateChanged -= OnMqttStateChanged;
		_mqttConnectionService.MessageReceived -= OnMqttMessageReceived;
		_mqttConnectionService.Dispose();
	}

	private async Task InitializeMqttAsync()
	{
		var settings = new MqttConnectionSettings(
			Host: "127.0.0.1",
			Port: 1883,
			ClientId: $"myforce-ui-{Environment.MachineName}-{Environment.ProcessId}");

		await _mqttConnectionService.ConnectAsync(settings).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.SystemPluginsTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.SystemDefinitionTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.SystemAudioDevicesTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.SystemRelaySetsTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.SystemSerialPortsTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.ModuleTopicFilter).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.ConsoleTxTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.HcdModeTopicFilter).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.HcdSoftKeyTopicFilter).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.AudioProcessorRegistryTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.StateTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.AudioFrameworkStateTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.RadioRuntimeStateTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.RoutingStateTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(AudioProcessorStatusTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(GpioControllerStatusTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(SirenInterfaceStatusTopic).ConfigureAwait(false);
		RestoreEntertainmentModePlayback();
	}

	private void OnClockTimerTick(object? sender, EventArgs e)
	{
		UpdateClock();
		RefreshComponentHeartbeatStatus();
	}

	private void OnMqttStateChanged(object? sender, MqttConnectionState state)
	{
		Dispatcher.UIThread.Post(() => ApplyMqttState(state));
	}

	private void OnMqttMessageReceived(object? sender, MqttApplicationMessage message)
	{
		if (HandleSpecMqttMessage(message))
		{
			return;
		}

		// Hand grip controller (HCD): mode selection and soft-key presses (topics are console-scoped).
		if (message.Topic.EndsWith("/hcd/mode", StringComparison.OrdinalIgnoreCase))
		{
			var payload = message.ConvertPayloadToString();
			if (!string.IsNullOrWhiteSpace(payload))
			{
				var mode = JsonSerializer.Deserialize<HcdModeMessage>(payload, MqttJsonSerializerOptions);
				if (mode is not null)
				{
					Dispatcher.UIThread.Post(() => ApplyHandGripMode(mode.Mode));
				}
			}

			return;
		}

		if (message.Topic.EndsWith("/hcd/softkey", StringComparison.OrdinalIgnoreCase))
		{
			var payload = message.ConvertPayloadToString();
			if (!string.IsNullOrWhiteSpace(payload))
			{
				var softKey = JsonSerializer.Deserialize<HcdSoftKeyMessage>(payload, MqttJsonSerializerOptions);
				if (softKey is not null)
				{
					Dispatcher.UIThread.Post(() => TriggerSoftKey(softKey.Index));
				}
			}

			return;
		}

		// sys/plugins is the authoritative "addable radio types" list (discovered RM plugins + built-in
		// AP resources). Without this, only already-declared radio instances showed as addable, so a
		// discovered-but-uninstantiated plugin (e.g. Barrett) never appeared.
		if (string.Equals(message.Topic, InternetRadioMqttTopics.SystemPluginsTopic, StringComparison.OrdinalIgnoreCase))
		{
			var pluginsPayload = message.ConvertPayloadToString();
			if (string.IsNullOrWhiteSpace(pluginsPayload))
			{
				return;
			}

			var plugins = JsonSerializer.Deserialize<SystemPluginsMessage>(pluginsPayload, MqttJsonSerializerOptions);
			if (plugins is null)
			{
				return;
			}

			Dispatcher.UIThread.Post(() => ApplySystemPlugins(plugins));
			return;
		}

		if (string.Equals(message.Topic, InternetRadioMqttTopics.AudioProcessorRegistryTopic, StringComparison.OrdinalIgnoreCase))
		{
			var registryPayload = message.ConvertPayloadToString();
			if (string.IsNullOrWhiteSpace(registryPayload))
			{
				return;
			}

			var registry = JsonSerializer.Deserialize<AudioProcessorRegistryMessage>(registryPayload, MqttJsonSerializerOptions);
			if (registry is null)
			{
				return;
			}

			Dispatcher.UIThread.Post(() => ApplyAudioProcessorRegistry(registry));
			return;
		}

		if (string.Equals(message.Topic, InternetRadioMqttTopics.SystemDefinitionTopic, StringComparison.OrdinalIgnoreCase))
		{
			var definitionPayload = message.ConvertPayloadToString();
			if (string.IsNullOrWhiteSpace(definitionPayload))
			{
				return;
			}

			var definition = JsonSerializer.Deserialize<SystemDefinitionMessage>(definitionPayload, MqttJsonSerializerOptions);
			if (definition is null)
			{
				return;
			}

			Dispatcher.UIThread.Post(() => ApplySystemDefinition(definition));
			return;
		}

		if (string.Equals(message.Topic, InternetRadioMqttTopics.SystemAudioDevicesTopic, StringComparison.OrdinalIgnoreCase))
		{
			var payload = message.ConvertPayloadToString();
			var devices = string.IsNullOrWhiteSpace(payload) ? null : JsonSerializer.Deserialize<SystemAudioDevicesMessage>(payload, MqttJsonSerializerOptions);
			if (devices is not null)
			{
				Dispatcher.UIThread.Post(() => ApplyAudioDevices(devices));
			}

			return;
		}

		if (string.Equals(message.Topic, InternetRadioMqttTopics.SystemRelaySetsTopic, StringComparison.OrdinalIgnoreCase))
		{
			var payload = message.ConvertPayloadToString();
			var relaySets = string.IsNullOrWhiteSpace(payload) ? null : JsonSerializer.Deserialize<SystemRelaySetsMessage>(payload, MqttJsonSerializerOptions);
			if (relaySets is not null)
			{
				Dispatcher.UIThread.Post(() => ApplyRelaySets(relaySets));
			}

			return;
		}

		if (string.Equals(message.Topic, InternetRadioMqttTopics.SystemSerialPortsTopic, StringComparison.OrdinalIgnoreCase))
		{
			var payload = message.ConvertPayloadToString();
			var serialPorts = string.IsNullOrWhiteSpace(payload) ? null : JsonSerializer.Deserialize<SystemSerialPortsMessage>(payload, MqttJsonSerializerOptions);
			if (serialPorts is not null)
			{
				Dispatcher.UIThread.Post(() => ApplySerialPorts(serialPorts));
			}

			return;
		}

		if (string.Equals(message.Topic, InternetRadioMqttTopics.AudioFrameworkStateTopic, StringComparison.OrdinalIgnoreCase))
		{
			var frameworkPayload = message.ConvertPayloadToString();
			if (string.IsNullOrWhiteSpace(frameworkPayload))
			{
				return;
			}

			var framework = JsonSerializer.Deserialize<AudioFrameworkStateMessage>(frameworkPayload, MqttJsonSerializerOptions);
			if (framework is null)
			{
				return;
			}

			Dispatcher.UIThread.Post(() => ApplyAudioFrameworkState(framework));
			return;
		}

		if (string.Equals(message.Topic, InternetRadioMqttTopics.RoutingStateTopic, StringComparison.OrdinalIgnoreCase))
		{
			var routingPayload = message.ConvertPayloadToString();
			if (string.IsNullOrWhiteSpace(routingPayload))
			{
				return;
			}

			var routing = JsonSerializer.Deserialize<RoutingStateMessage>(routingPayload, MqttJsonSerializerOptions);
			if (routing is null)
			{
				return;
			}

			Dispatcher.UIThread.Post(() => ApplyRoutingState(routing));
			return;
		}

		if (string.Equals(message.Topic, InternetRadioMqttTopics.RadioRuntimeStateTopic, StringComparison.OrdinalIgnoreCase))
		{
			var radioRuntimePayload = message.ConvertPayloadToString();
			if (string.IsNullOrWhiteSpace(radioRuntimePayload))
			{
				return;
			}

			var radioRuntime = JsonSerializer.Deserialize<RadioRuntimeStateMessage>(radioRuntimePayload, MqttJsonSerializerOptions);
			if (radioRuntime is null)
			{
				return;
			}

			Dispatcher.UIThread.Post(() => ApplyRadioRuntimeState(radioRuntime));
			return;
		}

		if (string.Equals(message.Topic, AudioProcessorStatusTopic, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(message.Topic, GpioControllerStatusTopic, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(message.Topic, SirenInterfaceStatusTopic, StringComparison.OrdinalIgnoreCase))
		{
			var statusPayload = message.ConvertPayloadToString();
			Dispatcher.UIThread.Post(() => ApplyComponentStatusMessage(message.Topic, statusPayload));
			return;
		}

		if (!string.Equals(message.Topic, InternetRadioMqttTopics.StateTopic, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		var internetRadioPayload = message.ConvertPayloadToString();
		if (string.IsNullOrWhiteSpace(internetRadioPayload))
		{
			return;
		}

		var state = JsonSerializer.Deserialize<InternetRadioPlaybackStateMessage>(internetRadioPayload, MqttJsonSerializerOptions);
		if (state is null)
		{
			return;
		}

		Dispatcher.UIThread.Post(() => ApplyInternetRadioPlaybackState(state));
	}

	private bool HandleSpecMqttMessage(MqttApplicationMessage message)
	{
		if (message.Topic.EndsWith("/ack", StringComparison.OrdinalIgnoreCase))
		{
			var ackPayload = message.ConvertPayloadToString();
			if (string.IsNullOrWhiteSpace(ackPayload))
			{
				return true;
			}

			var ack = JsonSerializer.Deserialize<CommandAckMessage>(ackPayload, MqttJsonSerializerOptions);
			if (ack is not null)
			{
				Dispatcher.UIThread.Post(() => ApplyCommandAck(message.Topic, ack));
			}

			return true;
		}

		if (string.Equals(message.Topic, InternetRadioMqttTopics.ConsoleTxTopic, StringComparison.OrdinalIgnoreCase))
		{
			var payload = message.ConvertPayloadToString();
			if (string.IsNullOrWhiteSpace(payload))
			{
				return true;
			}

			var consoleTx = JsonSerializer.Deserialize<ConsoleTxStateMessage>(payload, MqttJsonSerializerOptions);
			if (consoleTx is not null)
			{
				Dispatcher.UIThread.Post(() => ApplyConsoleTxState(consoleTx));
			}

			return true;
		}

		if (!message.Topic.StartsWith("myforce/module/", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var topicParts = message.Topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (topicParts.Length != 4)
		{
			return false;
		}

		var payloadText = message.ConvertPayloadToString();
		if (string.IsNullOrWhiteSpace(payloadText))
		{
			// A cleared (empty) retained status means the module went away (§4.4). A fixed controller stays
			// on the page (marked offline); a configured radio's row is removed entirely.
			if (string.Equals(topicParts[3], "status", StringComparison.OrdinalIgnoreCase))
			{
				var clearedComponentId = MapModuleIdToComponentId(topicParts[2]);
				Dispatcher.UIThread.Post(() =>
				{
					if (InfrastructureComponentIds.Contains(clearedComponentId))
					{
						UpdateSystemComponentStatus(clearedComponentId, AdminComponentStatus.Offline, "Controller cleared its status (offline).", message.Topic);
					}
					else
					{
						RemoveSystemComponentStatus(clearedComponentId);
					}
				});
			}

			return true;
		}

		var topicClass = topicParts[3];
		if (string.Equals(topicClass, "registry", StringComparison.OrdinalIgnoreCase))
		{
			var registry = JsonSerializer.Deserialize<ModuleRegistrySpecMessage>(payloadText, MqttJsonSerializerOptions);
			if (registry is not null)
			{
				Dispatcher.UIThread.Post(() => ApplyModuleRegistrySpec(registry));
			}

			return true;
		}

		if (string.Equals(topicClass, "status", StringComparison.OrdinalIgnoreCase))
		{
			var status = JsonSerializer.Deserialize<ModuleStatusSpecMessage>(payloadText, MqttJsonSerializerOptions);
			if (status is not null)
			{
				Dispatcher.UIThread.Post(() => ApplyModuleStatusSpec(message.Topic, status));
			}

			return true;
		}

		if (string.Equals(topicClass, "state", StringComparison.OrdinalIgnoreCase))
		{
			var state = JsonSerializer.Deserialize<ModuleRadioStateSpecMessage>(payloadText, MqttJsonSerializerOptions);
			if (state is not null)
			{
				Dispatcher.UIThread.Post(() => ApplyModuleRadioStateSpec(state));
			}

			return true;
		}

		// Keep the current instance config so the schema editor can prefill field values (§3.9.5).
		if (string.Equals(topicClass, "config", StringComparison.OrdinalIgnoreCase))
		{
			var moduleId = topicParts[2];
			using var document = JsonDocument.Parse(payloadText);
			if (document.RootElement.TryGetProperty("config", out var configElement))
			{
				var clone = configElement.Clone();
				Dispatcher.UIThread.Post(() => _radioConfigs[moduleId] = clone);
			}

			return true;
		}

		// Retained per-radio channel list feeding the CHANNELS picker (§5.3/§3.11).
		if (string.Equals(topicClass, "channels", StringComparison.OrdinalIgnoreCase))
		{
			var channels = JsonSerializer.Deserialize<ModuleChannelsMessage>(payloadText, MqttJsonSerializerOptions);
			if (channels is not null)
			{
				Dispatcher.UIThread.Post(() => ApplyModuleChannels(channels));
			}

			return true;
		}

		return true;
	}

	private void ApplyConsoleTxState(ConsoleTxStateMessage state)
	{
		ArgumentNullException.ThrowIfNull(state);
		CurSelChExt4 = string.Equals(state.State, "active", StringComparison.OrdinalIgnoreCase)
			? $"TX HELD BY {state.Holder ?? "UNKNOWN"}: {state.Target ?? "UNKNOWN"}"
			: "TX IDLE";
	}

	private void ApplyCommandAck(string topic, CommandAckMessage ack)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(topic);
		ArgumentNullException.ThrowIfNull(ack);

		_pendingMqttCommands.Remove(ack.MsgId, out var pendingCommand);
		var commandLabel = pendingCommand?.Topic ?? topic[..^4];
		if (string.Equals(ack.Status, "ok", StringComparison.OrdinalIgnoreCase))
		{
			MqttCommandFeedback = $"Command accepted: {commandLabel}";
			return;
		}

		var error = ack.Errors?.FirstOrDefault();
		MqttCommandFeedback = error is null
			? $"Command {ack.Status}: {commandLabel}"
			: $"Command {ack.Status}: {commandLabel} - {error.Message}";
	}

	private void ApplyModuleRegistrySpec(ModuleRegistrySpecMessage registry)
	{
		ArgumentNullException.ThrowIfNull(registry);

		// Only radio modules carry a config_schema the radio editor renders. Non-radio controllers (siren,
		// gpio) also publish module/<id>/registry but without a config_schema; processing them here would
		// pollute the radio admin and crash on Clone() of an undefined schema. Skip them.
		if (!string.Equals(registry.Category, "radio", StringComparison.OrdinalIgnoreCase)
			|| registry.ConfigSchema.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		// Keep the instance config schema so the schema-driven editor can render this radio (§3.9.5).
		// The addable-type list is fed from sys/plugins (ApplySystemPlugins); a per-instance registry only
		// supplies the live instance's schema for its EDIT CONFIG editor, not a new addable type.
		_radioSchemas[registry.Id] = registry.ConfigSchema.Clone();
		ApplyAdminSchemaProjection(registry);
		RaiseAdminNetworkStateChanged();
	}

	private void ApplyAdminSchemaProjection(ModuleRegistrySpecMessage registry)
	{
		var fieldPaths = ExtractSchemaFieldPaths(registry.ConfigSchema).ToArray();
		var existing = AdminSchemaModules.Where(module => !string.Equals(module.Id, registry.Id, StringComparison.OrdinalIgnoreCase));
		AdminSchemaModules = existing.Append(new AdminSchemaModuleProjection(
			registry.Id,
			registry.TypeId,
			registry.Kind,
			registry.Category,
			fieldPaths)).OrderBy(static module => module.Id, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private static IEnumerable<string> ExtractSchemaFieldPaths(JsonElement schema)
	{
		if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties))
		{
			yield break;
		}

		foreach (var property in properties.EnumerateObject())
		{
			foreach (var path in ExtractSchemaFieldPaths(property.Value, property.Name))
			{
				yield return path;
			}
		}
	}

	private static IEnumerable<string> ExtractSchemaFieldPaths(JsonElement schema, string prefix)
	{
		yield return prefix;
		if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties))
		{
			yield break;
		}

		foreach (var property in properties.EnumerateObject())
		{
			foreach (var path in ExtractSchemaFieldPaths(property.Value, $"{prefix}.{property.Name}"))
			{
				yield return path;
			}
		}
	}

	private void ApplyModuleStatusSpec(string topic, ModuleStatusSpecMessage status)
	{
		ArgumentNullException.ThrowIfNull(status);
		var componentStatus = status.Online && string.Equals(status.Health, "available", StringComparison.OrdinalIgnoreCase)
			? AdminComponentStatus.Online
			: AdminComponentStatus.Offline;
		var detail = status.Online
			? $"Health: {status.Health}"
			: $"Offline: {status.Reason ?? "offline"}";
		// Append the controller firmware revision when the device reports it (§5.8.4).
		if (!string.IsNullOrWhiteSpace(status.Fw))
		{
			detail = $"{detail}  FW: {status.Fw}";
		}

		// The ESP32 firmwares publish §5.2 status under their module id (e.g. "siren1",
		// "gpio.relay1"); map those onto the pre-registered System Status rows so they
		// update the existing component instead of adding a duplicate row.
		UpdateSystemComponentStatus(MapModuleIdToComponentId(status.Id), componentStatus, detail, topic);
	}

	// Maps a §5.2 module id onto the System Status component id it should drive.
	private static string MapModuleIdToComponentId(string moduleId)
	{
		return moduleId switch
		{
			"siren1" => "siren-interface",
			"gpio.relay1" => "gpio-controller",
			_ => moduleId,
		};
	}

	private void ApplyModuleRadioStateSpec(ModuleRadioStateSpecMessage state)
	{
		ArgumentNullException.ThrowIfNull(state);

		// The RADIO page channel only reflects the radio it is currently viewing, not whichever radio
		// happened to report state (the talk radio is operator-designated via the TALK button, not the
		// last reporter).
		var isSelectedRadio = string.Equals(state.Id, _selectedRadioId, StringComparison.OrdinalIgnoreCase);
		if (isSelectedRadio && state.Channel?.Label is not null)
		{
			CurrentRadioChannel = state.Channel.Label;
		}

		CurSelChExt1 = state.RxActive ? "RX ACTIVE" : "RX IDLE";
		CurSelChExt2 = state.TxActive ? $"TX ACTIVE / {state.TxSource ?? "manual"}" : "TX IDLE";
		CurSelChExt3 = state.Signal?.RssiDbm is null ? string.Empty : $"RSSI {state.Signal.RssiDbm} dBm";

		// RADIO page: drive the TX/RX indicator colors and the VU meter.
		IsRadioTxActive = state.TxActive;
		IsRadioRxActive = state.RxActive;
		if (state.Channel?.Label is not null)
		{
			_radioChannelLabels[state.Id] = state.Channel.Label;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRadioChannelLabel)));
			// A new channel label feeds the patrol list, the picker, and the talk-radio display.
			RebuildPatrolRadioChannelList();
			RebuildRadioSelectionItems();
			RefreshTalkRadioDisplay();
		}

		// No live audio-level feed yet, so map RSSI (~ -120..-40 dBm) onto the 0-100 VU
		// meter as a stand-in. TODO: replace with a real audio/VU level from the AP.
		if (state.Signal?.RssiDbm is int rssiDbm)
		{
			RadioVuLevel = Math.Clamp((rssiDbm + 120) * 100.0 / 80.0, 0, 100);
		}

		// Live function-button state (active/enabled/label) for the selected radio (§3.10.1).
		if (state.Buttons is not null && string.Equals(state.Id, _selectedRadioId, StringComparison.OrdinalIgnoreCase))
		{
			ApplyFunctionButtonState(state.Buttons);
		}

		// Reflect the radio's actual scan state on the SCAN buttons (module-reported via IS).
		if (state.Scan is bool scanning && string.Equals(state.Id, _selectedRadioId, StringComparison.OrdinalIgnoreCase))
		{
			IsRadioScanActive = scanning;
		}
	}

	/// <summary>
	/// Applies the retained AP radio runtime metadata for future schema-driven admin rendering.
	/// </summary>
	private void ApplyRadioRuntimeState(RadioRuntimeStateMessage state)
	{
		ArgumentNullException.ThrowIfNull(state);
		RadioRuntimeEntries = state.Radios ?? Array.Empty<RadioRuntimeEntryMessage>();
		RebuildRadioFunctionButtons();   // a registry update may add/replace declared buttons
		RefreshDeclaredRadioState();     // default selection/talk radio + patrol lists track the declared set
		RaiseAdminNetworkStateChanged();
	}

	// Re-derive everything that depends on the declared radio set: the default radio-1 selection and talk
	// radio (so reboots land on radio 1), the patrol "RADIO n" channel list (NOT USED for empty slots), and
	// the RADIO-page picker. Called whenever the declared radios or their channels change.
	private void RefreshDeclaredRadioState()
	{
		EnsureDefaultRadioSelection();
		RebuildPatrolRadioChannelList();
		RebuildRadioSelectionItems();
		RefreshTalkRadioDisplay();
		// These must run AFTER EnsureDefaultRadioSelection sets the default radio: on boot the runtime list
		// arrives with no radio selected yet, so the function-button panel and CHANNELS picker would
		// otherwise be built for an empty selection and stay blank until the operator re-selects.
		RebuildRadioFunctionButtons();
		RebuildChannelSelectionItems();
	}

	// On startup (and whenever the current selection/talk radio is no longer declared) fall back to the
	// first declared radio, so the RADIO page and TALK RADIO both default to "radio 1" after a reboot.
	private void EnsureDefaultRadioSelection()
	{
		var firstRadio = RadioRuntimeEntries.FirstOrDefault();
		if (firstRadio is null)
		{
			// No declared radios: clear the selection and talk radio so nothing shows filler data.
			_selectedRadioId = string.Empty;
			_selectedRadioDisplayName = "---";
			_talkRadioId = string.Empty;
			CurrentRadioChannel = string.Empty;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioPageTitle)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChannelSelectionRadioTitle)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRadioScanLabel)));
			RaiseSelectedRadioTalkListenChanged();
			return;
		}

		if (string.IsNullOrWhiteSpace(_selectedRadioId)
			|| !RadioRuntimeEntries.Any(radio => string.Equals(radio.RadioId, _selectedRadioId, StringComparison.OrdinalIgnoreCase)))
		{
			_selectedRadioId = firstRadio.RadioId;
			_selectedRadioDisplayName = string.IsNullOrWhiteSpace(firstRadio.DisplayName) ? firstRadio.RadioId : firstRadio.DisplayName;
			CurrentRadioChannel = ResolveRadioChannel(firstRadio.RadioId, firstRadio.TypeId);
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioPageTitle)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChannelSelectionRadioTitle)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRadioScanLabel)));
		}

		// The talk radio defaults to radio 1 and only changes via the TALK button; reset if it vanished.
		if (string.IsNullOrWhiteSpace(_talkRadioId)
			|| !RadioRuntimeEntries.Any(radio => string.Equals(radio.RadioId, _talkRadioId, StringComparison.OrdinalIgnoreCase)))
		{
			_talkRadioId = firstRadio.RadioId;
		}

		RaiseSelectedRadioTalkListenChanged();
	}

	// Fill the six positional patrol radio slots from the declared radios; slots without a declared radio
	// read "NOT USED" (no filler data).
	private void RebuildPatrolRadioChannelList()
	{
		Radio1ChannelName = PatrolRadioSlotLabel(0);
		Radio2ChannelName = PatrolRadioSlotLabel(1);
		Radio3ChannelName = PatrolRadioSlotLabel(2);
		Radio4ChannelName = PatrolRadioSlotLabel(3);
		Radio5ChannelName = PatrolRadioSlotLabel(4);
		Radio6ChannelName = PatrolRadioSlotLabel(5);
	}

	// The label for one positional patrol radio slot: the declared radio's current channel, or NOT USED.
	private string PatrolRadioSlotLabel(int index)
	{
		if (index >= RadioRuntimeEntries.Count)
		{
			return RadioSlotNotUsed;
		}

		var radio = RadioRuntimeEntries[index];
		return ResolveRadioChannel(radio.RadioId, radio.TypeId);
	}

	// Resolve a radio's current channel: the live label if one has been reported, else the 4W resource's
	// static "channel1", else a placeholder until the radio reports.
	private string ResolveRadioChannel(string radioId, string typeId)
	{
		if (_radioChannelLabels.TryGetValue(radioId, out var label) && !string.IsNullOrWhiteSpace(label))
		{
			return label;
		}

		return string.Equals(typeId, FourWireResourceTypeId, StringComparison.OrdinalIgnoreCase)
			? FourWireStaticChannel
			: "---";
	}

	// "TALK RADIO: <alias> <channel>" for the designated talk radio (or "---" when none is declared).
	private void RefreshTalkRadioDisplay()
	{
		var talkRadio = RadioRuntimeEntries.FirstOrDefault(radio => string.Equals(radio.RadioId, _talkRadioId, StringComparison.OrdinalIgnoreCase));
		if (talkRadio is null)
		{
			CurrentTalkRadio = "---";
			return;
		}

		var alias = string.IsNullOrWhiteSpace(talkRadio.DisplayName) ? talkRadio.RadioId : talkRadio.DisplayName;
		CurrentTalkRadio = $"{alias} {ResolveRadioChannel(talkRadio.RadioId, talkRadio.TypeId)}";
	}

	/// <summary>
	/// Sections 3.9, 4.1, and 4.3: applies retained AP type availability so the Radio admin page can offer built-in resources and RM-backed radio types before instances exist.
	/// </summary>
	private void ApplyAudioProcessorRegistry(AudioProcessorRegistryMessage registry)
	{
		ArgumentNullException.ThrowIfNull(registry);
		// The addable-type list now comes from sys/plugins (ApplySystemPlugins); the service registry only
		// describes the currently declared instances, which are not the same as the types you can add.
		RaiseAdminNetworkStateChanged();
	}

	/// <summary>
	/// Sections 4.1/4.3/4.4: applies the retained sys/plugins payload as the addable radio type list so
	/// every discovered RM plugin and built-in AP resource can be added, even before any instance exists.
	/// </summary>
	private void ApplySystemPlugins(SystemPluginsMessage plugins)
	{
		ArgumentNullException.ThrowIfNull(plugins);
		var emptyCapabilities = new RadioCapabilitiesMessage(
			Array.Empty<string>(),
			Array.Empty<string>(),
			ProvidesAudio: false,
			Array.Empty<string>());

		AvailableRadioTypes = (plugins.Types ?? Array.Empty<SystemPluginTypeMessage>())
			.Select(type => new RadioRegistryEntryMessage(
				RadioId: type.TypeId,
				TypeId: type.TypeId,
				DisplayName: string.IsNullOrWhiteSpace(type.DisplayName) ? type.TypeId : type.DisplayName,
				// Normalize the AP plugin kinds onto the UI's Module/Resource vocabulary so the summary
				// (BUILT-IN RESOURCES count) and the per-card badge stay meaningful.
				Kind: string.Equals(type.Kind, "radio_resource", StringComparison.OrdinalIgnoreCase) ? "Resource" : "Module",
				Capabilities: emptyCapabilities,
				ConfigSchema: string.Empty,
				InstanceSchema: string.Empty))
			.ToArray();
		RaiseAdminNetworkStateChanged();
	}

	private void ApplyMqttState(MqttConnectionState state)
	{
		MqttStatus = state.Status;
		MqttEndpoint = string.IsNullOrWhiteSpace(state.Endpoint) ? "127.0.0.1:1883" : state.Endpoint;
		MqttDetail = state.Detail;
		IsMqttConnectionBannerVisible = !state.IsConnected;
		MqttConnectionBannerText = state.IsConnected
			? "MQTT broker connected."
			: $"MQTT broker offline: {state.Detail}";

		if (state.IsConnected)
		{
			UpdateSystemComponentStatus("ui", AdminComponentStatus.Online, "Local console is connected to the MQTT broker.", "LOCAL");
			RestoreEntertainmentModePlayback();
			return;
		}

		UpdateSystemComponentStatus("ui", AdminComponentStatus.Online, "Local console is running without broker connectivity.", "LOCAL");
	}

	/// <summary>
	/// Applies retained service health messages so the System status page can reflect live component availability.
	/// </summary>
	private void ApplyComponentStatusMessage(string topic, string? payload)
	{
		if (string.IsNullOrWhiteSpace(topic))
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(payload))
		{
			// An empty retained payload means the component cleared its status (gone). Reflect that only if
			// we already knew about it; never create a phantom row for a component we never detected.
			var componentId = GetComponentIdFromTopic(topic);
			if (SystemComponentStatuses.Any(component => string.Equals(component.Id, componentId, StringComparison.OrdinalIgnoreCase)))
			{
				UpdateSystemComponentStatus(componentId, AdminComponentStatus.Offline, "Component cleared its status (offline).", topic);
			}

			return;
		}

		try
		{
			using var json = JsonDocument.Parse(payload);
			var root = json.RootElement;
			var serviceId = root.TryGetProperty("serviceId", out var serviceIdElement)
				? serviceIdElement.GetString()
				: GetComponentIdFromTopic(topic);
			var stateLabel = root.TryGetProperty("state", out var stateElement)
				? GetServiceStateLabel(stateElement)
				: string.Empty;
			var detail = root.TryGetProperty("detail", out var detailElement)
				? detailElement.GetString()
				: null;

			var status = string.Equals(stateLabel, "Running", StringComparison.OrdinalIgnoreCase)
				? AdminComponentStatus.Online
				: string.Equals(stateLabel, "Stopped", StringComparison.OrdinalIgnoreCase)
					? AdminComponentStatus.Offline
					: AdminComponentStatus.Unknown;

			UpdateSystemComponentStatus(
				string.IsNullOrWhiteSpace(serviceId) ? GetComponentIdFromTopic(topic) : serviceId,
				status,
				string.IsNullOrWhiteSpace(detail) ? $"State: {stateLabel}" : detail,
				topic);
		}
		catch (JsonException)
		{
			UpdateSystemComponentStatus(GetComponentIdFromTopic(topic), AdminComponentStatus.Unknown, "Status payload was not valid JSON.", topic);
		}
	}

	/// <summary>
	/// Applies retained AP internet radio playback state to the local UI without publishing commands back.
	/// </summary>
	private void ApplyInternetRadioPlaybackState(InternetRadioPlaybackStateMessage state)
	{
		ArgumentNullException.ThrowIfNull(state);

		if (string.IsNullOrWhiteSpace(state.StreamUrl))
		{
			return;
		}

		var station = _internetRadioStations.FirstOrDefault(candidate =>
			string.Equals(candidate.StreamUrl, state.StreamUrl, StringComparison.OrdinalIgnoreCase));

		if (station is null)
		{
			return;
		}

		_selectedInternetStation = station;
		EnsureInternetStationVisible(FilteredInternetRadioStations.ToList().FindIndex(candidate =>
			string.Equals(candidate.StreamUrl, station.StreamUrl, StringComparison.OrdinalIgnoreCase)));
		RaiseInternetChannelListStateChanged();
		RaiseAmFmStateChanged();
	}

	private void ApplyAudioFrameworkState(AudioFrameworkStateMessage framework)
	{
		ArgumentNullException.ThrowIfNull(framework);

		AudioOutputDevices = framework.Devices
			.Where(device => device.OutputEnabled && string.Equals(device.Role, "speaker", StringComparison.OrdinalIgnoreCase))
			.OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (AudioOutputDevices.Count == 0)
		{
			AdminAudioStatus = "AP did not report any speaker outputs.";
			RaiseAdminAudioStateChanged();
			return;
		}

		if (!AudioOutputDevices.Any(device => string.Equals(device.DeviceId, SelectedAdminOutputSpeakerId, StringComparison.OrdinalIgnoreCase)))
		{
			SelectedAdminOutputSpeakerId = AudioOutputDevices[0].DeviceId;
		}

		if (!AudioOutputDevices.Any(device => string.Equals(device.DeviceId, ApConfiguredOutputSpeakerId, StringComparison.OrdinalIgnoreCase)))
		{
			ApConfiguredOutputSpeakerId = AudioOutputDevices[0].DeviceId;
		}

		AdminAudioStatus = $"Detected {AudioOutputDevices.Count} AP speaker output(s).";
		RaiseAdminAudioStateChanged();
	}

	private void ApplyRoutingState(RoutingStateMessage routing)
	{
		ArgumentNullException.ThrowIfNull(routing);

		var deviceId = string.IsNullOrWhiteSpace(routing.SpeakerDeviceId)
			? DefaultAudioOutputSpeakerId
			: routing.SpeakerDeviceId;

		ApConfiguredOutputSpeakerId = deviceId;
		if (!HasAudioOutputDevices || IsSelectedSpeakerInSync)
		{
			SelectedAdminOutputSpeakerId = deviceId;
		}

		AdminAudioStatus = $"AP master speaker routed to {AppliedAudioOutputSpeakerLabel}.";
		RaiseAdminAudioStateChanged();
	}

	/// <summary>
	/// Publishes the selected INT station to the AP so playback can begin on the audio processor.
	/// </summary>
	private async Task PublishInternetRadioPlayCommandAsync(InternetRadioStation station)
	{
		ArgumentNullException.ThrowIfNull(station);

		var command = CreateInternetRadioPlayCommandMessage(station);
		await PublishCommandAsync(InternetRadioMqttTopics.SpecPlayCommandTopic, command).ConfigureAwait(false);
	}

	/// <summary>
	/// Publishes a stop command to the AP so internet radio playback halts immediately.
	/// </summary>
	private async Task PublishInternetRadioStopCommandAsync()
	{
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: true);
		await PublishCommandAsync(InternetRadioMqttTopics.SpecStopCommandTopic, envelope).ConfigureAwait(false);
	}

	/// <summary>
	/// Publishes the local entertainment volume to the AP mixer so AM/FM and INT playback follow the UI volume controls.
	/// </summary>
	private void PublishEntertainmentVolumeCommand()
	{
		var command = CreateAudioChannelGainCommandMessage(
			AudioProcessorEntertainmentChannelId,
			MapEntertainmentVolumeToGain(_amFmVolume));
		_ = PublishCommandAsync(AudioProcessorChannelGainCommandTopic, command);
	}

	/// <summary>Publishes the master output volume (the master sink level) to the AP.</summary>
	private void PublishMasterVolumeCommand()
	{
		var command = CreateAudioChannelGainCommandMessage("master", MapEntertainmentVolumeToGain(_masterVolume));
		_ = PublishCommandAsync(InternetRadioMqttTopics.SpecMasterVolumeCommandTopic, command);
	}

	private async Task PublishOutputSpeakerCommandAsync(string deviceId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

		var command = CreateOutputSpeakerCommandMessage(deviceId, isAdminCommand: true);
		await PublishCommandAsync(InternetRadioMqttTopics.SpecSpeakerOutputCommandTopic, command).ConfigureAwait(false);
	}

	/// <summary>
	/// Sections 3.9.3 and 5.8.1: creates the flat common MQTT envelope used by UI command payloads.
	/// </summary>
	private MqttCommandEnvelope CreateCommandEnvelope(bool isAdminCommand, bool includeMessageId)
	{
		var auth = isAdminCommand ? RequireAdminSessionCredential() : null;
		return new MqttCommandEnvelope(
			MqttCommandSchemaVersion,
			DateTimeOffset.UtcNow,
			includeMessageId ? Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture) : null,
			auth);
	}

	/// <summary>
	/// Sections 3.9 and 3.9.3: the UI writes to the system only by publishing MQTT commands, never by mutating authoritative state directly.
	/// </summary>
	private Task PublishCommandAsync<TCommand>(string topic, TCommand command, bool retain = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(topic);
		ArgumentNullException.ThrowIfNull(command);

		TrackPendingCommand(topic, command);
		var payload = JsonSerializer.Serialize(command, MqttJsonSerializerOptions);
		return _mqttConnectionService.PublishAsync(topic, payload, retain);
	}

	private void TrackPendingCommand<TCommand>(string topic, TCommand command)
	{
		var msgId = command switch
		{
			MqttCommandEnvelope envelope => envelope.MsgId,
			InternetRadioPlayCommandMessage play => play.MsgId,
			AudioChannelGainCommandMessage gain => gain.MsgId,
			OutputSpeakerCommandMessage speaker => speaker.MsgId,
			_ => null
		};

		if (string.IsNullOrWhiteSpace(msgId))
		{
			return;
		}

		_pendingMqttCommands[msgId] = new PendingMqttCommand(topic, DateTimeOffset.UtcNow);
		MqttCommandFeedback = $"Command pending: {topic}";
	}

	private InternetRadioPlayCommandMessage CreateInternetRadioPlayCommandMessage(InternetRadioStation station)
	{
		ArgumentNullException.ThrowIfNull(station);
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: true);
		return new InternetRadioPlayCommandMessage(
			envelope.V,
			envelope.Ts,
			envelope.MsgId,
			envelope.Auth,
			station.StreamUrl,
			station.DisplayName,
			station.Genre,
			station.Language);
	}

	private AudioChannelGainCommandMessage CreateAudioChannelGainCommandMessage(string channelId, decimal gain)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: true);
		return new AudioChannelGainCommandMessage(
			envelope.V,
			envelope.Ts,
			envelope.MsgId,
			envelope.Auth,
			channelId,
			gain);
	}

	private OutputSpeakerCommandMessage CreateOutputSpeakerCommandMessage(string deviceId, bool isAdminCommand)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
		var envelope = CreateCommandEnvelope(isAdminCommand, includeMessageId: true);
		return new OutputSpeakerCommandMessage(
			envelope.V,
			envelope.Ts,
			envelope.MsgId,
			envelope.Auth,
			deviceId);
	}

	private string RequireAdminSessionCredential()
	{
		if (!IsAdminAuthenticated || string.IsNullOrWhiteSpace(_adminSessionCredential))
		{
			throw new InvalidOperationException("Admin authentication is required for this command.");
		}

		return _adminSessionCredential;
	}

	/// <summary>
	/// Restores the saved entertainment playback state after MQTT becomes available.
	/// </summary>
	private void RestoreEntertainmentModePlayback()
	{
		PublishEntertainmentVolumeCommand();
		SyncInternetRadioPlaybackAsync();
	}

	/// <summary>
	/// Keeps AP internet radio playback synchronized with the current UI source, mute state, and station selection.
	/// </summary>
	private void SyncInternetRadioPlaybackAsync()
	{
		if (!IsInternetSourceSelected || _isAmFmMuted)
		{
			_ = PublishInternetRadioStopCommandAsync();
			return;
		}

		if (_selectedInternetStation is null)
		{
			return;
		}

		_ = PublishInternetRadioPlayCommandAsync(_selectedInternetStation);
	}

	private void UpdateClock()
	{
		var now = DateTime.Now;
		Clock = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
		Date = now.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
	}

	private void RaiseTabStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPatrolTabSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLightsAndSirensTabSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRadioTabSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRadarTabSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAmFmTabSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCadTabSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCameraTabSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPatrolContentVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLightsAndSirensContentVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRadioContentVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRadarContentVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAmFmContentVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCadContentVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCameraContentVisible)));
	}

	private void RaiseAdminOverlayStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminOverlayVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminAuthenticated)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminPinPromptVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminContentVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminPinMaskedEntry)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminPinStatus)));
	}

	private void RaiseAdminSystemStatusChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemComponentStatuses)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminSystemStatusSummary)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MqttCommandFeedback)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminSystemGeneralSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminSystemStatusSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminAudioSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminRadioSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminNetworkSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminIntegrationsSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminIntegrationsWhat3WordsSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminNonSystemSectionSelected)));
	}

	private void RaiseAdminAudioStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioOutputDevices)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ApConfiguredOutputSpeakerId)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAdminOutputSpeakerId)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminAudioStatus)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminAudioDeviceSummary)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAudioOutputDevices)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedSpeakerInSync)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioOutputSpeakerLabel)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ApConfiguredOutputSpeakerLabel)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppliedAudioOutputSpeakerLabel)));
	}

	private void RaiseAdminNetworkStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableRadioTypes)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAvailableRadioTypes)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminAvailableRadioTypeSummary)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioRuntimeEntries)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminRadioEntryCount)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAdminRadioEntries)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminRadioSummary)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminSchemaModules)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminSchemaSummary)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminRadioSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminNetworkSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminIntegrationsSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminIntegrationsWhat3WordsSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminNonSystemSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminWhat3WordsApiKey)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdminWhat3WordsStatus)));
	}

	private void RaiseDirectionalStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirectionalLeftSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirectionalCenterOutSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirectionalRightSelected)));
	}

	// Publishes the selected directional position to the Siren Interface Controller.
	// The firmware maps "center" to both directional relays energised at once (§ siren
	// wiring spec). Fire-and-forget operating command (no admin auth, §4.6).
	private void PublishSirenDirectionalCommand(DirectionalMode directional)
	{
		var direction = directional switch
		{
			DirectionalMode.Off => "off",
			DirectionalMode.Left => "left",
			DirectionalMode.CenterOut => "center",
			DirectionalMode.Right => "right",
			_ => "off",
		};

		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: false);
		var command = new SirenDirectionalCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, direction);
		_ = PublishCommandAsync(InternetRadioMqttTopics.SirenDirectionalCommandTopic, command);
	}

	// Publishes the selected siren code to the Siren Interface Controller. The
	// firmware interlocks the Code1/2/3 group (selecting one clears the others).
	// Fire-and-forget operating command (no admin auth, §4.6).
	private void PublishSirenCodeCommand(AlertCodeMode alertCode)
	{
		var code = alertCode switch
		{
			AlertCodeMode.Off => "off",
			AlertCodeMode.Code1 => "code1",
			AlertCodeMode.Code2 => "code2",
			AlertCodeMode.Code3 => "code3",
			_ => "off",
		};

		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: false);
		var command = new SirenCodeCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, code);
		_ = PublishCommandAsync(InternetRadioMqttTopics.SirenCodeCommandTopic, command);
	}

	// --- L/S page: scene lights (toggle) + air horn (momentary) -----------------
	// Each drives a Siren Interface Controller relay via cmd/set { function, state }.

	// Scene-light status colors: the SCENE "LA / TD / RA" abbreviations go green while
	// their light is active (§ L/S status panel).
	private static readonly IBrush SceneActiveBrush = new SolidColorBrush(Color.Parse("#1CFF1C"));

	private bool _isLeftAlleyActive;
	public bool IsLeftAlleyActive
	{
		get => _isLeftAlleyActive;
		private set
		{
			if (SetProperty(ref _isLeftAlleyActive, value))
			{
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LeftAlleyStatusBrush)));
			}
		}
	}

	private bool _isTakeDownActive;
	public bool IsTakeDownActive
	{
		get => _isTakeDownActive;
		private set
		{
			if (SetProperty(ref _isTakeDownActive, value))
			{
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TakeDownStatusBrush)));
			}
		}
	}

	private bool _isRightAlleyActive;
	public bool IsRightAlleyActive
	{
		get => _isRightAlleyActive;
		private set
		{
			if (SetProperty(ref _isRightAlleyActive, value))
			{
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RightAlleyStatusBrush)));
			}
		}
	}

	// Green when the matching scene light is active, white otherwise.
	public IBrush LeftAlleyStatusBrush => IsLeftAlleyActive ? SceneActiveBrush : Brushes.White;

	public IBrush TakeDownStatusBrush => IsTakeDownActive ? SceneActiveBrush : Brushes.White;

	public IBrush RightAlleyStatusBrush => IsRightAlleyActive ? SceneActiveBrush : Brushes.White;

	private bool _isAirHornActive;
	public bool IsAirHornActive
	{
		get => _isAirHornActive;
		private set => SetProperty(ref _isAirHornActive, value);
	}

	// Scene-light toggles: flip the latched state and drive the matching siren relay.
	public void ToggleLeftAlley()
	{
		IsLeftAlleyActive = !IsLeftAlleyActive;
		PublishSirenSet("alley_left", IsLeftAlleyActive);
	}

	public void ToggleTakeDown()
	{
		IsTakeDownActive = !IsTakeDownActive;
		PublishSirenSet("takedown", IsTakeDownActive);
	}

	public void ToggleRightAlley()
	{
		IsRightAlleyActive = !IsRightAlleyActive;
		PublishSirenSet("alley_right", IsRightAlleyActive);
	}

	// Air horn is momentary: sound while the button is held (press = on, release = off).
	public void AirHornDown()
	{
		if (IsAirHornActive)
		{
			return;
		}

		IsAirHornActive = true;
		PublishSirenSet("airhorn", true);
	}

	public void AirHornUp()
	{
		if (!IsAirHornActive)
		{
			return;
		}

		IsAirHornActive = false;
		PublishSirenSet("airhorn", false);
	}

	// Publishes an on/off command for a single siren relay (§5.2 cmd/set). Fire-and-forget
	// operating command (no admin auth, §4.6, §3.9.3).
	private void PublishSirenSet(string function, bool on)
	{
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: false);
		var command = new SirenSetCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, function, on ? "on" : "off");
		_ = PublishCommandAsync(InternetRadioMqttTopics.SirenSetCommandTopic, command);
	}

	// --- Siren/light active-function lease + watchdog refresh (§5.10.1) ----------
	// While any siren/light function is active, this console holds the lease: it refreshes
	// the controller (~1 s, inside the 8 s watchdog) so an unattended siren can never stay
	// on, and publishes a holder-heartbeat for multi-console handoff. When nothing is active
	// the lease is dropped. The L/S button kills everything (all_off + clears UI state).

	private bool _isSirenLeaseHolder;

	// True when any siren/light function is currently asserted by this console (momentary
	// operator intent, §5.10.1) - directional, code, scene lights, or air horn.
	public bool AnySirenLightActive =>
		SelectedDirectional != DirectionalMode.Off
		|| SelectedAlertCode != AlertCodeMode.Off
		|| IsLeftAlleyActive || IsTakeDownActive || IsRightAlleyActive || IsAirHornActive || IsExtAudioActive;

	// Drives the ~1 s lease tick (refresh + holder heartbeat while active).
	private void OnSirenLeaseTick(object? sender, EventArgs e)
	{
		if (AnySirenLightActive)
		{
			_isSirenLeaseHolder = true;            // activation / successor takeover
			PublishSirenRefresh();                 // reset the controller watchdog (§5.10.1)
			PublishSirenLeaseHeartbeat(active: true);
		}
		else if (_isSirenLeaseHolder)
		{
			// Nothing active anymore: drop the lease and stop refreshing (§5.10.1).
			_isSirenLeaseHolder = false;
			PublishSirenLeaseHeartbeat(active: false);
		}
	}

	// L/S button (soft key 1, touch or VIP/HCD): turn off ALL active siren/light functions.
	public void AllSirenLightsOff()
	{
		SelectedDirectional = DirectionalMode.Off;   // publishes directional off
		SelectedAlertCode = AlertCodeMode.Off;       // publishes code off

		// Each scene light / air horn must be explicitly de-energised on the controller, not just
		// cleared in the UI, so the relays actually drop (publish set:off per function).
		if (IsLeftAlleyActive) { IsLeftAlleyActive = false; PublishSirenSet("alley_left", false); }
		if (IsTakeDownActive) { IsTakeDownActive = false; PublishSirenSet("takedown", false); }
		if (IsRightAlleyActive) { IsRightAlleyActive = false; PublishSirenSet("alley_right", false); }
		if (IsAirHornActive) { IsAirHornActive = false; PublishSirenSet("airhorn", false); }
		if (IsExtAudioActive) { IsExtAudioActive = false; PublishSirenSet("ext_audio", false); }

		// Backstop: also tell the controller to kill every relay, then drop the lease.
		PublishSirenAllOff();
		if (_isSirenLeaseHolder)
		{
			_isSirenLeaseHolder = false;
			PublishSirenLeaseHeartbeat(active: false);
		}
	}

	// Lease keep-alive: re-asserts operator presence so the controller's watchdog never trips.
	private void PublishSirenRefresh()
	{
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: false);
		_ = PublishCommandAsync(InternetRadioMqttTopics.SirenRefreshCommandTopic, envelope);
	}

	// Authoritative all-off for the siren controller (kills every relay).
	private void PublishSirenAllOff()
	{
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: false);
		_ = PublishCommandAsync(InternetRadioMqttTopics.SirenAllOffCommandTopic, envelope);
	}

	// Retained holder heartbeat so other consoles know who holds the lease (§5.10.1, v2.5).
	private void PublishSirenLeaseHeartbeat(bool active)
	{
		var command = new SirenLeaseMessage(MqttCommandSchemaVersion, DateTimeOffset.UtcNow, active ? ConsoleId : null, active);
		_ = PublishCommandAsync(InternetRadioMqttTopics.SirenLeaseTopic, command, retain: true);
	}

	// --- Active-radio SCAN (patrol "ACTIVE RADIO / SCAN" + radio page SCAN) ------
	// Both buttons toggle scan for the currently selected radio and glow orange while scanning.

	private bool _isRadioScanActive;
	public bool IsRadioScanActive
	{
		get => _isRadioScanActive;
		private set => SetProperty(ref _isRadioScanActive, value);
	}

	// "ACTIVE RADIO" label shows which radio scan targets.
	public string ActiveRadioScanLabel => string.IsNullOrWhiteSpace(_selectedRadioDisplayName) || _selectedRadioDisplayName == "---"
		? "ACTIVE RADIO"
		: _selectedRadioDisplayName;

	// Toggle scan on the selected radio module (cmd/scan { state: start|stop }, §5.3).
	public void ToggleRadioScan()
	{
		if (string.IsNullOrWhiteSpace(_selectedRadioId))
		{
			return;
		}

		IsRadioScanActive = !IsRadioScanActive;
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: true);
		var command = new RadioScanCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, IsRadioScanActive ? "start" : "stop");
		_ = PublishCommandAsync($"myforce/module/{_selectedRadioId}/cmd/scan", command);
	}

	// --- EXT AUDIO (patrol soft key 2 + radio page) -> siren ext_audio relay -----
	// Toggles the siren controller's ext_audio relay and pulses green while active.

	private bool _isExtAudioActive;
	public bool IsExtAudioActive
	{
		get => _isExtAudioActive;
		private set => SetProperty(ref _isExtAudioActive, value);
	}

	public void ToggleExtAudio()
	{
		IsExtAudioActive = !IsExtAudioActive;
		PublishSirenSet("ext_audio", IsExtAudioActive);
	}

	// ===========================================================================
	// RADIO page: VU meter, TX/RX indicators, radio-selection + channel overlays.
	// ===========================================================================

	private const double RadioVuMeterHeight = 150;   // pixel height of the VU meter fill area

	private string _selectedRadioId = string.Empty;
	private string _selectedRadioDisplayName = "---";
	private double _radioVuLevel;
	private bool _isRadioTxActive;
	private bool _isRadioRxActive;
	private bool _isRadioSelectionOverlayVisible;
	private bool _isChannelSelectionOverlayVisible;
	private IReadOnlyList<RadioSelectionItem> _radioSelectionItems = Array.Empty<RadioSelectionItem>();
	private IReadOnlyList<ChannelSelectionItem> _channelSelectionItems = Array.Empty<ChannelSelectionItem>();
	private readonly Dictionary<string, string> _radioChannelLabels = new(StringComparer.OrdinalIgnoreCase);

	// Retained per-radio channel lists (myforce/module/<id>/channels, §3.11) feeding the CHANNELS picker.
	private readonly Dictionary<string, IReadOnlyList<ChannelEntryMessage>> _radioChannels = new(StringComparer.OrdinalIgnoreCase);

	// The channel center behind each of the 4 patrol PROXIMITY LIST slots, so a press can jump to that
	// radio + channel. Null when the slot is empty.
	private readonly ChannelCenter?[] _proximitySlots = new ChannelCenter?[4];

	// The single operator-designated talk radio (TALK button); future PTT triggers key this one.
	private string _talkRadioId = string.Empty;

	// Radios whose RX monitor is muted (LISTEN button toggles membership). Multiple radios can be listened
	// to at once, so this tracks the muted exceptions rather than a single selection.
	private readonly HashSet<string> _mutedRadioIds = new(StringComparer.OrdinalIgnoreCase);

	// 4-Wire radio resources have no live channel concept, so they show a single static channel. The AP
	// reports its label from the operator-editable "channel1_alias" setting (default "4W"); this is the
	// fallback shown before the AP's retained state has arrived.
	private const string FourWireResourceTypeId = "4w_resource";
	private const string FourWireStaticChannel = "4W";

	// Header title, e.g. "RADIO 1: XTL5000".
	public string RadioPageTitle => $"RADIO 1: {_selectedRadioDisplayName}";

	// "CH: <label>" for the selected radio's current channel.
	public string SelectedRadioChannelLabel => string.IsNullOrWhiteSpace(CurrentRadioChannel) ? "CH:" : $"CH: {CurrentRadioChannel}";

	// 0-100 level driving the VU meter, plus the derived pixel fill height the bar binds to.
	public double RadioVuLevel
	{
		get => _radioVuLevel;
		private set
		{
			if (SetProperty(ref _radioVuLevel, value))
			{
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioVuFillHeight)));
			}
		}
	}

	public double RadioVuFillHeight => Math.Clamp(RadioVuLevel, 0, 100) / 100.0 * RadioVuMeterHeight;

	// TX is red while transmitting, RX is blue while receiving; both dim grey when idle.
	public bool IsRadioTxActive
	{
		get => _isRadioTxActive;
		private set
		{
			if (SetProperty(ref _isRadioTxActive, value))
			{
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioTxBrush)));
			}
		}
	}

	public bool IsRadioRxActive
	{
		get => _isRadioRxActive;
		private set
		{
			if (SetProperty(ref _isRadioRxActive, value))
			{
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioRxBrush)));
			}
		}
	}

	public IBrush RadioTxBrush => IsRadioTxActive ? Brushes.Red : Brushes.Gray;

	public IBrush RadioRxBrush => IsRadioRxActive ? Brushes.DeepSkyBlue : Brushes.Gray;

	public bool IsRadioSelectionOverlayVisible
	{
		get => _isRadioSelectionOverlayVisible;
		private set => SetProperty(ref _isRadioSelectionOverlayVisible, value);
	}

	public bool IsChannelSelectionOverlayVisible
	{
		get => _isChannelSelectionOverlayVisible;
		private set => SetProperty(ref _isChannelSelectionOverlayVisible, value);
	}

	// The radios offered in the SELECT picker: alias + current channel + a select key,
	// built from the radios programmed in via the admin page (AP runtime list).
	public IReadOnlyList<RadioSelectionItem> RadioSelectionItems
	{
		get => _radioSelectionItems;
		private set => SetProperty(ref _radioSelectionItems, value);
	}

	public string ChannelSelectionRadioTitle => $"CHANNELS: {_selectedRadioDisplayName}";

	// SELECT button: open the radio picker built from the admin/runtime radio list.
	public void OpenRadioSelection()
	{
		RebuildRadioSelectionItems();
		IsRadioSelectionOverlayVisible = true;
	}

	public void CloseRadioSelection() => IsRadioSelectionOverlayVisible = false;

	// Choose a radio: remember it, tell the AP this console selected it (§5.4), and close.
	public void SelectRadioTarget(string radioId)
	{
		if (string.IsNullOrWhiteSpace(radioId))
		{
			return;
		}

		_selectedRadioId = radioId;
		var match = RadioRuntimeEntries.FirstOrDefault(radio => string.Equals(radio.RadioId, radioId, StringComparison.OrdinalIgnoreCase));
		_selectedRadioDisplayName = string.IsNullOrWhiteSpace(match?.DisplayName) ? radioId : match!.DisplayName;
		// Show the newly selected radio's current channel (4W resources fall back to their static channel1).
		CurrentRadioChannel = ResolveRadioChannel(radioId, match?.TypeId ?? string.Empty);
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioPageTitle)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChannelSelectionRadioTitle)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRadioScanLabel)));
		// The TALK/LISTEN buttons reflect the now-viewed radio's talk-radio / mute state.
		RaiseSelectedRadioTalkListenChanged();
		IsRadioScanActive = false;   // scan state is per-radio; reset on selection
		RebuildRadioFunctionButtons();
		PublishConsoleSelect(radioId);
		IsRadioSelectionOverlayVisible = false;
	}

	// TALK button (RADIO page): designate the currently-viewed radio as THE talk radio. Only one radio is
	// the talk radio at a time (the one future PTT triggers will key); selecting a new one replaces it.
	public void DesignateSelectedAsTalkRadio()
	{
		if (string.IsNullOrWhiteSpace(_selectedRadioId))
		{
			return;
		}

		_talkRadioId = _selectedRadioId;
		RefreshTalkRadioDisplay();
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedRadioTalkRadio)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioTalkBrush)));
	}

	// LISTEN button (RADIO page): toggle the RX monitor mute for the currently-viewed radio. Several radios
	// may be listened to at once, so this flips just this radio's muted state.
	public void ToggleSelectedRadioListen()
	{
		if (string.IsNullOrWhiteSpace(_selectedRadioId))
		{
			return;
		}

		if (!_mutedRadioIds.Remove(_selectedRadioId))
		{
			_mutedRadioIds.Add(_selectedRadioId);
		}

		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioListenButtonText)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioListenBrush)));
	}

	// True when the viewed radio is the designated talk radio (drives the TALK button highlight).
	public bool IsSelectedRadioTalkRadio =>
		!string.IsNullOrWhiteSpace(_selectedRadioId)
		&& string.Equals(_selectedRadioId, _talkRadioId, StringComparison.OrdinalIgnoreCase);

	// True when the viewed radio's RX monitor is muted (LISTEN off).
	public bool IsSelectedRadioMuted =>
		!string.IsNullOrWhiteSpace(_selectedRadioId) && _mutedRadioIds.Contains(_selectedRadioId);

	// LISTEN button label flips to MUTED when the viewed radio's RX monitor is muted.
	public string RadioListenButtonText => IsSelectedRadioMuted ? "MUTED" : "LISTEN";

	// TALK button glows active green (Code1) when the viewed radio is the talk radio.
	public IBrush RadioTalkBrush => IsSelectedRadioTalkRadio ? Brushes.LimeGreen : Brushes.Gray;

	// LISTEN button dims when muted so the operator can see at a glance it is not being monitored.
	public IBrush RadioListenBrush => IsSelectedRadioMuted ? Brushes.Gray : Brushes.LimeGreen;

	// Re-raise the TALK/LISTEN button bindings after the viewed radio (or its talk/mute state) changes.
	private void RaiseSelectedRadioTalkListenChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedRadioTalkRadio)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioTalkBrush)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedRadioMuted)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioListenButtonText)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RadioListenBrush)));
	}

	// CHANNELS button (replaces MONITOR): open the channel-selection page for the viewed radio, populated
	// from its retained channel list (§3.11).
	public void OpenChannelSelection()
	{
		RebuildChannelSelectionItems();
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChannelSelectionRadioTitle)));
		IsChannelSelectionOverlayVisible = true;
	}

	public void CloseChannelSelection() => IsChannelSelectionOverlayVisible = false;

	// The channels offered in the CHANNELS picker for the currently-viewed radio.
	public IReadOnlyList<ChannelSelectionItem> ChannelSelectionItems
	{
		get => _channelSelectionItems;
		private set => SetProperty(ref _channelSelectionItems, value);
	}

	public bool HasChannelSelectionItems => ChannelSelectionItems.Count > 0;

	// Apply a retained channel list and refresh the picker if it is for the radio currently being viewed.
	private void ApplyModuleChannels(ModuleChannelsMessage channels)
	{
		_radioChannels[channels.Id] = channels.Channels ?? Array.Empty<ChannelEntryMessage>();
		if (string.Equals(channels.Id, _selectedRadioId, StringComparison.OrdinalIgnoreCase))
		{
			RebuildChannelSelectionItems();
		}
	}

	// Rebuild the picker rows from the viewed radio's retained channel list.
	private void RebuildChannelSelectionItems()
	{
		var channels = _radioChannels.TryGetValue(_selectedRadioId, out var list) ? list : Array.Empty<ChannelEntryMessage>();
		ChannelSelectionItems = channels
			.Select(channel => new ChannelSelectionItem(channel.Index, channel.Label, this))
			.ToArray();
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChannelSelectionItems)));
	}

	// CHANNELS picker row tapped: command the viewed radio to that channel (if it supports channel_select),
	// reflect it locally, and close the picker (§3.11 / §5.3).
	public void SelectChannel(int index, string label)
	{
		if (string.IsNullOrWhiteSpace(_selectedRadioId))
		{
			return;
		}

		var radio = RadioRuntimeEntries.FirstOrDefault(entry => string.Equals(entry.RadioId, _selectedRadioId, StringComparison.OrdinalIgnoreCase));
		var supportsChannelSelect = radio?.Capabilities.Controls?.Contains("channel_select", StringComparer.OrdinalIgnoreCase) ?? false;
		if (supportsChannelSelect)
		{
			var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: true);
			var command = new RadioChannelSelectCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, index);
			_ = PublishCommandAsync($"myforce/module/{_selectedRadioId}/cmd/channel_select", command);
		}

		// Optimistically reflect the new channel; the radio's reported state will confirm/replace it.
		_radioChannelLabels[_selectedRadioId] = label;
		CurrentRadioChannel = label;
		MqttCommandFeedback = $"Channel: {label}";
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRadioChannelLabel)));
		RebuildPatrolRadioChannelList();
		RebuildRadioSelectionItems();
		RefreshTalkRadioDisplay();
		IsChannelSelectionOverlayVisible = false;
	}

	// CH UP / CH DN (status bar + RADIO page): step the viewed radio one channel along its channel list,
	// wrapping at the ends. Gives feedback when the radio has no channels so a press is never silent.
	public void ChannelStepUp() => StepChannel(+1);

	public void ChannelStepDown() => StepChannel(-1);

	private void StepChannel(int delta)
	{
		if (string.IsNullOrWhiteSpace(_selectedRadioId))
		{
			MqttCommandFeedback = "No radio selected.";
			return;
		}

		var channels = _radioChannels.TryGetValue(_selectedRadioId, out var list) ? list : Array.Empty<ChannelEntryMessage>();
		if (channels.Count == 0)
		{
			MqttCommandFeedback = "No channels available for this radio.";
			return;
		}

		// Locate the current channel by its label; default to the first if the current is unknown.
		var currentIndex = 0;
		for (var i = 0; i < channels.Count; i++)
		{
			if (string.Equals(channels[i].Label, CurrentRadioChannel, StringComparison.OrdinalIgnoreCase))
			{
				currentIndex = i;
				break;
			}
		}

		var nextIndex = ((currentIndex + delta) % channels.Count + channels.Count) % channels.Count;
		var next = channels[nextIndex];
		SelectChannel(next.Index, next.Label);
	}

	// PROXIMITY LIST press: jump to the nearby channel's radio as both the selected radio and the talk
	// radio, and tune it to that channel (§radio-page-semantics).
	public void SelectProximitySlot(int slotIndex)
	{
		if (slotIndex < 0 || slotIndex >= _proximitySlots.Length || _proximitySlots[slotIndex] is not { } center)
		{
			return;
		}

		SelectRadioTarget(center.RadioId);         // selected radio (publishes console select)
		DesignateSelectedAsTalkRadio();            // and the single talk radio

		// Tune to the channel: by index if the radio's channel list has it, else reflect the label directly.
		var channels = _radioChannels.TryGetValue(center.RadioId, out var list) ? list : Array.Empty<ChannelEntryMessage>();
		var match = channels.FirstOrDefault(channel => string.Equals(channel.Label, center.Channel, StringComparison.OrdinalIgnoreCase));
		if (match is not null)
		{
			SelectChannel(match.Index, match.Label);
		}
		else
		{
			_radioChannelLabels[center.RadioId] = center.Channel;
			CurrentRadioChannel = center.Channel;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRadioChannelLabel)));
			RefreshTalkRadioDisplay();
		}
	}

	// Volume up/down on the radio page reuse the master output volume for now.
	public void RadioVolumeUp() => IncreaseMasterVolume();

	public void RadioVolumeDown() => DecreaseMasterVolume();

	private void RebuildRadioSelectionItems()
	{
		RadioSelectionItems = RadioRuntimeEntries
			.Select(radio => new RadioSelectionItem(
				radio.RadioId,
				string.IsNullOrWhiteSpace(radio.DisplayName) ? radio.RadioId : radio.DisplayName,
				ResolveRadioChannel(radio.RadioId, radio.TypeId),
				this))
			.ToArray();
	}

	private void PublishConsoleSelect(string radioId)
	{
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: false);
		var command = new ConsoleSelectCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, radioId);
		_ = PublishCommandAsync(InternetRadioMqttTopics.ConsoleSelectCommandTopic, command);
	}

	// --- Function buttons (§3.10, v2.8) -----------------------------------------
	// The selected radio declares up to 24 function buttons in its registry; the UI
	// renders them and a press publishes cmd/button. Live label/active/enabled ride in
	// the module state's "buttons" map.

	private const string ConsoleId = "vip";   // this console's id on the bus (§5.4)

	// Physical panel is 8 buttons. Up to 24 functions span pages: page 1 holds 7 functions
	// (slot 8 = NEXT when more pages exist); later pages hold 6 (slot 1 = BACK, slot 8 = NEXT
	// when still more). Page count is dynamic from the function count.
	private const int RadioButtonSlots = 8;
	private const int FunctionsOnFirstPage = 7;   // slots 1-7 (slot 8 = NEXT)
	private const int FunctionsOnLaterPage = 6;   // slots 2-7 (slot 1 = BACK, slot 8 = NEXT)

	// Master live function buttons (declared by the selected radio); state updates land here.
	private IReadOnlyList<RadioFunctionButton> _allFunctionButtons = Array.Empty<RadioFunctionButton>();
	private int _radioPageIndex;

	// The 8 slots rendered for the current page (functions + BACK/NEXT/empty fillers).
	private IReadOnlyList<RadioFunctionButton> _radioButtonPage = Array.Empty<RadioFunctionButton>();

	public IReadOnlyList<RadioFunctionButton> RadioButtonPage
	{
		get => _radioButtonPage;
		private set => SetProperty(ref _radioButtonPage, value);
	}

	// Pressing a function button publishes module/<id>/cmd/button { button_id, console_id }.
	// A one-shot button acts; a menu button (opens_menu) makes the module push a menu (§3.10.3).
	public void PressRadioFunctionButton(string buttonId)
	{
		if (string.IsNullOrWhiteSpace(_selectedRadioId) || string.IsNullOrWhiteSpace(buttonId))
		{
			return;
		}

		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: true);
		var command = new ButtonPressCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, buttonId, ConsoleId);
		_ = PublishCommandAsync($"myforce/module/{_selectedRadioId}/cmd/button", command);
	}

	public void RadioPageNext()
	{
		if (_radioPageIndex < RadioPageCount - 1)
		{
			_radioPageIndex++;
			BuildRadioButtonPage();
		}
	}

	public void RadioPageBack()
	{
		if (_radioPageIndex > 0)
		{
			_radioPageIndex--;
			BuildRadioButtonPage();
		}
	}

	// Total pages for the current function count: 1 if <= 7, else 1 + ceil((n - 7) / 6).
	private int RadioPageCount
	{
		get
		{
			int n = _allFunctionButtons.Count;
			return n <= FunctionsOnFirstPage ? 1 : 1 + (int)Math.Ceiling((n - FunctionsOnFirstPage) / (double)FunctionsOnLaterPage);
		}
	}

	// Rebuild the master function buttons from the selected radio's declared buttons (§3.10.1).
	private void RebuildRadioFunctionButtons()
	{
		var match = RadioRuntimeEntries.FirstOrDefault(radio => string.Equals(radio.RadioId, _selectedRadioId, StringComparison.OrdinalIgnoreCase));
		var declared = match?.Capabilities?.Buttons;
		_allFunctionButtons = declared is null
			? Array.Empty<RadioFunctionButton>()
			: declared.Take(24).Select(button => new RadioFunctionButton(button.Id, button.Label, button.OpensMenu, this)).ToArray();
		_radioPageIndex = 0;
		BuildRadioButtonPage();
	}

	// Lay out the 8 slots for the current page. Function slots reference the live master buttons
	// so their active/label state stays current; BACK/NEXT/empty are filler slots.
	private void BuildRadioButtonPage()
	{
		int total = _allFunctionButtons.Count;
		var slots = new RadioFunctionButton[RadioButtonSlots];
		for (int i = 0; i < RadioButtonSlots; i++)
		{
			slots[i] = RadioFunctionButton.EmptySlot(this);
		}

		int firstFunctionSlot;
		int firstFunctionIndex;
		int functionsThisPage;

		if (_radioPageIndex == 0)
		{
			// Page 1: no BACK; functions fill slots 1-7.
			firstFunctionSlot = 0;
			firstFunctionIndex = 0;
			functionsThisPage = FunctionsOnFirstPage;
		}
		else
		{
			slots[0] = RadioFunctionButton.BackSlot(this);
			firstFunctionSlot = 1;
			firstFunctionIndex = FunctionsOnFirstPage + ((_radioPageIndex - 1) * FunctionsOnLaterPage);
			functionsThisPage = FunctionsOnLaterPage;
		}

		int placed = 0;
		for (int j = 0; j < functionsThisPage; j++)
		{
			int idx = firstFunctionIndex + j;
			if (idx >= total)
			{
				break;
			}

			slots[firstFunctionSlot + j] = _allFunctionButtons[idx];   // live master button
			placed++;
		}

		// Slot 8 is NEXT when there are still functions beyond this page.
		if (firstFunctionIndex + placed < total)
		{
			slots[RadioButtonSlots - 1] = RadioFunctionButton.NextSlot(this);
		}

		RadioButtonPage = slots;
	}

	// Apply live per-button state (label / active / enabled) from the module state map.
	private void ApplyFunctionButtonState(IReadOnlyDictionary<string, FunctionButtonStateMessage> buttonStates)
	{
		foreach (var button in _allFunctionButtons)
		{
			if (!buttonStates.TryGetValue(button.Id, out var state))
			{
				continue;
			}

			if (state.Active is bool active) { button.IsActive = active; }
			if (state.Enabled is bool enabled) { button.IsEnabled = enabled; }
			if (!string.IsNullOrWhiteSpace(state.Label)) { button.Label = state.Label!; }
		}
	}

	// --- Channel centers (GEO AREA) + patrol proximity list ---------------------
	// Groundwork ahead of the per-radio channel list: the GEO AREA screen will call
	// SetChannelCenter/ClearChannelCenter per channel; RefreshProximityList ranks all
	// configured centers by distance from the current location into the proximity slots.

	/// <summary>The centers configured for a radio (for the GEO AREA channel-centers screen).</summary>
	public IReadOnlyList<ChannelCenter> GetChannelCentersForRadio(string radioId) => _channelCenterStore.GetForRadio(radioId);

	/// <summary>Sets a channel's geographic center and re-ranks the proximity list.</summary>
	public void SetChannelCenter(string radioId, string channel, double latitude, double longitude)
	{
		_channelCenterStore.Set(radioId, channel, latitude, longitude);
		RefreshProximityList();
	}

	/// <summary>Clears a channel's center (centers are optional) and re-ranks the proximity list.</summary>
	public void ClearChannelCenter(string radioId, string channel)
	{
		_channelCenterStore.Clear(radioId, channel);
		RefreshProximityList();
	}

	// Fill the patrol PROXIMITY LIST with the channel centers nearest the current location.
	// When no centers are configured yet, the existing slot contents are left untouched.
	private void RefreshProximityList()
	{
		var centers = _channelCenterStore.GetAll();
		if (centers.Count == 0 || double.IsNaN(LocationLatitude) || double.IsNaN(LocationLongitude))
		{
			return;
		}

		var nearest = ProximityRanker.Nearest(LocationLatitude, LocationLongitude, centers, 4);
		ProximityChannel1 = ProximitySlotLabel(nearest, 0);
		ProximityChannel2 = ProximitySlotLabel(nearest, 1);
		ProximityChannel3 = ProximitySlotLabel(nearest, 2);
		ProximityChannel4 = ProximitySlotLabel(nearest, 3);
	}

	// A proximity slot shows the owning radio's alias then its channel ("ALIAS: CHANNEL") so the operator
	// can tell which radio a nearby channel belongs to, not just the bare channel name. Also records the
	// slot's channel center so a press can jump to that radio + channel.
	private string ProximitySlotLabel(IReadOnlyList<RankedChannelCenter> nearest, int index)
	{
		if (index >= nearest.Count)
		{
			_proximitySlots[index] = null;
			return string.Empty;
		}

		var center = nearest[index].Center;
		_proximitySlots[index] = center;
		return $"{ResolveRadioAlias(center.RadioId)}: {center.Channel}";
	}

	// Resolve a radio's operator-facing alias from the declared radios (sys/definition) first, then the
	// runtime entries, falling back to the raw radio id when neither carries a friendly name.
	private string ResolveRadioAlias(string radioId)
	{
		var adminMatch = RadioAdminItems.FirstOrDefault(radio => string.Equals(radio.RadioId, radioId, StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(adminMatch?.AliasInput))
		{
			return adminMatch!.AliasInput;
		}

		var runtimeMatch = RadioRuntimeEntries.FirstOrDefault(radio => string.Equals(radio.RadioId, radioId, StringComparison.OrdinalIgnoreCase));
		return string.IsNullOrWhiteSpace(runtimeMatch?.DisplayName) ? radioId : runtimeMatch!.DisplayName;
	}

	// --- GEO AREA overlay -------------------------------------------------------
	// Lets the operator drop a Lat/Long center for the selected radio's CURRENT channel
	// at the present location (works without a full channel list), and view/clear the
	// centers already configured for that radio.

	private bool _isGeoAreaOverlayVisible;
	private IReadOnlyList<ChannelCenterListItem> _geoAreaCenters = Array.Empty<ChannelCenterListItem>();

	public bool IsGeoAreaOverlayVisible
	{
		get => _isGeoAreaOverlayVisible;
		private set => SetProperty(ref _isGeoAreaOverlayVisible, value);
	}

	public IReadOnlyList<ChannelCenterListItem> GeoAreaCenters
	{
		get => _geoAreaCenters;
		private set => SetProperty(ref _geoAreaCenters, value);
	}

	public string GeoAreaTitle => $"GEO AREA: {_selectedRadioDisplayName}";

	// What pressing "SET CENTER HERE" will do: stamp the current channel at this location.
	public string GeoAreaCurrentChannelLabel => string.IsNullOrWhiteSpace(CurrentRadioChannel)
		? "No current channel reported yet, select a radio / channel first."
		: $"Set center for current channel \"{CurrentRadioChannel}\" at this location.";

	// GEO AREA button (radio page): open the channel-centers overlay for the selected radio.
	public void OpenGeoArea()
	{
		RebuildGeoAreaCenters();
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GeoAreaTitle)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GeoAreaCurrentChannelLabel)));
		IsGeoAreaOverlayVisible = true;
	}

	public void CloseGeoArea() => IsGeoAreaOverlayVisible = false;

	// Drop a center for the selected radio's current channel at the current GPS location.
	public void SetCurrentChannelCenterHere()
	{
		if (string.IsNullOrWhiteSpace(_selectedRadioId) || string.IsNullOrWhiteSpace(CurrentRadioChannel))
		{
			return;
		}

		if (double.IsNaN(LocationLatitude) || double.IsNaN(LocationLongitude))
		{
			return;
		}

		SetChannelCenter(_selectedRadioId, CurrentRadioChannel, LocationLatitude, LocationLongitude);
		RebuildGeoAreaCenters();
	}

	// Clear one channel's center (invoked from a list row) for the selected radio.
	public void ClearGeoAreaCenter(string channel)
	{
		if (string.IsNullOrWhiteSpace(_selectedRadioId))
		{
			return;
		}

		ClearChannelCenter(_selectedRadioId, channel);
		RebuildGeoAreaCenters();
	}

	private void RebuildGeoAreaCenters()
	{
		GeoAreaCenters = string.IsNullOrWhiteSpace(_selectedRadioId)
			? Array.Empty<ChannelCenterListItem>()
			: GetChannelCentersForRadio(_selectedRadioId)
				.Select(center => new ChannelCenterListItem(center.Channel, center.Latitude, center.Longitude, this))
				.ToArray();
	}

	// Camera REC button: pulse the GPIO controller's camera_record relay, flashing
	// the button so the operator gets immediate confirmation the press registered.
	public void TriggerCameraRecord()
	{
		PublishGpioRelayPulse("camera_record");
		_ = FlashCameraButtonAsync(active => IsCameraRecordActive = active);
	}

	// Camera STOP button: pulse the GPIO controller's camera_stop relay.
	public void TriggerCameraStop()
	{
		PublishGpioRelayPulse("camera_stop");
		_ = FlashCameraButtonAsync(active => IsCameraStopActive = active);
	}

	// Camera AUTOZ button: pulse the GPIO controller's cam_autozoom relay.
	public void TriggerCameraAutoZoom()
	{
		PublishGpioRelayPulse("cam_autozoom");
		_ = FlashCameraButtonAsync(active => IsCameraAutoZoomActive = active);
	}

	// Briefly lights a momentary camera button's active highlight as press feedback,
	// then clears it. The clear runs on the UI thread so the binding updates safely.
	private static async Task FlashCameraButtonAsync(Action<bool> setActive)
	{
		setActive(true);
		await Task.Delay(CameraFeedbackFlashMs).ConfigureAwait(false);
		await Dispatcher.UIThread.InvokeAsync(() => setActive(false));
	}

	// Press-flash state for the momentary camera buttons (REC / STOP / AUTOZ). True for
	// CameraFeedbackFlashMs after a press so the button shows the active highlight.
	private bool _isCameraRecordActive;
	public bool IsCameraRecordActive
	{
		get => _isCameraRecordActive;
		private set => SetProperty(ref _isCameraRecordActive, value);
	}

	private bool _isCameraStopActive;
	public bool IsCameraStopActive
	{
		get => _isCameraStopActive;
		private set => SetProperty(ref _isCameraStopActive, value);
	}

	private bool _isCameraAutoZoomActive;
	public bool IsCameraAutoZoomActive
	{
		get => _isCameraAutoZoomActive;
		private set => SetProperty(ref _isCameraAutoZoomActive, value);
	}

	// Publishes a momentary pulse to a named GPIO controller relay. The firmware
	// energises the relay then auto-releases it after CameraTriggerPulseMs, simulating
	// a button press on the camera/DVR. Fire-and-forget operating command (§4.6, §3.9.3).
	private void PublishGpioRelayPulse(string function)
	{
		var envelope = CreateCommandEnvelope(isAdminCommand: false, includeMessageId: false);
		var command = new GpioPulseCommandMessage(envelope.V, envelope.Ts, envelope.MsgId, envelope.Auth, function, CameraTriggerPulseMs);
		_ = PublishCommandAsync(InternetRadioMqttTopics.GpioPulseCommandTopic, command);
	}

	private void RaiseAlertCodeStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCode1Selected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCode2Selected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCode3Selected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code1StateText)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code2StateText)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code3StateText)));
	}

	private void RaiseAmFmStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmModuleTitle)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmPrimaryDisplay)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmPrimaryDisplayFontSize)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmFrequencyDisplay)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmBandLabel)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmStereoLabel)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmVolumeDisplay)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmActiveSourceSummary)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmDetailLine)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmSecondaryDetailLine)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAmFmDetailVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSeekControlsVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAmFmMuted)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAmFmChannelSetArmed)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFm1SourceSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAm1SourceSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBluetoothSourceSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInternetSourceSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInternetChannelListButtonVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmPreset1Label)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmPreset2Label)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmPreset3Label)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmPreset4Label)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmPreset5Label)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmFmPreset6Label)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EntertainmentModePrefix)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EntertainmentModeName)));
	}

	private void RaiseInternetChannelListStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInternetChannelListVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternetGenreFilter)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternetLanguageFilter)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternetSelectedStationName)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternetSelectedStreamUrl)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternetSelectedMetadata)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternetGenreOptions)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InternetLanguageOptions)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredInternetRadioStations)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleInternetRadioStations)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanScrollInternetStationsUp)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanScrollInternetStationsDown)));
	}

	private void LoadInternetRadioCatalog()
	{
		var streamsPath = Path.Combine(AppContext.BaseDirectory, "streams.sii");
		_internetRadioStations = _internetRadioCatalogService.LoadCatalog(streamsPath);
		if (ValidateInternetPresets())
		{
			SaveAmFmUiState();
		}

		_selectedInternetStation = _internetRadioStations.FirstOrDefault();
		ResetInternetStationViewport(preserveSelection: false);
		RaiseInternetChannelListStateChanged();
	}

	/// <summary>
	/// Resets the INT popup viewport when the filtered station list changes.
	/// </summary>
	private void ResetInternetStationViewport(bool preserveSelection)
	{
		_internetStationViewportStartIndex = 0;
		if (!preserveSelection)
		{
			_selectedInternetStation = FilteredInternetRadioStations.FirstOrDefault();
			return;
		}

		if (_selectedInternetStation is null)
		{
			return;
		}

		if (FilteredInternetRadioStations.Any(station => string.Equals(station.StreamUrl, _selectedInternetStation.StreamUrl, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}
	}

	/// <summary>
	/// Restores the last selected mode and per-mode source selections from local state.
	/// </summary>
	private void RestoreAmFmUiState()
	{
		var savedState = _amFmUiStateStore.Load();
		_amFmFrequency = savedState.FmFrequency;
		_amFrequency = savedState.AmFrequency;
		_fmPresetStations = NormalizeFrequencyPresets(savedState.FmPresets);
		_amPresetStations = NormalizeFrequencyPresets(savedState.AmPresets);
		_internetPresetStationNames = NormalizeInternetPresets(savedState.InternetPresets);
		_bluetoothDisplayLabel = string.IsNullOrWhiteSpace(savedState.BluetoothLabel) ? "BT AUDIO" : savedState.BluetoothLabel;
		_isAmFmMuted = savedState.IsMuted;
		_amFmVolume = Math.Clamp(savedState.Volume, 0, MaxSourceVolume);

		if (!string.IsNullOrWhiteSpace(savedState.InternetStreamUrl))
		{
			_selectedInternetStation = _internetRadioStations.FirstOrDefault(station =>
				string.Equals(station.StreamUrl, savedState.InternetStreamUrl, StringComparison.OrdinalIgnoreCase))
				?? _selectedInternetStation;
		}

		if (!Enum.TryParse<AuxiliaryAudioSourceMode>(savedState.LastMode, ignoreCase: true, out var restoredMode))
		{
			restoredMode = AuxiliaryAudioSourceMode.Fm1;
		}

		_selectedAuxiliarySourceMode = restoredMode;
		if (ValidateInternetPresets())
		{
			SaveAmFmUiState();
		}

		ResetInternetStationViewport(preserveSelection: true);
	}

	/// <summary>
	/// Saves the current mode and source selections for restoration on the next launch.
	/// </summary>
	private void SaveAmFmUiState()
	{
		var state = new AmFmUiState(
			LastMode: _selectedAuxiliarySourceMode.ToString(),
			FmFrequency: _amFmFrequency,
			AmFrequency: _amFrequency,
			BluetoothLabel: _bluetoothDisplayLabel,
			InternetStreamUrl: _selectedInternetStation?.StreamUrl,
			IsMuted: _isAmFmMuted,
			Volume: _amFmVolume,
			FmPresets: [.. _fmPresetStations],
			AmPresets: [.. _amPresetStations],
			InternetPresets: [.. _internetPresetStationNames]);

		_amFmUiStateStore.Save(state);
	}

	/// <summary>
	/// Keeps the selected INT station inside the visible touch-scrolling window.
	/// </summary>
	private void EnsureInternetStationVisible(int stationIndex)
	{
		if (stationIndex < 0)
		{
			return;
		}

		if (stationIndex < _internetStationViewportStartIndex)
		{
			_internetStationViewportStartIndex = stationIndex;
			return;
		}

		if (stationIndex < _internetStationViewportStartIndex + InternetStationViewportSize)
		{
			return;
		}

		_internetStationViewportStartIndex = stationIndex - InternetStationViewportSize + 1;
	}

	private static string GetNextFilterValue(IReadOnlyList<string> values, string currentValue)
	{
		if (values.Count == 0)
		{
			return "ALL";
		}

		var currentIndex = -1;
		for (var index = 0; index < values.Count; index++)
		{
			if (!string.Equals(values[index], currentValue, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			currentIndex = index;
			break;
		}

		if (currentIndex < 0)
		{
			return values[0];
		}

		var nextIndex = (currentIndex + 1) % values.Count;
		return values[nextIndex];
	}

	private string GetPresetLabel(int presetIndex)
	{
		return _selectedAuxiliarySourceMode switch
		{
			AuxiliaryAudioSourceMode.Fm1 => FormatPresetLabel(_fmPresetStations[presetIndex]),
			AuxiliaryAudioSourceMode.Am1 => FormatPresetLabel(_amPresetStations[presetIndex]),
			AuxiliaryAudioSourceMode.Bluetooth => string.Empty,
			AuxiliaryAudioSourceMode.InternetRadio => _internetPresetStationNames[presetIndex] ?? string.Empty,
			_ => throw new ArgumentOutOfRangeException(),
		};
	}

	/// <summary>
	/// Refreshes the what3words display when the location changes enough to warrant a new lookup.
	/// </summary>
	private async Task RefreshWhat3WordsAsync()
	{
		if (Math.Abs(LocationLatitude - _lastWhat3WordsLatitude) < 0.0001d
			&& Math.Abs(LocationLongitude - _lastWhat3WordsLongitude) < 0.0001d)
		{
			return;
		}

		_lastWhat3WordsLatitude = LocationLatitude;
		_lastWhat3WordsLongitude = LocationLongitude;

		// This runs fire-and-forget from the constructor and the location setters, so any
		// unhandled fault here would become an unobserved task exception. The service
		// already swallows network failures, but we defend in depth: on any failure we
		// clear the cached coordinates so the next location change retries the lookup
		// rather than being skipped as "unchanged".
		try
		{
			string? words = await _what3WordsService.GetWordsAsync(LocationLatitude, LocationLongitude, CancellationToken.None).ConfigureAwait(false);
			string resolvedText = string.IsNullOrWhiteSpace(words)
				? "CONFIG API KEY"
				: words.ToUpperInvariant();

			await Dispatcher.UIThread.InvokeAsync(() => What3WordsDisplay = resolvedText);
		}
		catch (Exception)
		{
			_lastWhat3WordsLatitude = double.NaN;
			_lastWhat3WordsLongitude = double.NaN;
		}
	}

	private void SaveCurrentPreset(int presetIndex)
	{
		switch (_selectedAuxiliarySourceMode)
		{
			case AuxiliaryAudioSourceMode.Fm1:
				_fmPresetStations[presetIndex] = _amFmFrequency;
				break;

			case AuxiliaryAudioSourceMode.Am1:
				_amPresetStations[presetIndex] = _amFrequency;
				break;

			case AuxiliaryAudioSourceMode.Bluetooth:
				break;

			case AuxiliaryAudioSourceMode.InternetRadio:
				_internetPresetStationNames[presetIndex] = _selectedInternetStation?.DisplayName;
				break;

			default:
				throw new ArgumentOutOfRangeException();
		}
	}

	private bool RecallPreset(int presetIndex)
	{
		switch (_selectedAuxiliarySourceMode)
		{
			case AuxiliaryAudioSourceMode.Fm1:
				if (!_fmPresetStations[presetIndex].HasValue)
				{
					return false;
				}

				_amFmFrequency = _fmPresetStations[presetIndex]!.Value;
				return true;

			case AuxiliaryAudioSourceMode.Am1:
				if (!_amPresetStations[presetIndex].HasValue)
				{
					return false;
				}

				_amFrequency = _amPresetStations[presetIndex]!.Value;
				return true;

			case AuxiliaryAudioSourceMode.Bluetooth:
				return false;

			case AuxiliaryAudioSourceMode.InternetRadio:
				return RecallInternetPreset(presetIndex);

			default:
				throw new ArgumentOutOfRangeException();
		}
	}

	private bool RecallInternetPreset(int presetIndex)
	{
		var stationName = _internetPresetStationNames[presetIndex];
		if (string.IsNullOrWhiteSpace(stationName))
		{
			return false;
		}

		var station = FindInternetStationByDisplayName(stationName);
		if (station is null)
		{
			_internetPresetStationNames[presetIndex] = null;
			SaveAmFmUiState();
			return false;
		}

		_selectedInternetStation = station;
		IsInternetChannelListVisible = false;
		EnsureInternetStationVisible(FilteredInternetRadioStations.ToList().FindIndex(candidate =>
			string.Equals(candidate.StreamUrl, station.StreamUrl, StringComparison.OrdinalIgnoreCase)));
		return true;
	}

	private bool ValidateInternetPresets()
	{
		var hasChanges = false;
		for (var presetIndex = 0; presetIndex < _internetPresetStationNames.Length; presetIndex++)
		{
			var stationName = _internetPresetStationNames[presetIndex];
			if (string.IsNullOrWhiteSpace(stationName))
			{
				continue;
			}

			if (FindInternetStationByDisplayName(stationName) is not null)
			{
				continue;
			}

			_internetPresetStationNames[presetIndex] = null;
			hasChanges = true;
		}

		return hasChanges;
	}

	private InternetRadioStation? FindInternetStationByDisplayName(string stationName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stationName);

		return _internetRadioStations.FirstOrDefault(station =>
			string.Equals(station.DisplayName, stationName, StringComparison.OrdinalIgnoreCase));
	}

	private static decimal?[] NormalizeFrequencyPresets(decimal?[]? presets)
	{
		var normalizedPresets = new decimal?[PresetCount];
		if (presets is null)
		{
			return normalizedPresets;
		}

		for (var presetIndex = 0; presetIndex < Math.Min(PresetCount, presets.Length); presetIndex++)
		{
			normalizedPresets[presetIndex] = presets[presetIndex];
		}

		return normalizedPresets;
	}

	private static string?[] NormalizeInternetPresets(string?[]? presets)
	{
		var normalizedPresets = new string?[PresetCount];
		if (presets is null)
		{
			return normalizedPresets;
		}

		for (var presetIndex = 0; presetIndex < Math.Min(PresetCount, presets.Length); presetIndex++)
		{
			normalizedPresets[presetIndex] = string.IsNullOrWhiteSpace(presets[presetIndex]) ? null : presets[presetIndex];
		}

		return normalizedPresets;
	}

	private static string FormatPresetLabel(decimal? frequency)
	{
		return frequency?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;
	}

	private string ResolveAudioOutputSpeakerLabel(string? deviceId)
	{
		if (string.IsNullOrWhiteSpace(deviceId))
		{
			return "UNKNOWN";
		}

		return AudioOutputDevices.FirstOrDefault(device => string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.DisplayName
			?? deviceId.ToUpperInvariant();
	}

	// All source/master volumes are an integer 0..25 where 0 = 0% and 25 = 100% (unity gain 1.0).
	private const int MaxSourceVolume = 25;

	private static decimal MapEntertainmentVolumeToGain(int volume)
	{
		var normalizedVolume = Math.Clamp(volume, 0, MaxSourceVolume);
		return decimal.Round(normalizedVolume / (decimal)MaxSourceVolume, 3, MidpointRounding.AwayFromZero);
	}

	private static string GetServiceStateLabel(JsonElement stateElement)
	{
		if (stateElement.ValueKind == JsonValueKind.Number && stateElement.TryGetInt32(out var numericState))
		{
			return numericState switch
			{
				0 => "Stopped",
				1 => "Running",
				_ => $"State {numericState}"
			};
		}

		return stateElement.ToString();
	}

	private void UpdateSystemComponentStatus(string componentId, AdminComponentStatus status, string detail, string topic)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
		ArgumentException.ThrowIfNullOrWhiteSpace(detail);

		var updatedStatuses = SystemComponentStatuses
			.Select(component => string.Equals(component.Id, componentId, StringComparison.OrdinalIgnoreCase)
				? component with { Status = status, Detail = detail, Topic = topic }
				: component)
			.ToArray();

		if (!updatedStatuses.Any(component => string.Equals(component.Id, componentId, StringComparison.OrdinalIgnoreCase)))
		{
			updatedStatuses =
			[
				..updatedStatuses,
				new AdminSystemComponentStatus(componentId, GetDisplayNameFromComponentId(componentId), status, detail, topic)
			];
		}

		SystemComponentStatuses = updatedStatuses;
		RaiseAdminSystemStatusChanged();
	}

	// Remove a System Status row entirely (its module cleared its retained status, i.e. was removed). The
	// local UI row is never removed.
	private void RemoveSystemComponentStatus(string componentId)
	{
		// Fixed infrastructure rows (UI + controllers) are never removed, only their status changes.
		if (string.IsNullOrWhiteSpace(componentId) || InfrastructureComponentIds.Contains(componentId))
		{
			return;
		}

		var remaining = SystemComponentStatuses
			.Where(component => !string.Equals(component.Id, componentId, StringComparison.OrdinalIgnoreCase))
			.ToArray();
		if (remaining.Length == SystemComponentStatuses.Count)
		{
			return;
		}

		SystemComponentStatuses = remaining;
		RaiseAdminSystemStatusChanged();
	}

	/// <summary>
	/// Downgrades stale component rows to offline when heartbeat updates stop arriving.
	/// </summary>
	private void RefreshComponentHeartbeatStatus()
	{
		var now = DateTime.UtcNow;
		var hasChanges = false;
		var refreshedStatuses = SystemComponentStatuses
			.Select(component =>
			{
				if (string.Equals(component.Id, "ui", StringComparison.OrdinalIgnoreCase)
					|| string.IsNullOrWhiteSpace(component.Detail))
				{
					return component;
				}

				if (!TryParseHeartbeatTimestamp(component.Detail, out var lastSeenUtc))
				{
					return component;
				}

				if (now - lastSeenUtc <= ComponentHeartbeatTimeout)
				{
					return component;
				}

				hasChanges = true;
				return component with
				{
					Status = AdminComponentStatus.Offline,
					Detail = $"Heartbeat timed out. Last seen {lastSeenUtc:O}",
				};
			})
			.ToArray();

		if (!hasChanges)
		{
			return;
		}

		SystemComponentStatuses = refreshedStatuses;
		RaiseAdminSystemStatusChanged();
	}

	private static bool TryParseHeartbeatTimestamp(string detail, out DateTime lastSeenUtc)
	{
		const string prefix = "Heartbeat: ";
		if (!detail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			lastSeenUtc = default;
			return false;
		}

		return DateTime.TryParse(detail[prefix.Length..], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out lastSeenUtc);
	}

	private static string GetComponentIdFromTopic(string topic)
	{
		if (string.Equals(topic, AudioProcessorStatusTopic, StringComparison.OrdinalIgnoreCase))
		{
			return "audio-processor";
		}

		if (string.Equals(topic, GpioControllerStatusTopic, StringComparison.OrdinalIgnoreCase))
		{
			return "gpio-controller";
		}

		if (string.Equals(topic, SirenInterfaceStatusTopic, StringComparison.OrdinalIgnoreCase))
		{
			return "siren-interface";
		}

		return topic;
	}

	private static string GetDisplayNameFromComponentId(string componentId)
	{
		return componentId switch
		{
			"ui" => "Main UI",
			"audio-processor" => "Audio Processor",
			"gpio-controller" => "GPIO Controller",
			"siren-interface" => "Siren Interface",
			_ => componentId.Replace('-', ' ').ToUpperInvariant(),
		};
	}

	private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (Equals(field, value))
		{
			return false;
		}

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}

	private void OnPropertyChanged(string propertyName)
		=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// The physical hand grip controller (HCD) mode, which contextualizes the on-screen soft keys.
/// Scaffolding for hand-grip and custom soft-key support.
/// </summary>
public enum HandGripMode
{
	Lights,
	Radio,
	Patrol
}