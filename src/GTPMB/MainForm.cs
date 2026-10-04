using System.Text;
using GTPMB.Core.Mbl;
using GTPMB.Core.Pmb;

namespace GTPMB;

/// <summary>
/// The GTPMB main window, laid out like GTGPBc's: the PMB actions at the top, then the MBL
/// section, then the inspectors. All the work happens in GTPMB.Core; this class only asks for
/// paths, runs an operation behind the progress dialog and reports how it went.
/// </summary>
internal sealed class MainForm : Form
{
    private const string WorkingText = "Working...\n\nPlease wait.";
    private const string ConvertingText = "Converting textures...\n\nPlease wait.";
    private const string CountingText = "Counting colors...\n\nPlease wait.";

    private readonly Font _headerFont = new("Segoe UI", 9F, FontStyle.Bold);
    private readonly TableLayoutPanel _layout = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        Margin = Padding.Empty,
        Padding = new Padding(16, 10, 16, 10),
        MinimumSize = new Size(320, 0), // sized to fit the contents, with a comfortable minimum width
    };

    public MainForm()
    {
        SuspendLayout();

        Text = "GTPMB";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.Manual; // centred in OnLoad, when the size is final
        using (Stream icon = typeof(MainForm).Assembly.GetManifestResourceStream("PMB.ico")!)
            Icon = new Icon(icon);

        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        AddHeader("Menu source tree — a family and all its PMBs as one folder");
        AddButton("Extract menu source tree", ExtractTree, top: 2, bottom: 4);
        AddButton("Build menu source tree", BuildTree, top: 0, bottom: 4);
        AddButton("Check menu source tree", CheckTree, top: 0, bottom: 6);

        AddSection("PMB — menu pages & textures (GT3)");
        AddButton("Extract PMB to folder", ExtractPmb, top: 2, bottom: 4);
        AddButton("Generate PMB from .ini", GeneratePmb, top: 0, bottom: 4);
        AddButton("Update PMB file entries", UpdateFileEntries, top: 0, bottom: 6);

        AddSection("MBL — menu list: page flags & paths (GT3)");
        AddButton("Extract MBL to text", ExtractMbl, top: 2, bottom: 4);
        AddButton("Generate MBL from text", GenerateMbl, top: 0, bottom: 6);

        AddSection("Inspect");
        AddButton("Inspect PMB / MBL structure", InspectStructure, top: 2, bottom: 4);
        AddButton("Inspect color depths", InspectColorDepths, top: 0, bottom: 6);

        Controls.Add(_layout);

        AutoScaleDimensions = new SizeF(96F, 96F); // every literal size above is in 96-DPI pixels
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e); // DPI scaling and auto-sizing are done
        CenterToScreen();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _headerFont.Dispose();
        base.Dispose(disposing);
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private void AddButton(string text, Action onClick, int top, int bottom)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(0, 26),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, top, 0, bottom),
        };
        button.Click += (_, _) => onClick();
        _layout.Controls.Add(button);
    }

    private void AddHeader(string title)
    {
        _layout.Controls.Add(new Label
        {
            Text = title,
            UseMnemonic = false,
            Font = _headerFont,
            ForeColor = Color.FromArgb(0x44, 0x44, 0x44),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
        });
    }

    private void AddSection(string title)
    {
        _layout.Controls.Add(new Label // an etched horizontal separator
        {
            AutoSize = false,
            Height = 2,
            BorderStyle = BorderStyle.Fixed3D,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 8, 0, 2),
        });
        AddHeader(title);
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private void ExtractPmb()
    {
        using var open = new OpenFileDialog { Title = "Select PMB File", Filter = "PMB files (*.pmb)|*.pmb|All files (*.*)|*.*" };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        if (PickFolder("Select Destination Directory") is not { } destination)
            return;
        string pmbPath = open.FileName;

        RunOperation(ConvertingText, () => PmbExtractor.Extract(pmbPath, destination), ShowExtractResult);
    }

    internal void ShowExtractResult(ExtractResult result)
    {
        if (result.Errors.Count == 0)
        {
            ShowInfo("Success!", $"PMB extraction completed. {result.EntryCount} files extracted.");
            return;
        }

        ShowError("Completed with errors",
            $"Extraction + config written.\n\nDump failed for {result.Errors.Count} file(s). See the log window for details.");
        var log = new StringBuilder("Dump errors:\n");
        foreach (ItemError error in result.Errors)
            log.Append("- ").Append(error.Path).Append("\n  ").Append(error.Message).Append("\n\n");
        LogDialog.Open(this, "Completed with errors", log.ToString());
    }

    private void GeneratePmb()
    {
        using var open = new OpenFileDialog { Title = "Select pmb_config.ini File", Filter = "INI files (*.ini)|*.ini" };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        using var save = new SaveFileDialog { Title = "Save .pmb File As", Filter = "PMB files (*.pmb)|*.pmb", DefaultExt = "pmb" };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;
        string iniPath = open.FileName, pmbPath = save.FileName;

        var notices = new List<string>();
        RunOperation(ConvertingText, () => PmbBuilder.Build(iniPath, pmbPath, notices.Add), count =>
        {
            ShowInfo("Success!", $"PMB generation completed. {count} files packed.");
            if (notices.Count > 0)
                LogDialog.Open(this, "Build notices", string.Join('\n', notices));
        });
    }

    private void UpdateFileEntries()
    {
        if (PickRootFolder() is not { } root)
            return;

        List<ScannedEntry> entries;
        try
        {
            entries = PmbConfigGenerator.Scan(root);
        }
        catch (Exception ex)
        {
            ShowError("Error", ex.Message);
            return;
        }
        if (entries.Count == 0)
        {
            ShowInfo("Update PMB file entries", $"No content files found under:\n{root}");
            return;
        }
        if (CompressionDialog.Ask(this, entries) is not { } compression)
            return;

        RunOperation(WorkingText, () => PmbConfigGenerator.Generate(root, compression), result =>
            ShowInfo("Success!",
                $"Config file generated successfully at:\n{result.ConfigPath}\n\n{result.EntryCount} entries."
                + "\n\nEntry order follows the file names - that order is what the pages reference."));
    }

    private void ExtractMbl()
    {
        using var open = new OpenFileDialog { Title = "Select MBL File", Filter = "MBL files (*.mbl)|*.mbl|All files (*.*)|*.*" };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        using var save = new SaveFileDialog
        {
            Title = "Save Menu Text As",
            Filter = "Menu text (*.txt)|*.txt",
            DefaultExt = "txt",
            FileName = Path.GetFileNameWithoutExtension(open.FileName) + "_menu.txt",
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;
        string mblPath = open.FileName, textPath = save.FileName;

        RunOperation(WorkingText, () => MenuText.Extract(mblPath, textPath),
            path => ShowInfo("Success!", $"MBL extracted to:\n{path}"));
    }

    private void GenerateMbl()
    {
        using var open = new OpenFileDialog { Title = "Select Menu Text File", Filter = "Menu text (*.txt)|*.txt|All files (*.*)|*.*" };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        using var save = new SaveFileDialog { Title = "Save .mbl File As", Filter = "MBL files (*.mbl)|*.mbl", DefaultExt = "mbl" };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;
        string textPath = open.FileName, mblPath = save.FileName;

        RunOperation(WorkingText, () => MenuText.Build(textPath, mblPath),
            count => ShowInfo("Success!", $"MBL generation completed. {count} pages written."));
    }

    private void ExtractTree()
    {
        using var open = new OpenFileDialog { Title = "Select the family's MBL File", Filter = "MBL files (*.mbl)|*.mbl" };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        if (PickFolder("Select the folder holding this family's PMB files (e.g. a language folder)") is not { } pmbFolder)
            return;
        if (PickFolder("Select Destination Directory (a family folder is created inside)") is not { } destination)
            return;
        string mblPath = open.FileName;

        RunOperation(ConvertingText, () => MenuSource.ExtractTree(mblPath, pmbFolder, destination), result =>
        {
            if (result.Errors.Count == 0)
                ShowInfo("Success!", $"Menu source tree extracted to:\n{result.Root}\n\n{result.PageCount} menu pages, {result.PmbCount} PMB(s).");
            else
                ShowError("Completed with errors", $"Extracted to:\n{result.Root}\n\nDump failed for {result.Errors.Count} file(s).");
        });
    }

    private void BuildTree()
    {
        if (PickFolder("Select the menu source tree folder (contains menu.txt)") is not { } source)
            return;
        if (PickFolder("Select Output Directory for the .mbl and .pmb files") is not { } destination)
            return;
        if (CheckOptionDialog.Ask(this, "Build menu source tree",
                $"Build '{Path.GetFileName(source)}' into:\n{destination}",
                "Update all file entries first (rescan every PMB folder's files;\n"
                + "known files keep their compression, new files start uncompressed)",
                optionDefault: false) is not { } updateEntries)
            return;

        var notices = new List<string>();
        RunOperation(ConvertingText, () => MenuSource.BuildTree(source, destination, updateEntries, notices.Add), result =>
        {
            ShowInfo("Success!", $"Menu built: {Path.GetFileName(result.MblPath)} + {result.PmbCount} PMB(s), {result.FileCount} files packed.");
            if (notices.Count > 0)
                LogDialog.Open(this, "Build notices", string.Join('\n', notices));
        });
    }

    private void CheckTree()
    {
        if (PickFolder("Select the menu source tree folder (contains menu.txt)") is not { } source)
            return;

        RunOperation(WorkingText, () => MenuSource.CheckTree(source),
            report => LogDialog.Open(this, "Menu source check", report));
    }

    private void InspectStructure()
    {
        using var open = new OpenFileDialog
        {
            Title = "Select PMB or MBL File",
            Filter = "PMB / MBL files (*.pmb;*.mbl)|*.pmb;*.mbl|All files (*.*)|*.*",
        };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        string path = open.FileName;

        RunOperation(WorkingText, () => PmbInspector.Report(path),
            report => LogDialog.Open(this, Path.GetFileName(path), report));
    }

    private void InspectColorDepths()
    {
        if (PickRootFolder() is not { } root)
            return;

        RunOperation(CountingText, () => ColorInspector.BuildReport(root), report =>
        {
            if (report is null)
                ShowInfo("Inspect PNG color counts", $"No .png files found under:\n{root}");
            else
                LogDialog.Open(this, "PNG color counts", report);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The one path every operation takes: <paramref name="work"/> runs on a worker thread behind the modal progress
    /// dialog; then, back on the UI thread, its result goes to <paramref name="onSuccess"/> or its failure is reported.
    /// </summary>
    internal void RunOperation<T>(string progressText, Func<T> work, Action<T> onSuccess)
    {
        T result;
        try
        {
            result = ProgressDialog.Run(this, progressText, work);
        }
        catch (PmbToolException ex)
        {
            ShowError(ex.Title, ex.Message);
            if (!string.IsNullOrEmpty(ex.Details))
                LogDialog.Open(this, ex.Title, ex.Details);
            return;
        }
        catch (Exception ex)
        {
            ShowError("Error", ex.Message);
            return;
        }

        onSuccess(result);
    }

    private string? PickRootFolder()
    {
        string? root = PickFolder("Select Root Folder");
        if (root is null)
            ShowError("Error", "No root folder selected.");
        return root;
    }

    private string? PickFolder(string title)
    {
        using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void ShowInfo(string title, string text) =>
        MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowError(string title, string text) =>
        MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
