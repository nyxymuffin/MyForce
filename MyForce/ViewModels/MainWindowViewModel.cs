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
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using MyForce.Models;
using MyForce.Services;

namespace MyForce.ViewModels;

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

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly DispatcherTimer _clockTimer;

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

	private MainConsoleTab _selectedTab = MainConsoleTab.Patrol;

	private bool _isAdminOverlayVisible;

	private AdminSection _selectedAdminSection = AdminSection.System;

	private string _adminSectionTitle = "SYSTEM";

	private string _adminSectionDescription = "Core console configuration and startup settings will live here.";

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
		_mqttConnectionService = new MqttConnectionService();
		_mqttConnectionService.StateChanged += OnMqttStateChanged;
		_clockTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1),
		};

		_clockTimer.Tick += OnClockTimerTick;
		UpdateClock();
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
		IsAdminOverlayVisible = true;
	}

	public void CloseAdminOverlay()
	{
		IsAdminOverlayVisible = false;
	}

	public void SelectAdminSection(AdminSection section)
	{
		SelectedAdminSection = section;

		switch (section)
		{
			case AdminSection.System:
				AdminSectionTitle = "SYSTEM";
				AdminSectionDescription = "Core console configuration and startup settings will live here.";
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

	public void Dispose()
	{
		_clockTimer.Stop();
		_clockTimer.Tick -= OnClockTimerTick;
		_mqttConnectionService.StateChanged -= OnMqttStateChanged;
		_mqttConnectionService.Dispose();
	}

	private async Task InitializeMqttAsync()
	{
		var settings = new MqttConnectionSettings(
			Host: "127.0.0.1",
			Port: 1883,
			ClientId: $"myforce-ui-{Environment.MachineName}");

		await _mqttConnectionService.ConnectAsync(settings).ConfigureAwait(false);
	}

	private void OnClockTimerTick(object? sender, EventArgs e)
	{
		UpdateClock();
	}

	private void OnMqttStateChanged(object? sender, MqttConnectionState state)
	{
		Dispatcher.UIThread.Post(() => ApplyMqttState(state));
	}

	private void ApplyMqttState(MqttConnectionState state)
	{
		MqttStatus = state.Status;
		MqttEndpoint = string.IsNullOrWhiteSpace(state.Endpoint) ? "127.0.0.1:1883" : state.Endpoint;
		MqttDetail = state.Detail;
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