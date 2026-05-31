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

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly DispatcherTimer _clockTimer;

	private readonly MqttConnectionService _mqttConnectionService;

	private string _clock = string.Empty;

	private string _date = string.Empty;

	private string _currentTalkRadio = "APX7500 V/8";

	private string _currentRadioChannel = "CT OPS 800";

	private string _alertLightSiren = "CODE 1";

	private string _directionalStatus = "RIGHT";

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

	private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (Equals(field, value))
		{
			return;
		}

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}