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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Providers;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.UI.Avalonia;
using Mapsui.Utilities;
using NetTopologySuite.Geometries;
using MyForce.Models;
using MyForce.Services;
using MyForce.ViewModels;

namespace MyForce;

public partial class MainWindow : Window
{
	private const int AdminTapThreshold = 5;

	private const int RadarFollowResolutionIndex = 16;

	private static readonly TimeSpan AdminTapWindow = TimeSpan.FromSeconds(20);

	private readonly MainWindowViewModel _viewModel;

	private readonly MemoryLayer _vehicleLayer = new() { Name = "Vehicle" };

	private readonly MemoryLayer _alertLayer = new() { Name = "Alerts" };

	private readonly WeatherAlertService _weatherAlertService = new();

	private readonly Queue<DateTime> _speedTapTimes = new();

	private MapControl? _radarMapControl;

	private CancellationTokenSource? _alertRefreshCts;

	public MainWindow()
	{
		InitializeComponent();
		_viewModel = new MainWindowViewModel();
		DataContext = _viewModel;
		InitializeRadarMap();
	}

	protected override void OnOpened(EventArgs e)
	{
		base.OnOpened(e);

		// WindowState.FullScreen is unreliable on some displays/drivers when
		// SystemDecorations is None: the window stays at its natural size centered on
		// the desktop, which shows through as black bars on all sides. Instead, pin the
		// borderless window manually to the screen's full bounds.
		Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
		if (screen is null)
		{
			WindowState = WindowState.FullScreen;
			return;
		}

		// screen.Bounds is in physical pixels; Position is physical, but Width/Height
		// are logical (DIP) units, so divide by the screen's scaling factor.
		WindowState = WindowState.Normal;
		PixelRect bounds = screen.Bounds;
		Position = bounds.Position;
		Width = bounds.Width / screen.Scaling;
		Height = bounds.Height / screen.Scaling;
	}

	protected override void OnClosed(System.EventArgs e)
	{
		_alertRefreshCts?.Cancel();
		_alertRefreshCts?.Dispose();

		if (_viewModel is not null)
		{
			_viewModel.PropertyChanged -= OnViewModelPropertyChanged;
		}

		_viewModel.Dispose();
		base.OnClosed(e);
	}

	private void InitializeRadarMap()
	{
		ContentControl? radarMapHost = this.FindControl<ContentControl>("RadarMapHost");
		if (radarMapHost is null)
		{
			return;
		}

		Map map = new();
		map.Layers.Add(OpenStreetMap.CreateTileLayer());
		map.Layers.Add(_alertLayer);
		map.Layers.Add(_vehicleLayer);

		_radarMapControl = new MapControl
		{
			Map = map,
			UseContinuousMouseWheelZoom = true,
			ContinuousMouseWheelZoomStepSize = 0.5,
			UseFling = false,
		};

		radarMapHost.Content = _radarMapControl;
		_viewModel.PropertyChanged += OnViewModelPropertyChanged;
		UpdateRadarMapLocation();
		_ = UpdateRadarMapAlertsAsync();
	}

	private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.LocationLatitude), StringComparison.Ordinal)
			|| string.Equals(e.PropertyName, nameof(MainWindowViewModel.LocationLongitude), StringComparison.Ordinal)
			|| string.Equals(e.PropertyName, nameof(MainWindowViewModel.VehicleHeadingDegrees), StringComparison.Ordinal))
		{
			UpdateRadarMapLocation();
			_ = UpdateRadarMapAlertsAsync();
			return;
		}

		if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.IsRadarFollowEnabled), StringComparison.Ordinal))
		{
			UpdateRadarMapLocation();
		}
	}

	private void UpdateRadarMapLocation()
	{
		if (_radarMapControl?.Map is null)
		{
			return;
		}

		double longitude = _viewModel.LocationLongitude;
		double latitude = _viewModel.LocationLatitude;
		(double x, double y) = SphericalMercator.FromLonLat(longitude, latitude);
		MPoint sphericalMercator = new(x, y);

		PointFeature vehicleFeature = new(sphericalMercator)
		{
			Styles =
			[
				new SymbolStyle
				{
					SymbolType = SymbolType.Triangle,
					Fill = new Brush(Color.FromArgb(255, 77, 225, 255)),
					Outline = new Pen { Color = Color.White, Width = 2 },
					SymbolScale = 0.9,
					SymbolRotation = _viewModel.VehicleHeadingDegrees,
				},
			],
		};

		_vehicleLayer.Features = [vehicleFeature];
		if (_viewModel.IsRadarFollowEnabled)
		{
			// Start follow mode at a closer default zoom so the vehicle area is easier to read.
			IReadOnlyList<double> resolutions = _radarMapControl.Map.Navigator.Resolutions;
			int resolutionIndex = Math.Min(RadarFollowResolutionIndex, resolutions.Count - 1);
			_radarMapControl.Map.Navigator.CenterOnAndZoomTo(sphericalMercator, resolutions[resolutionIndex]);
		}

		_radarMapControl.Refresh();
	}

	private async Task UpdateRadarMapAlertsAsync()
	{
		if (_radarMapControl?.Map is null)
		{
			return;
		}

		_alertRefreshCts?.Cancel();
		_alertRefreshCts?.Dispose();
		_alertRefreshCts = new CancellationTokenSource();
		CancellationToken cancellationToken = _alertRefreshCts.Token;

		try
		{
			IReadOnlyList<WeatherAlertPolygon> polygons = await _weatherAlertService
				.GetActiveAlertsAsync(new GeoCoordinate(_viewModel.LocationLatitude, _viewModel.LocationLongitude), cancellationToken)
				.ConfigureAwait(false);

			IReadOnlyList<IFeature> features = polygons
				.Select(CreateAlertFeature)
				.Where(feature => feature is not null)
				.Cast<IFeature>()
				.ToArray();

			await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
			{
				_alertLayer.Features = features;
				_radarMapControl?.Refresh();
			});
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception)
		{
			await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
			{
				_alertLayer.Features = [];
				_radarMapControl?.Refresh();
			});
		}
	}

	private static IFeature? CreateAlertFeature(WeatherAlertPolygon polygon)
	{
		if (polygon.Coordinates.Count < 3)
		{
			return null;
		}

		Coordinate[] projectedCoordinates = polygon.Coordinates
			.Select(coordinate =>
			{
				(double x, double y) = SphericalMercator.FromLonLat(coordinate.Longitude, coordinate.Latitude);
				return new Coordinate(x, y);
			})
			.ToArray();

		if (!projectedCoordinates[0].Equals2D(projectedCoordinates[^1]))
		{
			Array.Resize(ref projectedCoordinates, projectedCoordinates.Length + 1);
			projectedCoordinates[^1] = projectedCoordinates[0];
		}

		GeometryFeature feature = new()
		{
			Geometry = new Polygon(new LinearRing(projectedCoordinates)),
			Styles =
			[
				new VectorStyle
				{
					Fill = new Brush(Color.FromArgb(46, GetAlertColor(polygon.Severity).R, GetAlertColor(polygon.Severity).G, GetAlertColor(polygon.Severity).B)),
					Outline = new Pen { Color = GetAlertColor(polygon.Severity), Width = 2 },
				},
			],
		};

		feature["event"] = polygon.EventName;
		return feature;
	}

	private static Color GetAlertColor(string severity)
	{
		return severity.ToUpperInvariant() switch
		{
			"EXTREME" => Color.FromArgb(255, 255, 59, 48),
			"SEVERE" => Color.FromArgb(255, 255, 149, 0),
			"MODERATE" => Color.FromArgb(255, 255, 214, 10),
			_ => Color.FromArgb(255, 77, 225, 255),
		};
	}

	private void OnRadarFollowPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.ToggleRadarFollow();
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
		=> _viewModel.TriggerCameraRecord();

	private void OnCameraStopPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.TriggerCameraStop();

	private void OnCameraAutoZoomPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.TriggerCameraAutoZoom();

	// L/S page scene lights (latching toggles on the Siren Interface Controller).
	private void OnLeftAlleyPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.ToggleLeftAlley();

	private void OnTakeDownPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.ToggleTakeDown();

	private void OnRightAlleyPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.ToggleRightAlley();

	// L/S page air horn: momentary, sounds while held.
	private void OnAirHornPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.AirHornDown();

	private void OnAirHornReleased(object? sender, PointerReleasedEventArgs e)
		=> _viewModel.AirHornUp();

	// RADIO page: SELECT opens the radio picker; CHANNELS opens the channel page.
	private void OnRadioSelectPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.OpenRadioSelection();

	private void OnRadioChannelsPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.OpenChannelSelection();

	private void OnRadioVolumeUpPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.RadioVolumeUp();

	private void OnRadioVolumeDownPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.RadioVolumeDown();

	// Radio-selection overlay: pick a radio row, or close the overlay.
	private void OnRadioSelectionItemPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is Control { DataContext: RadioSelectionItem item })
		{
			item.Select();
		}
	}

	private void OnRadioSelectionClosePressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.CloseRadioSelection();

	private void OnChannelSelectionClosePressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.CloseChannelSelection();

	// RADIO page secondary controls. These depend on per-radio plugin support (channel
	// stepping, scan, geo-area, nuisance delete, ext audio) and are placeholders for now.
	private void OnRadioListenPressed(object? sender, PointerPressedEventArgs e) { }

	private void OnRadioTalkPressed(object? sender, PointerPressedEventArgs e) { }

	private void OnRadioChannelUpPressed(object? sender, PointerPressedEventArgs e) { }

	private void OnRadioChannelDownPressed(object? sender, PointerPressedEventArgs e) { }

	// GEO AREA: open the channel-centers overlay for the selected radio.
	private void OnRadioGeoAreaPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.OpenGeoArea();

	private void OnGeoAreaClosePressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.CloseGeoArea();

	private void OnGeoAreaSetHerePressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.SetCurrentChannelCenterHere();

	private void OnGeoAreaCenterClearPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is Control { DataContext: ChannelCenterListItem item })
		{
			item.Clear();
		}
	}

	// Module function button (§3.10, v2.8): publish the press for the selected radio.
	private void OnRadioFunctionButtonPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is Control { DataContext: RadioFunctionButton button })
		{
			button.Press();
		}
	}

	// Admin Radio config screen (§4.4): add a radio of the pressed type, or remove/rename an existing one.
	private void OnAddRadioTypePressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is Control { DataContext: RadioRegistryEntryMessage type })
		{
			_viewModel.AddRadio(type.TypeId, type.DisplayName);
		}
	}

	private void OnRemoveRadioPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is Control { DataContext: RadioAdminItem radio })
		{
			radio.Remove();
		}
	}

	private void OnSetRadioAliasPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is Control { DataContext: RadioAdminItem radio })
		{
			radio.SaveAlias();
		}
	}

	private void OnRadioNuisPressed(object? sender, PointerPressedEventArgs e) { }

	// EXT AUDIO (radio page + patrol): toggle the siren ext_audio relay (pulses green).
	private void OnRadioExtAudioPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.ToggleExtAudio();

	private void OnExtAudioTogglePressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.ToggleExtAudio();

	// SCAN (radio page + patrol ACTIVE RADIO/SCAN): toggle scan on the selected radio (glows orange).
	private void OnRadioScanPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.ToggleRadioScan();

	private void OnRadioScanTogglePressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.ToggleRadioScan();

	private void OnRadioPresetPressed(object? sender, PointerPressedEventArgs e) { }

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
	{
		// PATROL screen volume buttons drive the master output volume.
		_viewModel.IncreaseMasterVolume();
	}

	private void OnVolumeDownPressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.DecreaseMasterVolume();
	}

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

	// Soft keys 1-6 under the directionals: route touch presses through the same trigger the HCD uses.
	private void OnLightsAndSirensOffPressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.TriggerSoftKey(1);

	private void OnSoftKey5Pressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.TriggerSoftKey(5);

	private void OnSoftKey6Pressed(object? sender, PointerPressedEventArgs e)
		=> _viewModel.TriggerSoftKey(6);

	private void OnAmFmMutePressed(object? sender, PointerPressedEventArgs e)
	{
		_viewModel.TriggerSoftKey(4);
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

	private void OnAdminIntegrationsWhat3WordsPressed(object? sender, PointerPressedEventArgs e)
	{
		SelectAdminSection(AdminSection.IntegrationsWhat3Words);
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

	private async void OnAdminSaveWhat3WordsApiKeyPressed(object? sender, PointerPressedEventArgs e)
	{
		await _viewModel.SaveAdminWhat3WordsApiKeyAsync();
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