namespace GTPMB;

/// <summary>
/// Base of the app's dialogs: owned by the main window, kept out of the taskbar and - like GTGPB.py's
/// center_window - centred over the owner, which keeps a dialog on top of the app no matter where (or on
/// which monitor) the user has dragged it. The position is clamped so the dialog stays fully on-screen.
/// </summary>
internal abstract class OwnedDialog : Form
{
    protected OwnedDialog(Form owner, string title)
    {
        SuspendLayout(); // resumed by FinishLayout() once the derived constructor has added its controls

        Owner = owner;
        Text = title;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual; // placed in OnLoad, when the size is final
        AutoScaleDimensions = new SizeF(96F, 96F); // every literal size in the dialogs is in 96-DPI pixels
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    /// <summary>Call last in the derived constructor.</summary>
    protected void FinishLayout()
    {
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e); // DPI scaling and auto-sizing are done: Size is final, and the window is not visible yet
        CenterOverOwner();
    }

    protected void CenterOverOwner()
    {
        Rectangle screen = Screen.FromControl(Owner ?? this).WorkingArea;
        Rectangle over = Owner is { WindowState: not FormWindowState.Minimized } owner ? owner.Bounds : screen;

        int x = over.X + (over.Width - Width) / 2;
        int y = over.Y + (over.Height - Height) / 2;
        x = Math.Max(screen.Left, Math.Min(x, screen.Right - Width));
        y = Math.Max(screen.Top, Math.Min(y, screen.Bottom - Height));
        Location = new Point(x, y);
    }
}
