using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace HtPiano;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        int identityStatus = SetCurrentProcessExplicitAppUserModelID("HtPiano.Desktop");
        if (identityStatus < 0)
            Trace.TraceWarning("Could not set HtPiano taskbar identity: 0x{0:X8}.", identityStatus);

        base.OnStartup(e);
        UiLanguage.Initialize();
        // Explorer passes the recording as the sole argument. Keep the existing sound-bank arguments.
        string[] bankArgs = e.Args;
        string? recording = null;
        if (e.Args is [string file] && Path.GetExtension(file).Equals(".htkeys", StringComparison.OrdinalIgnoreCase))
        {
            recording = file;
            bankArgs = [];
        }
        MainWindow = new HtPiano.MainWindow(() => new NativePiano(FindBank(bankArgs)), recording);
        MainWindow.Show();
    }

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private static string FindBank(string[] args)
    {
        if (args.Length != 0)
        {
            string file = args switch
            {
                [string path] when path != "--bank" => path,
                ["--bank", string path] => path,
                _ => throw new ArgumentException(UiLanguage.Get("Lang.Bank.Usage"))
            };
            string pathToBank = Path.GetFullPath(file);
            if (!File.Exists(pathToBank))
                throw new FileNotFoundException(UiLanguage.Format("Lang.Bank.PathMissing", pathToBank));
            return pathToBank;
        }

        string local = Path.Combine(AppContext.BaseDirectory, "instruments.htbank");
        if (File.Exists(local)) return local;
        throw new FileNotFoundException(UiLanguage.Format("Lang.Bank.NotInstalled", AppContext.BaseDirectory));
    }
}
