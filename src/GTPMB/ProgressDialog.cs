namespace GTPMB;

/// <summary>
/// The modal "Working" dialog (GTGPB.py show_progress_dialog): a message over an animated marquee bar, shown while
/// an operation runs on a worker thread. The user cannot close it; it closes itself when the work ends.
/// </summary>
internal sealed class ProgressDialog : OwnedDialog
{
    private bool _finished;

    private ProgressDialog(Form owner, string text) : base(owner, "Working")
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(30, 20, 30, 20),
        };
        layout.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 8),
        });
        layout.Controls.Add(new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 20,
            Size = new Size(260, 20),
            Anchor = AnchorStyles.None,
            Margin = Padding.Empty,
        });
        Controls.Add(layout);

        FinishLayout();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread while the dialog is shown modally over <paramref name="owner"/>;
    /// then returns its result - or rethrows its exception - on the calling (UI) thread. The worker must not touch UI.
    /// </summary>
    public static T Run<T>(Form owner, string text, Func<T> work)
    {
        using var dialog = new ProgressDialog(owner, text);
        Task<T>? task = null;

        dialog.Shown += async (_, _) =>
        {
            task = Task.Run(work);
            try
            {
                await task; // resumes here on the UI thread
            }
            catch
            {
                // Rethrown to the caller below, once the dialog is gone.
            }

            dialog._finished = true;
            dialog.Close();
        };

        dialog.ShowDialog(owner);
        return task!.GetAwaiter().GetResult();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 must not abandon a running operation.
        if (!_finished && e.CloseReason == CloseReason.UserClosing)
            e.Cancel = true;
        base.OnFormClosing(e);
    }
}
