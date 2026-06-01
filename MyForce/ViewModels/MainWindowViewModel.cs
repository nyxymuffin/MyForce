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
using System.Threading.Tasks;
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

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
	private static readonly decimal[] AmFmPresetStations = [88.1m, 88.1m, 88.1m, 88.1m, 88.1m, 88.1m];
	private const string AdminPinCode = "2135";
	private const string AudioProcessorStatusTopic = "myforce/ap/status/service";
	private const string AudioProcessorChannelGainCommandTopic = "myforce/ap/cmd/channel-gain";
	private const string AudioProcessorEntertainmentChannelId = "entertainment";
	private const string DefaultAudioOutputSpeakerId = "cabin-speaker";
	private const string GpioControllerStatusTopic = "myforce/gpio/status/service";
	private const string SirenInterfaceStatusTopic = "myforce/siren/status/service";
	private static readonly TimeSpan ComponentHeartbeatTimeout = TimeSpan.FromSeconds(15);

	private const int InternetStationViewportSize = 6;

	private readonly DispatcherTimer _clockTimer;

	private readonly InternetRadioCatalogService _internetRadioCatalogService;

	private readonly AmFmUiStateStore _amFmUiStateStore;

	private readonly MqttConnectionService _mqttConnectionService;

	private string _clock = string.Empty;

	private string _date = string.Empty;

	private string _currentTalkRadio = "APX7500 V/8";

	private string _currentRadioChannel = "CT OPS 800";

	// Stores the current alert light and siren status shown in the status panel.
	private string _alertLightSiren = "OFF";

	// Stores the current directional status shown in the status panel.
	private string _directionalStatus = "OFF";

	private string _sirenStatus = "DISABLED";

	private string _currentTalkRadioVolume = "13";

	private decimal _amFmFrequency = 97.5m;

	private int _amFmVolume = 25;

	private decimal _amFrequency = 87.5m;

	private bool _isAmFmMuted;

	private bool _isAmFmStereoEnabled = true;

	private AuxiliaryAudioSourceMode _selectedAuxiliarySourceMode = AuxiliaryAudioSourceMode.Fm1;

	private bool _isInternetChannelListVisible;

	private string _internetGenreFilter = "ALL";

	private string _internetLanguageFilter = "ALL";

	private InternetRadioStation? _selectedInternetStation;

	private string _bluetoothDisplayLabel = "BT AUDIO";

	private IReadOnlyList<InternetRadioStation> _internetRadioStations = Array.Empty<InternetRadioStation>();

	private int _internetStationViewportStartIndex;

	private string _radio1ChannelName = "CT OPS 800";

	private string _radio2ChannelName = "CT OPS V";

	private string _radio3ChannelName = "TXT MAIN";

	private string _radio4ChannelName = "CT ALE M";

	private string _radio5ChannelName = "$CMD_1";

	private string _radio6ChannelName = "ABIA TWR";

	private string _proximityChannel1 = "CT OPS 800";

	private string _proximityChannel2 = "DFW TAC1";

	private string _proximityChannel3 = "$CMD_1";

	private string _proximityChannel4 = "DFW_TWR_E";

	private string _mqttStatus = "OFFLINE";

	private string _mqttEndpoint = "127.0.0.1:1883";

	private string _mqttDetail = "Broker not connected.";

	private static readonly JsonSerializerOptions MqttJsonSerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false,
	};

	private MainConsoleTab _selectedTab = MainConsoleTab.Patrol;

	private bool _isAdminOverlayVisible;
	private bool _isAdminAuthenticated;

	private AdminSection _selectedAdminSection = AdminSection.System;

	private string _adminSectionTitle = "SYSTEM";

	private string _adminSectionDescription = "Core console configuration and startup settings will live here.";

	private string _adminPinEntry = string.Empty;

	private string _adminPinStatus = "Enter PIN to unlock admin controls.";

	private IReadOnlyList<AudioDeviceStateMessage> _audioOutputDevices = Array.Empty<AudioDeviceStateMessage>();

	private string _apConfiguredOutputSpeakerId = DefaultAudioOutputSpeakerId;

	private string _selectedAdminOutputSpeakerId = DefaultAudioOutputSpeakerId;

	private string _adminAudioStatus = "Waiting for AP audio routing state.";

	private IReadOnlyList<AdminSystemComponentStatus> _systemComponentStatuses =
	[
		new("ui", "Main UI", AdminComponentStatus.Online, "Local console is running.", "LOCAL"),
		new("audio-processor", "Audio Processor", AdminComponentStatus.Unknown, "Waiting for retained MQTT status.", AudioProcessorStatusTopic),
		new("gpio-controller", "GPIO Controller", AdminComponentStatus.Unknown, "Waiting for retained MQTT status.", GpioControllerStatusTopic),
		new("siren-interface", "Siren Interface", AdminComponentStatus.Unknown, "Waiting for retained MQTT status.", SirenInterfaceStatusTopic),
	];

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
		_mqttConnectionService.StateChanged += OnMqttStateChanged;
		_mqttConnectionService.MessageReceived += OnMqttMessageReceived;
		_clockTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1),
		};

		_clockTimer.Tick += OnClockTimerTick;
		UpdateClock();
		LoadInternetRadioCatalog();
		RestoreAmFmUiState();
		ApplyMqttState(_mqttConnectionService.CurrentState);
		_clockTimer.Start();
		_ = InitializeMqttAsync();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Title => "MyForce Main Console";

	public string SpeedLabel => "SPD";

	public string SpeedValue => "55";

	public string LocationLabel => "LOCATION";

	public string LocationValue => "30.5422, -97.6384";

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

	public string SirenStatus
	{
		get => _sirenStatus;
		set => SetProperty(ref _sirenStatus, value);
	}

	//Extra Info Provided by the Currently Selected channel. could be currently talking rid or somehting
	public string CurSelChExt1 { get => _CurSelChExt1; set => SetProperty(ref _CurSelChExt1, value); }

	public string CurSelChExt2 { get => _CurSelChExt2; set => SetProperty(ref _CurSelChExt2, value); }

	public string CurSelChExt3 { get => _CurSelChExt3; set => SetProperty(ref _CurSelChExt3, value); }

	public string CurSelChExt4 { get => _CurSelChExt4; set => SetProperty(ref _CurSelChExt4, value); }

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

	public string AmFmPreset1Label => FormatPresetLabel(AmFmPresetStations[0]);

	public string AmFmPreset2Label => FormatPresetLabel(AmFmPresetStations[1]);

	public string AmFmPreset3Label => FormatPresetLabel(AmFmPresetStations[2]);

	public string AmFmPreset4Label => FormatPresetLabel(AmFmPresetStations[3]);

	public string AmFmPreset5Label => FormatPresetLabel(AmFmPresetStations[4]);

	public string AmFmPreset6Label => FormatPresetLabel(AmFmPresetStations[5]);

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

	public bool IsAdminAudioSectionSelected => SelectedAdminSection == AdminSection.Audio;

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

	public string AdminAudioDeviceSummary => $"MASTER OUTPUT: {AppliedAudioOutputSpeakerLabel}  CHANNEL DEVICES: {Math.Max(AudioOutputDevices.Count - 1, 0)}";

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

	public bool IsAdminSystemGeneralSectionSelected => SelectedAdminSection == AdminSection.System;

	public bool IsAdminSystemStatusSectionSelected => SelectedAdminSection == AdminSection.SystemStatus;

	public bool IsAdminNonSystemSectionSelected => !IsAdminSystemGeneralSectionSelected && !IsAdminSystemStatusSectionSelected;

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
				AdminSectionDescription = "MQTT, LAN, broker, and remote endpoint configuration will live here.";
				break;
			case AdminSection.Security:
				AdminSectionTitle = "SECURITY";
				AdminSectionDescription = "Authentication, roles, and system access configuration will live here.";
				break;
			case AdminSection.Integrations:
				AdminSectionTitle = "INTEGRATIONS";
				AdminSectionDescription = "CAD, siren, SCADA, and other external integration settings will live here.";
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
	/// Adds a digit to the admin PIN entry and unlocks the admin overlay when the configured PIN is entered.
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

		_amFmFrequency = decimal.Round(decimal.Min(_amFmFrequency + 0.2m, 107.9m), 1, MidpointRounding.AwayFromZero);
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
		_amFmFrequency = decimal.Round(decimal.Min(_amFmFrequency + 0.5m, 107.9m), 1, MidpointRounding.AwayFromZero);
		RaiseAmFmStateChanged();
	}

	public void SeekAmFmDown()
	{
		_amFmFrequency = decimal.Round(decimal.Max(_amFmFrequency - 0.5m, 87.5m), 1, MidpointRounding.AwayFromZero);
		RaiseAmFmStateChanged();
	}

	public void ScanAmFm()
	{
		SeekAmFmUp();
	}

	public void StoreCurrentAmFmChannel()
	{
		_isAmFmStereoEnabled = !_isAmFmStereoEnabled;
		RaiseAmFmStateChanged();
	}

	public void IncreaseAmFmVolume()
	{
		_amFmVolume = Math.Min(_amFmVolume + 1, 40);
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
		if (presetIndex < 0 || presetIndex >= AmFmPresetStations.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(presetIndex));
		}

		_amFmFrequency = AmFmPresetStations[presetIndex];
		RaiseAmFmStateChanged();
	}

	public void Dispose()
	{
		_clockTimer.Stop();
		_clockTimer.Tick -= OnClockTimerTick;
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
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.StateTopic).ConfigureAwait(false);
		await _mqttConnectionService.SubscribeAsync(InternetRadioMqttTopics.AudioFrameworkStateTopic).ConfigureAwait(false);
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

	private void ApplyMqttState(MqttConnectionState state)
	{
		MqttStatus = state.Status;
		MqttEndpoint = string.IsNullOrWhiteSpace(state.Endpoint) ? "127.0.0.1:1883" : state.Endpoint;
		MqttDetail = state.Detail;

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
			UpdateSystemComponentStatus(GetComponentIdFromTopic(topic), AdminComponentStatus.Unknown, "Status payload was empty.", topic);
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

		var command = new InternetRadioPlayCommandMessage(
			station.StreamUrl,
			station.DisplayName,
			station.Genre,
			station.Language);

		var payload = JsonSerializer.Serialize(command, MqttJsonSerializerOptions);
		await _mqttConnectionService.PublishAsync(InternetRadioMqttTopics.PlayCommandTopic, payload).ConfigureAwait(false);
	}

	/// <summary>
	/// Publishes a stop command to the AP so internet radio playback halts immediately.
	/// </summary>
	private async Task PublishInternetRadioStopCommandAsync()
	{
		await _mqttConnectionService.PublishAsync(InternetRadioMqttTopics.StopCommandTopic, "{}" ).ConfigureAwait(false);
	}

	/// <summary>
	/// Publishes the local entertainment volume to the AP mixer so AM/FM and INT playback follow the UI volume controls.
	/// </summary>
	private void PublishEntertainmentVolumeCommand()
	{
		var command = new AudioChannelGainCommandMessage(
			AudioProcessorEntertainmentChannelId,
			MapEntertainmentVolumeToGain(_amFmVolume));
		var payload = JsonSerializer.Serialize(command, MqttJsonSerializerOptions);
		_ = _mqttConnectionService.PublishAsync(AudioProcessorChannelGainCommandTopic, payload);
	}

	private async Task PublishOutputSpeakerCommandAsync(string deviceId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

		var command = new OutputSpeakerCommandMessage(deviceId);
		var payload = JsonSerializer.Serialize(command, MqttJsonSerializerOptions);
		await _mqttConnectionService.PublishAsync(InternetRadioMqttTopics.SpeakerOutputCommandTopic, payload).ConfigureAwait(false);
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
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminSystemGeneralSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminSystemStatusSectionSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdminAudioSectionSelected)));
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

	private void RaiseDirectionalStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirectionalLeftSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirectionalCenterOutSelected)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirectionalRightSelected)));
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
		_bluetoothDisplayLabel = string.IsNullOrWhiteSpace(savedState.BluetoothLabel) ? "BT AUDIO" : savedState.BluetoothLabel;
		_isAmFmMuted = savedState.IsMuted;
		_amFmVolume = Math.Clamp(savedState.Volume, 0, 40);

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
			Volume: _amFmVolume);

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

	private static string FormatPresetLabel(decimal frequency)
	{
		return frequency.ToString("0.0", CultureInfo.InvariantCulture);
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

	private static decimal MapEntertainmentVolumeToGain(int volume)
	{
		var normalizedVolume = Math.Clamp(volume, 0, 40);
		return decimal.Round(normalizedVolume / 20m, 2, MidpointRounding.AwayFromZero);
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
}