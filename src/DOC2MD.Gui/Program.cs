namespace DOC2MD.Gui;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // WinForms dialogs and controls require an STA UI thread; ApplicationConfiguration also applies DPI defaults.
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }
}
