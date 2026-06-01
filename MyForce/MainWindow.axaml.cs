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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using MyForce.ViewModels;

namespace MyForce;

public partial class MainWindow : Window
{
	private const int AdminTapThreshold = 5;

	private static readonly TimeSpan AdminTapWindow = TimeSpan.FromSeconds(20);

	private readonly MainWindowViewModel _viewModel;

	private readonly Queue<DateTime> _speedTapTimes = new();

	public MainWindow()
	{
		InitializeComponent();
		_viewModel = new MainWindowViewModel();
		DataContext = _viewModel;
	}

	protected override void OnClosed(System.EventArgs e)
	{
		_viewModel.Dispose();
		base.OnClosed(e);
	}

	private void OnChannelUpPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnSpeedPressed(object? sender, PointerPressedEventArgs e)
	{
		DateTime now = DateTime.UtcNow;
		_speedTapTimes.Enqueue(now);

		while (_speedTapTimes.Count > 0 && now - _speedTapTimes.Peek() > AdminTapWindow)
		{
			_ = _speedTapTimes.Dequeue();
		}

		if (_speedTapTimes.Count < AdminTapThreshold)
		{
			return;
		}

		_speedTapTimes.Clear();
		_viewModel.OpenAdminOverlay();
	}

	private void OnChannelDownPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnScanPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnCameraRecordPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnCameraStopPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnCameraAutoZoomPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnEmergencyPressed(object? sender, PointerPressedEventArgs e)
	{
		if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.Shutdown();
			return;
		}
	}

	private void OnPatrolTabPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectTab(MainConsoleTab.Patrol);
	}

	private void OnLightsAndSirensTabPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectTab(MainConsoleTab.LightsAndSirens);
	}

	private void OnRadioTabPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectTab(MainConsoleTab.Radio);
	}

	private void OnRadarTabPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectTab(MainConsoleTab.Radar);
	}

	private void OnAmFmTabPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectTab(MainConsoleTab.AmFm);
	}

	private void OnCadTabPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectTab(MainConsoleTab.Cad);
	}

	private void OnCameraTabPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectTab(MainConsoleTab.Camera);
	}

	private void OnProximityChannel1Pressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnProximityChannel2Pressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnProximityChannel3Pressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnProximityChannel4Pressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnVolumeUpPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnVolumeDownPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnCode1Pressed(object? sender, PointerPressedEventArgs e)
	{
		// Toggle Code 1 on or off.
		_viewModel.ToggleAlertCode(AlertCodeMode.Code1);
	}

	private void OnCode2Pressed(object? sender, PointerPressedEventArgs e)
	{
		// Toggle Code 2 on or off.
		_viewModel.ToggleAlertCode(AlertCodeMode.Code2);
	}

	private void OnCode3Pressed(object? sender, PointerPressedEventArgs e)
	{
		// Toggle Code 3 on or off.
		_viewModel.ToggleAlertCode(AlertCodeMode.Code3);
	}

	private void OnDirectionalLeftPressed(object? sender, PointerPressedEventArgs e)
	{
		// Toggle the left directional on or off.
		_viewModel.ToggleDirectional(DirectionalMode.Left);
	}

	private void OnDirectionalCenterOutPressed(object? sender, PointerPressedEventArgs e)
	{
		// Toggle the center-out directional on or off.
		_viewModel.ToggleDirectional(DirectionalMode.CenterOut);
	}

	private void OnDirectionalRightPressed(object? sender, PointerPressedEventArgs e)
	{
		// Toggle the right directional on or off.
		_viewModel.ToggleDirectional(DirectionalMode.Right);
	}

	private void OnLightsAndSirensOffPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnExternalAudioPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnMemoRecordPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnAmFmMutePressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.ToggleAmFmMute();
	}

	private void OnFm1SourcePressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAuxiliarySource(AuxiliaryAudioSourceMode.Fm1);
	}

	private void OnAm1SourcePressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAuxiliarySource(AuxiliaryAudioSourceMode.Am1);
	}

	private void OnBluetoothSourcePressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAuxiliarySource(AuxiliaryAudioSourceMode.Bluetooth);
	}

	private void OnInternetSourcePressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAuxiliarySource(AuxiliaryAudioSourceMode.InternetRadio);
	}

	private void OnAmFmTuneUpPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.StepAmFmTuneUp();
	}

	private void OnAmFmTuneDownPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.StepAmFmTuneDown();
	}

	private void OnAmFmSeekUpPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SeekAmFmUp();
	}

	private void OnAmFmSeekDownPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SeekAmFmDown();
	}

	private void OnAmFmScanPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.ScanAmFm();
	}

	private void OnAmFmChannelListPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.ToggleInternetChannelList();
	}

	private void OnAmFmChannelSetPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.StoreCurrentAmFmChannel();
	}

	private void OnInternetGenreFilterPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.CycleInternetGenreFilter();
	}

	private void OnInternetLanguageFilterPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.CycleInternetLanguageFilter();
	}

	private void OnInternetStationsUpPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.ScrollInternetStationsUp();
	}

	private void OnInternetStationsDownPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.ScrollInternetStationsDown();
	}

	private void OnCloseInternetChannelListPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.CloseInternetChannelList();
	}

	private void OnInternetStationPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Border { Tag: string streamUrl } stationItem || string.IsNullOrWhiteSpace(streamUrl))
		{
			return;
		}

		_viewModel.SelectInternetStation(streamUrl);
	}

	private void OnAmFmVolumeUpPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.IncreaseAmFmVolume();
	}

	private void OnAmFmVolumeDownPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.DecreaseAmFmVolume();
	}

	private void OnAmFmPreset1Pressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAmFmPreset(0);
	}

	private void OnAmFmPreset2Pressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAmFmPreset(1);
	}

	private void OnAmFmPreset3Pressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAmFmPreset(2);
	}

	private void OnAmFmPreset4Pressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAmFmPreset(3);
	}

	private void OnAmFmPreset5Pressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAmFmPreset(4);
	}

	private void OnAmFmPreset6Pressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.SelectAmFmPreset(5);
	}

	private void OnCloseAdminPressed(object? sender, PointerPressedEventArgs e)
	{
		_speedTapTimes.Clear();
		_viewModel.CloseAdminOverlay();
	}

	private void OnAdminSystemPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.System);
	}

	private void OnAdminSystemStatusPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.SystemStatus);
	}

	private void OnAdminAudioPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.Audio);
	}

	private void OnAdminRadioPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.Radio);
	}

	private void OnAdminNetworkPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.Network);
	}

	private void OnAdminSecurityPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.Security);
	}

	private void OnAdminIntegrationsPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.Integrations);
	}

	private void OnAdminDiagnosticsPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.Diagnostics);
	}

	private void OnAdminPinDigitPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Border { Tag: string digitText } || digitText.Length != 1)
		{
			return;
		}

		_viewModel.AppendAdminPinDigit(digitText[0]);
	}

	private void OnAdminPinBackspacePressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.BackspaceAdminPin();
	}

	private void OnAdminPinClearPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.ClearAdminPin();
	}

	private void OnAdminAudioOutputSpeakerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Border { Tag: string deviceId } || string.IsNullOrWhiteSpace(deviceId))
		{
			return;
		}

		_viewModel.SelectAdminOutputSpeaker(deviceId);
	}

	private void OnAdminPushAudioConfigPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.PushAdminAudioConfig();
	}

	private void SelectTab(MainConsoleTab tab)
	{
		_viewModel.SelectTab(tab);
	}

	private void SelectAdminSection(AdminSection section)
	{
		_viewModel.SelectAdminSection(section);
	}
}