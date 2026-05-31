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
	{ }

	private void OnCloseAdminPressed(object? sender, PointerPressedEventArgs e)
	{
		_speedTapTimes.Clear();
		_viewModel.CloseAdminOverlay();
	}

	private void OnAdminSystemPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.System);
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

	private void SelectTab(MainConsoleTab tab)
	{
		_viewModel.SelectTab(tab);
	}

	private void SelectAdminSection(AdminSection section)
	{
		_viewModel.SelectAdminSection(section);
	}
}