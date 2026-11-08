using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ShuShuShai.App.Services;

namespace ShuShuShai.App.Views;

internal sealed class UpdatePanel : StackPanel
{
    private readonly UpdateService service;
    private readonly Func<DownloadedUpdate, Task<string?>> install;
    private readonly Button check, download, cancel, apply, releasePage;
    private readonly TextBlock notes = DialogUi.Text(""), status = DialogUi.Text("");
    private readonly ProgressBar progress = new() { Height = 8, Margin = new Thickness(4, 6, 4, 4), Maximum = 1 };
    private CancellationTokenSource? operation;
    private UpdateManifest? offer;
    private DownloadedUpdate? package;
    private bool installing;

    internal bool Working => operation != null || installing;
    internal event Action? HandedOff;

    internal UpdatePanel(UpdateService service, Func<DownloadedUpdate, Task<string?>> install)
    {
        this.service = service;
        this.install = install;
        check = DialogUi.Button("检查更新", async (_, _) => await CheckAsync(), "主动检查是否有可安装的更新。");
        download = DialogUi.Button("下载更新", async (_, _) => await DownloadAsync(), "下载所显示的安装包并校验大小与 SHA-256。");
        cancel = DialogUi.Button("取消下载", (_, _) => operation?.Cancel(), "停止当前请求并清理未完成的下载。");
        apply = DialogUi.Button("安装更新", async (_, _) => await InstallAsync(), "保存当前工作，退出应用并打开安装向导。");
        releasePage = DialogUi.Button("打开发布页", (_, _) =>
        {
            if (service.ReleasePage is not { } page) return;
            try { using var browser = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "无法打开发布页。" + ex.Message; }
        }, "在默认浏览器查看发布说明和下载安装包。");
        apply.SetResourceReference(StyleProperty, "Primary");
        Children.Add(DialogUi.Text(service.IsInstalled ? "安装版" : "便携版 · 下载新版 ZIP 后手动更新。"));
        if (service.IsInstalled)
        {
            Children.Add(notes);
            Children.Add(progress);
            Children.Add(status);
            Children.Add(DialogUi.Row(check, releasePage, download, cancel, apply));
            try { status.Text = service.PreviousAttempt() ?? (service.IsConfigured ? "" : "当前预览版尚未配置公开发布地址。"); }
            catch (Exception ex) { status.Text = "无法读取上次更新记录。" + ex.Message; }
        }
        RefreshButtons();
    }

    internal void CancelOperation() => operation?.Cancel();

    internal void Release()
    {
        if (package == null) return;
        service.Discard(package);
        package = null;
    }

    private void RefreshButtons()
    {
        check.IsEnabled = !Working;
        download.Visibility = offer != null && service.CanDownload ? Visibility.Visible : Visibility.Collapsed;
        releasePage.Visibility = service.ReleasePage != null ? Visibility.Visible : Visibility.Collapsed;
        releasePage.IsEnabled = !Working;
        download.IsEnabled = !Working;
        download.Content = package == null ? "下载更新" : "重新下载";
        apply.Visibility = package != null ? Visibility.Visible : Visibility.Collapsed;
        apply.IsEnabled = !Working;
        cancel.Visibility = operation != null ? Visibility.Visible : Visibility.Collapsed;
        progress.Visibility = (offer != null && operation != null) || package != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CheckAsync()
    {
        if (Working) return;
        if (!service.IsConfigured)
        {
            status.Text = "此预览版尚未配置可信的联网更新源。请使用新版安装程序更新。";
            return;
        }
        Release();
        offer = null;
        notes.Text = "";
        status.Text = "正在检查更新…";
        cancel.Content = "取消检查";
        using var request = operation = new();
        RefreshButtons();
        try
        {
            offer = await service.CheckAsync(request.Token);
            status.Text = offer == null ? "当前已是最新版本。"
                : service.CanDownload ? "查看更新说明后，可选择下载。" : "可打开发布页查看新版与下载方式。";
            if (offer != null) notes.Text = $"发现新版 v{offer.Version} · {offer.Size / 1048576.0:F1} MB\n{offer.Notes}";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { status.Text = "已取消检查。"; }
        catch (Exception ex) { status.Text = "检查更新失败，可重试。" + Explain(ex); }
        finally { operation = null; RefreshButtons(); }
    }

    private async Task DownloadAsync()
    {
        if (Working || offer == null || !service.CanDownload) return;
        Release();
        var manifest = offer;
        progress.Value = 0;
        cancel.Content = "取消下载";
        using var request = operation = new();
        status.Text = "正在连接更新服务…";
        RefreshButtons();
        try
        {
            package = await service.DownloadAsync(manifest, new Progress<long>(bytes =>
            {
                if (operation != request) return;
                progress.Value = (double)bytes / manifest.Size;
                status.Text = $"正在下载和校验 {bytes / 1048576.0:F1} / {manifest.Size / 1048576.0:F1} MB";
            }), request.Token);
            progress.Value = 1;
            status.Text = "下载及校验已完成。点击「安装更新」后保存并退出舒舒筛，随后进入安装向导。";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { status.Text = "已取消下载，可再次下载。"; }
        catch (Exception ex) { status.Text = "下载失败，可重试。" + Explain(ex); }
        finally { operation = null; RefreshButtons(); }
    }

    private async Task InstallAsync()
    {
        if (Working || package == null) return;
        installing = true;
        status.Text = "正在保存任务并复核安装包…";
        RefreshButtons();
        bool started = false;
        try
        {
            var failure = await install(package);
            if (failure != null) status.Text = failure;
            else { package = null; started = true; }
        }
        catch (Exception ex) { status.Text = "无法开始安装，可重试。" + Explain(ex); }
        finally { installing = false; RefreshButtons(); }
        if (started) HandedOff?.Invoke();
    }

    private static string Explain(Exception error) => error switch
    {
        OperationCanceledException => "连接超时，请检查更新服务。",
        System.Net.Http.HttpRequestException => "无法连接更新服务或获取文件。" + error.Message,
        _ => error.Message
    };
}
