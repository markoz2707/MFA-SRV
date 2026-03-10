using System;
using System.IO;
using System.Windows.Forms;

namespace MfaSrv.Server.Configurator;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Determine the install directory:
        // 1. Command-line argument
        // 2. Same directory as this executable
        string installDir;
        if (args.Length > 0 && Directory.Exists(args[0]))
        {
            installDir = args[0];
        }
        else
        {
            installDir = AppContext.BaseDirectory;
        }

        var configPath = Path.Combine(installDir, "appsettings.json");
        if (!File.Exists(configPath))
        {
            MessageBox.Show(
                $"Configuration file not found:\n{configPath}\n\nMake sure MfaSrv Server is installed.",
                "MfaSrv Configurator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.Run(new ConfigForm(installDir));
    }
}
