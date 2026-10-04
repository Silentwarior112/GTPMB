namespace GTPMB;

/// <summary>
/// GTGPB.py show_log_popup: a resizable, non-modal window showing a read-only monospace log with Copy / Close
/// buttons. It also stands in for the Python tool's console as the place where per-file details end up.
/// </summary>
internal sealed class LogDialog : OwnedDialog
{
    private const int InitialColumns = 110;
    private const int InitialRows = 36;

    private readonly Font _logFont = new("Consolas", 10F);

    private LogDialog(Form owner, string title, string text) : base(owner, title)
    {
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        Icon = owner.Icon;
        MinimumSize = new Size(360, 240);

        string content = text.ReplaceLineEndings("\r\n"); // an edit control only breaks lines at CR LF

        var log = new TextBox
        {
            Text = content,
            Font = _logFont,
            Multiline = true,
            ReadOnly = true, // selecting / Ctrl+C still works
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BackColor = SystemColors.Window,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };

        var copy = new Button { Text = "Copy to clipboard", AutoSize = true, MinimumSize = new Size(80, 25), Margin = new Padding(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            if (content.Length > 0)
                Clipboard.SetText(content);
        };

        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(80, 25), Margin = Padding.Empty };
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(copy);
        buttons.Controls.Add(close);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(log, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);

        CancelButton = close;  // Esc
        ActiveControl = close; // focusing the text box first would select the whole log

        FinishLayout();

        // Initial size: room for InitialColumns x InitialRows characters of log, but never larger than the screen.
        // An edit control draws with the font's pixel size rounded to nearest while TextRenderer rounds a point size
        // up (8x17 px cells instead of the real 7x15 at 96 DPI), so the cell is measured with a pre-rounded copy.
        using var cellFont = new Font(_logFont.FontFamily, MathF.Round(_logFont.SizeInPoints * DeviceDpi / 72F), GraphicsUnit.Pixel);
        Size cell = TextRenderer.MeasureText("0", cellFont, Size.Empty, TextFormatFlags.NoPadding);
        var wanted = new Size(
            cell.Width * InitialColumns + SystemInformation.VerticalScrollBarWidth + 12, // + borders and text margins
            cell.Height * InitialRows + SystemInformation.HorizontalScrollBarHeight + 8);
        ClientSize += wanted - log.Size;

        Rectangle screen = Screen.FromControl(owner).WorkingArea;
        Size = new Size(Math.Min(Width, screen.Width), Math.Min(Height, screen.Height));
    }

    /// <summary>Opens a log window over <paramref name="owner"/> and returns at once; the window disposes itself when closed.</summary>
    public static void Open(Form owner, string title, string text) => new LogDialog(owner, title, text).Show();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _logFont.Dispose();
        base.Dispose(disposing);
    }
}
