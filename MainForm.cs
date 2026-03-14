using System.Runtime.InteropServices;

namespace SetDisplayZoom;

public sealed class MainForm : Form
{
    // ---------------------------------------------------------------------------
    // Resolution options (Width x Height)
    // ---------------------------------------------------------------------------
    private readonly List<(int Width, int Height, string Label)> _resolutionOptions = [];

    // ---------------------------------------------------------------------------
    // Win32
    // ---------------------------------------------------------------------------
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(
        string? lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int ChangeDisplaySettings(
        ref DEVMODE lpDevMode,
        int dwflags);

    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    // ---------------------------------------------------------------------------
    // Controls
    // ---------------------------------------------------------------------------
    private readonly ComboBox _comboResolution;
    private readonly Button _btnSet;
    private readonly Button _btnCancel;
    private readonly NotifyIcon _trayIcon;

    // ---------------------------------------------------------------------------
    // Constructor
    // ---------------------------------------------------------------------------
    public MainForm()
    {
        // Form properties
        Text = "SetDisplay";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 100);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        BackColor = SystemColors.Control;

        var lblResolution = new Label
        {
            Text = "Resolution:",
            Location = new Point(14, 20),
            AutoSize = true,
        };

        _comboResolution = new ComboBox
        {
            Location = new Point(100, 16),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.System,
        };

        LoadResolutionOptions();
        foreach (var (_, _, label) in _resolutionOptions)
            _comboResolution.Items.Add(label);

        _btnSet = new Button
        {
            Text = "Apply",
            Location = new Point(128, 56),
            Width = 80,
            Height = 28,
        };
        _btnSet.Click += BtnSet_Click;

        _btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(220, 56),
            Width = 80,
            Height = 28,
        };
        _btnCancel.Click += BtnCancel_Click;

        Controls.AddRange([lblResolution, _comboResolution, _btnSet, _btnCancel]);

        // System tray
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => ShowForm());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Text = "SetDisplay - Resolution",
            Icon = BuildTrayIcon(),
            ContextMenuStrip = trayMenu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowForm();

        SelectCurrentResolution();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------
    private static DEVMODE CreateDevMode()
    {
        return new DEVMODE
        {
            dmSize = (short)Marshal.SizeOf<DEVMODE>()
        };
    }

    private void LoadResolutionOptions()
    {
        var seen = new HashSet<(int Width, int Height)>();
        int mode = 0;

        while (true)
        {
            var devMode = CreateDevMode();
            if (!EnumDisplaySettings(null, mode, ref devMode))
                break;

            var key = (devMode.dmPelsWidth, devMode.dmPelsHeight);
            if (devMode.dmPelsWidth > 0 && devMode.dmPelsHeight > 0 && seen.Add(key))
            {
                _resolutionOptions.Add((
                    devMode.dmPelsWidth,
                    devMode.dmPelsHeight,
                    $"{devMode.dmPelsWidth} x {devMode.dmPelsHeight}"));
            }

            mode++;
        }

        _resolutionOptions.Sort((a, b) =>
        {
            int areaCompare = (b.Width * b.Height).CompareTo(a.Width * a.Height);
            if (areaCompare != 0)
                return areaCompare;
            return b.Width.CompareTo(a.Width);
        });

        if (_resolutionOptions.Count == 0)
        {
            // Defensive fallback for drivers that do not enumerate modes correctly.
            _resolutionOptions.Add((3840, 2160, "3840 x 2160"));
            _resolutionOptions.Add((2560, 1440, "2560 x 1440"));
            _resolutionOptions.Add((1920, 1080, "1920 x 1080"));
            _resolutionOptions.Add((1600, 900, "1600 x 900"));
            _resolutionOptions.Add((1366, 768, "1366 x 768"));
            _resolutionOptions.Add((1280, 720, "1280 x 720"));
        }
    }

    private void SelectCurrentResolution()
    {
        var current = CreateDevMode();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref current))
        {
            _comboResolution.SelectedIndex = 0;
            return;
        }

        for (int i = 0; i < _resolutionOptions.Count; i++)
        {
            var option = _resolutionOptions[i];
            if (option.Width == current.dmPelsWidth && option.Height == current.dmPelsHeight)
            {
                _comboResolution.SelectedIndex = i;
                return;
            }
        }

        _comboResolution.SelectedIndex = 0;
    }

    private void ApplyResolution(int width, int height, string label)
    {
        var devMode = CreateDevMode();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode))
        {
            MessageBox.Show(
                "Could not read current display mode.",
                "SetDisplay", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        devMode.dmPelsWidth = width;
        devMode.dmPelsHeight = height;
        devMode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;

        int result = ChangeDisplaySettings(ref devMode, 0);
        if (result == DISP_CHANGE_SUCCESSFUL)
        {
            SendMessageTimeout(
                HWND_BROADCAST, WM_SETTINGCHANGE,
                UIntPtr.Zero, "WindowMetrics",
                SMTO_ABORTIFHUNG, 5000, out _);

            MessageBox.Show(
                $"Resolution changed to {label}.",
                "SetDisplay", MessageBoxButtons.OK, MessageBoxIcon.Information);

            Hide();
            return;
        }

        MessageBox.Show(
            $"Windows rejected display mode {label} (error code: {result}).",
            "SetDisplay", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ShowForm()
    {
        SelectCurrentResolution();
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        Application.Exit();
    }

    private static Icon BuildTrayIcon()
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(Color.FromArgb(30, 120, 210));
            g.FillRectangle(brush, 0, 0, 16, 16);

            using var font = new Font("Arial", 7f, FontStyle.Bold, GraphicsUnit.Point);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("R", font, Brushes.White, new RectangleF(0, 0, 16, 16), sf);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    // ---------------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------------
    private void BtnSet_Click(object? sender, EventArgs e)
    {
        if (_comboResolution.SelectedIndex < 0)
            return;

        var (width, height, label) = _resolutionOptions[_comboResolution.SelectedIndex];
        ApplyResolution(width, height, label);
    }

    private void BtnCancel_Click(object? sender, EventArgs e) => Hide();

    // Intercept the X button: hide to tray instead of closing
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _trayIcon.Visible = false;
            base.OnFormClosing(e);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
