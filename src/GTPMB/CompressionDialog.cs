using GTPMB.Core.Pmb;

namespace GTPMB;

/// <summary>
/// The per-file compression list for "Update PMB file entries": one checkbox per content file,
/// ticked = gzip-wrapped in the rebuilt PMB. Defaults come from the folder's config; files the
/// config does not know start unticked.
/// </summary>
internal sealed class CompressionDialog : OwnedDialog
{
    private readonly CheckedListBox _list = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false,
        HorizontalScrollbar = true,
    };

    private CompressionDialog(Form owner, List<ScannedEntry> entries) : base(owner, "Update PMB file entries")
    {
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        ClientSize = new Size(460, 420);
        MinimumSize = new Size(360, 280);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = "Ticked files are gzip-wrapped in the rebuilt PMB (how the game ships almost everything).\n"
                + "Defaults follow the folder's config; files the config does not know start unticked.",
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(0, 0, 0, 8),
        });

        foreach (ScannedEntry entry in entries)
            _list.Items.Add(entry.FileName + (entry.Known ? "" : "   (new)"), entry.Compressed);
        layout.Controls.Add(_list);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "Generate", DialogResult = DialogResult.OK, AutoSize = true };
        var allOff = new Button { Text = "All off", AutoSize = true };
        var allOn = new Button { Text = "All on", AutoSize = true };
        allOn.Click += (_, _) => SetAll(true);
        allOff.Click += (_, _) => SetAll(false);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(allOff);
        buttons.Controls.Add(allOn);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        FinishLayout();
    }

    private void SetAll(bool value)
    {
        for (int i = 0; i < _list.Items.Count; i++)
            _list.SetItemChecked(i, value);
    }

    /// <summary>Shows the list; null = cancelled, else the file-name -> compress choices.</summary>
    public static Dictionary<string, bool>? Ask(Form owner, List<ScannedEntry> entries)
    {
        using var dialog = new CompressionDialog(owner, entries);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return null;
        var choices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
            choices[entries[i].FileName] = dialog._list.GetItemChecked(i);
        return choices;
    }
}

/// <summary>A one-checkbox confirmation, used by "Build menu source tree".</summary>
internal sealed class CheckOptionDialog : OwnedDialog
{
    private readonly CheckBox _option;

    private CheckOptionDialog(Form owner, string title, string message, string optionText, bool optionDefault)
        : base(owner, title)
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(12),
        };
        layout.Controls.Add(new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 0, 0, 10),
        });
        _option = new CheckBox
        {
            Text = optionText,
            Checked = optionDefault,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(_option);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "Build", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        FinishLayout();
    }

    /// <summary>Shows the prompt; null = cancelled, else the checkbox state.</summary>
    public static bool? Ask(Form owner, string title, string message, string optionText, bool optionDefault)
    {
        using var dialog = new CheckOptionDialog(owner, title, message, optionText, optionDefault);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._option.Checked : null;
    }
}
