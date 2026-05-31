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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using MyForce.ViewModels;

namespace MyForce;

public partial class MainWindow : Window
{
	private readonly MainWindowViewModel _viewModel;

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
	{ }

	private void OnLightsAndSirensTabPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnRadioTabPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnRadarTabPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnAmFmTabPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnCadTabPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnCameraTabPressed(object? sender, PointerPressedEventArgs e)
	{ }

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
	{ }

	private void OnCode2Pressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnCode3Pressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnDirectionalLeftPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnDirectionalCenterOutPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnDirectionalRightPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnLightsAndSirensOffPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnExternalAudioPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnMemoRecordPressed(object? sender, PointerPressedEventArgs e)
	{ }

	private void OnAmFmMutePressed(object? sender, PointerPressedEventArgs e)
	{ }
}