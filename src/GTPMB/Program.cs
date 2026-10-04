namespace GTPMB;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Visual styles + PerMonitorV2 high-DPI mode, generated from the ApplicationXxx properties in GTGPBc.csproj.
        ApplicationConfiguration.Initialize();

        // Last-resort funnel for anything thrown on the UI thread outside MainForm's operation helper.
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new MainForm());
    }
}
