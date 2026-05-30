namespace MyForceUIMockUp
{
	public partial class Form1 : Form
	{
		public Form1()
		{
			InitializeComponent();
			DoubleBuffered = true;
           WireNavigationButtons();
			ApplyConsoleButtonStyles();
		}

		private void WireNavigationButtons()
		{
			btnPatrol.Click += (_, _) => SelectConsoleTab(tabPagePatrol);
			btnLsMode.Click += (_, _) => SelectConsoleTab(tabPageLightsSiren);
			btnRadioMode.Click += (_, _) => SelectConsoleTab(tabPageRadio);
			btnRadarMode.Click += (_, _) => SelectConsoleTab(tabPageRadar);
			btnAmFmMode.Click += (_, _) => SelectConsoleTab(tabPageAmFm);
			btnCadMode.Click += (_, _) => SelectConsoleTab(tabPageCad);
			btnCameraMode.Click += (_, _) => SelectConsoleTab(tabPageCamera);

			SelectConsoleTab(tabPagePatrol);
		}

		private void SelectConsoleTab(TabPage tabPage)
		{
			ArgumentNullException.ThrowIfNull(tabPage);

			consoleTabControl.SelectedTab = tabPage;
			UpdateTopButtonState(tabPage);
		}

		private void UpdateTopButtonState(TabPage activeTab)
		{
			ArgumentNullException.ThrowIfNull(activeTab);

			SetTopButtonState(btnPatrol, activeTab == tabPagePatrol);
			SetTopButtonState(btnLsMode, activeTab == tabPageLightsSiren);
			SetTopButtonState(btnRadioMode, activeTab == tabPageRadio);
			SetTopButtonState(btnRadarMode, activeTab == tabPageRadar);
			SetTopButtonState(btnAmFmMode, activeTab == tabPageAmFm);
			SetTopButtonState(btnCadMode, activeTab == tabPageCad);
			SetTopButtonState(btnCameraMode, activeTab == tabPageCamera);
		}

		private static void SetTopButtonState(Button button, bool isActive)
		{
			ArgumentNullException.ThrowIfNull(button);

			button.BackColor = isActive ? Color.FromArgb(43, 113, 187) : Color.FromArgb(58, 58, 58);
			button.FlatAppearance.BorderColor = isActive ? Color.FromArgb(126, 180, 235) : Color.FromArgb(96, 96, 96);
		}

		private void ApplyConsoleButtonStyles()
		{
			Button[] navButtons =
			[
				btnPatrol,
				btnLsMode,
				btnRadioMode,
				btnRadarMode,
				btnAmFmMode,
				btnCadMode,
				btnCameraMode
			];

			foreach (Button button in navButtons)
			{
               StyleButton(button, Color.FromArgb(58, 58, 58), 11.5F);
			}

          Button[] compactButtons =
			[
				btnPri,
				btnTac,
				btnScan,
				btnRec,
				btnStop,
				btnAutoz,
               btnAlert2,
				btnAlert3
			];

			foreach (Button button in compactButtons)
			{
				StyleButton(button, Color.FromArgb(42, 42, 42), 10.5F);
			}

			Button[] mediumButtons =
			[
				btnPrecinct1,
				btnPrecinct6,
				btnPrecinct7,
				btnPrecinct9,
				btnDirectionalLeft,
				btnDirectionalCenter,
				btnLsOff,
				btnExtAudio,
				btnMemo,
				btnMute
			];

          foreach (Button button in mediumButtons)
			{
               StyleButton(button, Color.FromArgb(42, 42, 42), 9.5F);
			}

          StyleButton(btnVolUp, Color.FromArgb(42, 42, 42), 22F);
			StyleButton(btnVolDown, Color.FromArgb(42, 42, 42), 22F);
			StyleButton(btnAlert1, Color.FromArgb(43, 113, 187), 12F);
			StyleButton(btnDirectionalRight, Color.FromArgb(43, 113, 187), 22F);
			StyleButton(btnE911, Color.FromArgb(150, 48, 42), 14F);

          btnDirectionalLeft.Font = new Font("Segoe UI", 22F, FontStyle.Bold, GraphicsUnit.Point);
			btnDirectionalCenter.Font = new Font("Segoe UI", 18F, FontStyle.Bold, GraphicsUnit.Point);
			btnDirectionalRight.Font = new Font("Segoe UI", 22F, FontStyle.Bold, GraphicsUnit.Point);
		}

		private static void StyleButton(Button button, Color backColor, float fontSize)
		{
			ArgumentNullException.ThrowIfNull(button);

			button.BackColor = backColor;
          button.Dock = DockStyle.Fill;
			button.FlatStyle = FlatStyle.Flat;
			button.FlatAppearance.BorderColor = Color.FromArgb(96, 96, 96);
			button.FlatAppearance.BorderSize = 2;
			button.FlatAppearance.MouseDownBackColor = backColor;
			button.FlatAppearance.MouseOverBackColor = backColor;
			button.ForeColor = Color.White;
			button.Font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
         button.Margin = new Padding(4);
			button.Padding = new Padding(0);
         button.TextAlign = ContentAlignment.MiddleCenter;
			button.UseVisualStyleBackColor = false;
		}
	}
}
