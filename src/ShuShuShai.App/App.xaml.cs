using System.Windows;
using System.IO;
using ShuShuShai.App.Views;
using ShuShuShai.App.Services;
using ShuShuShai.Core;
namespace ShuShuShai.App;
public partial class App : Application
{
    internal static Version CurrentVersion { get; } = Version.Parse(typeof(App).Assembly.GetName().Version!.ToString(3));
    internal static string VersionLabel => CurrentVersion.ToString(3);
    private Mutex? installedMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            ShowError("操作失败", args.Exception);
            args.Handled = true;
        };

        StartupWindow? splash = null;
        bool handingOff = false;
        try
        {
            if (UpdateInstallation.IsInstalled()) installedMutex = new(false, UpdateInstallation.MutexName);
            splash = new StartupWindow();
            splash.Closed += (_, _) => { if (!handingOff) Shutdown(); };
            splash.Show();
            var preparation = PrepareHomeAsync();
            var presentation = splash.PlayAsync(preparation);
            var home = await preparation;
            await presentation;

            MainWindow = home;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            home.ContentRendered += CloseSplash;
            home.Show();

            void CloseSplash(object? sender, EventArgs args)
            {
                home.ContentRendered -= CloseSplash;
                handingOff = true;
                splash.Close();
            }
        }
        catch (Exception ex)
        {
            handingOff = true;
            if (splash?.IsVisible == true) splash.Close();
            ShowError("启动失败", ex);
            Shutdown(1);
        }
    }

    private static async Task<MainWindow> PrepareHomeAsync()
    {
        var store = await Task.Run(() => new TaskStore(UpdateInstallation.DataDirectory));
        var home = new MainWindow(store);
        await home.InitializeWorkspaceAsync();
        return home;
    }

    private static void ShowError(string title, Exception error)
    {
        string details;
        try
        {
            var data = UpdateInstallation.DataDirectory;
            var dir = Path.Combine(data, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"), $"{DateTime.Now:O} {error}\n");
            details = "\n诊断详情已保存到本机日志。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            details = "\n写入本机诊断日志失败：" + ex.Message;
        }
        MessageBox.Show(title + "：" + error.Message + details, "舒舒筛", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        installedMutex?.Dispose();
        base.OnExit(e);
    }
}
