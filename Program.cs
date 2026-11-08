using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NasdaraMorseDecoder;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        using var icon = new Icon(typeof(Program), "Nasdara.ico");
        using var form = new MainForm { Icon = icon };
        Application.Run(form);
    }
}

internal sealed class MainForm : Form
{
    private static readonly Color Graphite = Color.FromArgb(31, 32, 35);
    private static readonly Color Surface = Color.FromArgb(39, 40, 44);
    private static readonly Color Header = Color.FromArgb(44, 45, 49);
    private static readonly Color Border = Color.FromArgb(53, 55, 59);
    private static readonly Color Ink = Color.FromArgb(233, 234, 235);
    private static readonly Color Muted = Color.FromArgb(168, 173, 180);
    private static readonly Color Amber = Color.FromArgb(229, 172, 85);
    private static readonly Color LiveRow = Color.FromArgb(45, 41, 36);
    private static readonly Color AmberHover = Color.FromArgb(242, 188, 108);
    private static readonly Font ColumnFont = new("Segoe UI Semibold", 10);
    private static readonly Font DataFont = new("Consolas", 11);
    private const TextFormatFlags CellText = TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    private readonly Button start = new() { Text = "Начать прослушивание", AutoSize = true };
    private readonly Button stop = new() { Text = "Остановить", AutoSize = true, Enabled = false };
    private readonly NumericUpDown tone = new() { Maximum = 3500, Value = 980, Increment = 20, Width = 68, TextAlign = HorizontalAlignment.Center };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Text = "Прослушивание остановлено" };
    private readonly Label device = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
        AutoEllipsis = true, AccessibleName = "Устройство вывода", Text = "—" };
    private readonly Panel meter = new() { Dock = DockStyle.Fill, AccessibleName = "Уровень сигнала" };
    private readonly Panel statusDot = new() { Dock = DockStyle.Fill };
    private bool recognizing;
    private readonly ListView results = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = true, Margin = Padding.Empty, Sorting = SortOrder.None, OwnerDraw = true,
        BorderStyle = BorderStyle.None
    };
    private CancellationTokenSource? session;
    private ListViewItem? liveRow;
    private int meterLevel;

    public MainForm()
    {
        Text = "Nasdara Morse Decoder";
        BackColor = Graphite;
        ForeColor = Ink;
        ClientSize = new(760, 410);
        MinimumSize = new(640, 350);
        StartPosition = FormStartPosition.CenterScreen;
        StyleButton(start, "▶");
        StyleButton(stop, "■");
        tone.BackColor = Surface;
        tone.ForeColor = Ink;
        tone.Font = DataFont;
        device.ForeColor = Muted;
        meter.Paint += PaintMeter;
        statusDot.Paint += PaintStatusDot;
        results.BackColor = Graphite;
        results.ForeColor = Ink;
        results.Font = DataFont;
        results.Columns.Add("Координаты", 115);
        results.Columns.Add("Код двери", 105);
        results.Columns.Add("Принято", 150);
        results.ClientSizeChanged += (_, _) => ResizeColumns();
        results.DrawColumnHeader += PaintColumn;
        results.DrawSubItem += PaintCell;
        Shown += (_, _) => ResizeColumns();
        start.Click += Start;
        stop.Click += (_, _) => session?.Cancel();

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill,
            WrapContents = false, Margin = Padding.Empty };
        actions.Controls.AddRange([start, stop]);

        var tuning = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right,
            WrapContents = false, Margin = Padding.Empty };
        tuning.Controls.AddRange([new Label { Text = "Тон, Гц (0 — авто)", AutoSize = true,
            ForeColor = Muted, Margin = new(0, 7, 8, 0) }, tone]);
        tone.Margin = new(0, 3, 3, 3);

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = new(13, 9, 13, 9) };
        toolbar.ColumnStyles.Add(new(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new(SizeType.AutoSize));
        toolbar.Controls.Add(actions, 0, 0);
        toolbar.Controls.Add(tuning, 1, 0);

        var signal = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
            BackColor = Surface, Margin = new(0, 0, 0, 8), Padding = new(16, 0, 16, 0) };
        signal.RowStyles.Add(new(SizeType.Percent, 100));
        signal.ColumnStyles.Add(new(SizeType.Absolute, 17));
        signal.ColumnStyles.Add(new(SizeType.Percent, 49));
        signal.ColumnStyles.Add(new(SizeType.Percent, 23));
        signal.ColumnStyles.Add(new(SizeType.Percent, 28));
        statusDot.Margin = status.Margin = device.Margin = Padding.Empty;
        meter.Margin = new(6, 0, 12, 0);
        signal.Controls.Add(statusDot, 0, 0);
        signal.Controls.Add(status, 1, 0);
        signal.Controls.Add(meter, 2, 0);
        signal.Controls.Add(device, 3, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 54));
        layout.RowStyles.Add(new(SizeType.Absolute, 43));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(signal, 0, 1);
        layout.Controls.Add(results, 0, 2);
        Controls.Add(layout);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        const int captionColor = 35;
        const int captionTextColor = 36;
        int background = ColorTranslator.ToWin32(Graphite);
        int foreground = ColorTranslator.ToWin32(Ink);
        DwmSetWindowAttribute(Handle, captionColor, ref background, sizeof(int));
        DwmSetWindowAttribute(Handle, captionTextColor, ref foreground, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private static void StyleButton(Button button, string glyph)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Padding = new(22, 5, 12, 5);
        button.MouseEnter += (_, _) => button.Invalidate();
        button.MouseLeave += (_, _) => button.Invalidate();
        button.Paint += (_, e) =>
        {
            bool hover = button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
            var bounds = Rectangle.Inflate(button.ClientRectangle, -1, -1);
            e.Graphics.Clear(Graphite);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(bounds, new Size(6, 6));
            using var fill = new SolidBrush(button.Enabled ? (hover ? AmberHover : Amber) : Surface);
            e.Graphics.FillPath(fill, shape);
            if (button.Focused)
            {
                using var outline = new Pen(Ink);
                e.Graphics.DrawPath(outline, shape);
            }
            TextRenderer.DrawText(e.Graphics, glyph + "  " + button.Text, button.Font, bounds,
                button.Enabled ? Graphite : Muted, CellText | TextFormatFlags.HorizontalCenter);
        };
    }

    private void PaintColumn(object? sender, DrawListViewColumnHeaderEventArgs e) =>
        PaintTableCell(e.Graphics, e.Bounds, e.Header?.Text ?? "", ColumnFont, Header, Ink, false);

    private void PaintCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is not { } item || e.SubItem is not { } subItem)
            return;
        bool active = item == liveRow;
        Color background = item.Selected && results.Focused ? Header :
            active ? LiveRow : Graphite;
        Color text = e.ColumnIndex == 2 ? Muted : Ink;
        PaintTableCell(e.Graphics, e.Bounds, subItem.Text, DataFont, background, text,
            active && e.ColumnIndex == 0);
    }

    private static void PaintTableCell(Graphics graphics, Rectangle bounds, string text,
        Font font, Color background, Color foreground, bool marker)
    {
        using var fill = new SolidBrush(background);
        using var line = new Pen(Border);
        graphics.FillRectangle(fill, bounds);
        graphics.DrawLine(line, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        if (marker)
        {
            using var accent = new SolidBrush(Amber);
            graphics.FillRectangle(accent, bounds.Left, bounds.Top, 3, bounds.Height - 1);
        }
        bounds.Inflate(-12, 0);
        TextRenderer.DrawText(graphics, text, font, bounds, foreground, CellText);
    }

    private void PaintStatusDot(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var dot = new SolidBrush(recognizing ? Amber : Muted);
        e.Graphics.FillEllipse(dot, 1, (statusDot.Height - 7) / 2, 7, 7);
    }

    private void PaintMeter(object? sender, PaintEventArgs e)
    {
        if (meter.Width < 5)
            return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int y = meter.Height / 2;
        int width = meter.Width - 4;
        using var stroke = new Pen(Border, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(stroke, 2, y, 2 + width, y);
        if (meterLevel == 0)
            return;
        stroke.Color = Amber;
        e.Graphics.DrawLine(stroke, 2, y, 2 + Math.Max(1, width * meterLevel / 100), y);
    }

    private void SetMeter(int level)
    {
        if (meterLevel == level)
            return;
        meterLevel = level;
        meter.Invalidate();
    }

    private void ResizeColumns()
    {
        int width = results.ClientSize.Width / 3;
        results.Columns[0].Width = width;
        results.Columns[1].Width = width;
        results.AutoResizeColumn(2, ColumnHeaderAutoResizeStyle.HeaderSize);
    }

    private async void Start(object? sender, EventArgs args)
    {
        int frequency = (int)tone.Value;
        if (frequency is > 0 and < 100)
        {
            status.Text = "Укажите 0 для автопоиска или частоту от 100 до 3500 Гц.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        session = cancellation;
        stop.Enabled = true;
        stop.Select();
        start.Enabled = tone.Enabled = false;
        AddLiveRow();
        ShowAudio(new("Подключение…", "", 0));
        var updates = new Progress<AudioStatus>(value =>
        {
            if (!IsDisposed && session == cancellation)
                ShowAudio(value);
        });
        string finalState = "Прослушивание остановлено";
        try
        {
            await Task.Run(() => Loopback.Listen(frequency, ((IProgress<AudioStatus>)updates).Report,
                cancellation.Token));
        }
        catch (Exception error)
        {
            finalState = $"Ошибка захвата · {error.Message}";
        }
        finally
        {
            if (!IsDisposed)
            {
                liveRow?.Remove();
                liveRow = null;
                status.Text = finalState;
                recognizing = false;
                statusDot.Invalidate();
                SetMeter(0);
                start.Enabled = tone.Enabled = true;
                if (stop.Focused)
                    start.Select();
                stop.Enabled = false;
            }
            session = null;
        }
    }

    private void ShowAudio(AudioStatus audio)
    {
        status.Text = audio.State;
        device.Text = audio.Device.Length == 0 ? "—" : audio.Device;
        bool hasSignal = audio.State.StartsWith("Распознавание", StringComparison.Ordinal);
        if (recognizing != hasSignal)
        {
            recognizing = hasSignal;
            statusDot.Invalidate();
        }
        SetMeter((int)Math.Clamp(Math.Sqrt(audio.Level) * 100, 0, 100));
        if (audio.Completed is Bunker bunker && liveRow != null)
        {
            liveRow.Text = bunker.Grid;
            liveRow.SubItems[1].Text = bunker.Code;
            liveRow.SubItems[2].Text = bunker.Received.ToString("dd.MM.yyyy HH:mm:ss");
            AddLiveRow();
        }
        if (liveRow != null)
        {
            string digits = audio.PartialDigits.PadRight(10, '*');
            liveRow.Text = digits[..3] + " " + digits[3..6];
            liveRow.SubItems[1].Text = digits[6..];
            liveRow.SubItems[2].Text = audio.LastDigitReceivedAt?.ToString("dd.MM.yyyy HH:mm:ss") ?? "";
        }
    }

    private void AddLiveRow()
    {
        var previous = liveRow;
        liveRow = results.Items.Insert(results.Items.Count, new ListViewItem(["*** ***", "****", ""]));
        liveRow.EnsureVisible();
        if (previous != null)
            results.Invalidate(previous.Bounds);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        session?.Cancel();
        base.OnFormClosed(e);
    }
}
