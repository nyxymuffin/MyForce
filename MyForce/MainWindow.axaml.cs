using System;
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

	protected override void OnClosed(EventArgs e)
	{
		_viewModel.Dispose();
		base.OnClosed(e);
	}

	private void OnEmergencyExitPressed(object? sender, PointerPressedEventArgs e)
	{
		if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.Shutdown();
			return;
		}

		Close();
	}
}
