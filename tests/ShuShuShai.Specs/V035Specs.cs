using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using Microsoft.Data.Sqlite;
using ShuShuShai.App;
using ShuShuShai.App.Controls;
using ShuShuShai.App.Services;
using ShuShuShai.Core;

internal static partial class Program
{
    private static async Task V035Specs()
    {
        await Check("v0.3.5 历史移除、重启恢复、导出保护与永久删除", async () =>
        {
            var directory = Dir("history-data");
            var store = new TaskStore(directory);
            var source = Write(Dir("history-source"), "原片.JPG");
            var target = Write(Dir("history-output"), "导出.JPG");
            byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
            var sourceHash = Hash(source);
            var targetHash = Hash(target);
            var task = new PhotoTask { Photos = [new Photo { JpgPath = source, Stars = 4, Decision = Decision.Keep }] };
            store.Save(task);
            store.SetArchived(task.Id, true);
            var reopened = new TaskStore(directory);
            Is(reopened.LoadAll().Count == 0);
            var archived = reopened.LoadAll(archived: true).Single();
            Is(archived.Id == task.Id && archived.Photos.Single().Stars == 4 && archived.Photos.Single().Decision == Decision.Keep);
            var export = new ExportPlan { TaskId = task.Id, Root = Path.GetDirectoryName(target)!, RootCreated = true };
            store.SaveExport(export);
            await ThrowsAsync<InvalidOperationException>(() => Task.Run(() => store.DeleteTask(task.Id)));
            Is(store.LoadAll(archived: true).Single().Id == task.Id);
            store.SetArchived(task.Id, false);
            Is(reopened.LoadAll().Single().Id == task.Id);
            export.Complete = true;
            store.SaveExport(export);
            store.DeleteTask(task.Id);
            await ThrowsAsync<InvalidOperationException>(() => Task.Run(() => reopened.Save(archived)));
            await ThrowsAsync<InvalidOperationException>(() => new ExportService(reopened).RunAsync(export, null, default));
            Is(reopened.LoadAll().Count == 0 && reopened.LoadAll(archived: true).Count == 0);
            Is(reopened.LoadExports().Single().Root == export.Root);
            Is(Hash(source).SequenceEqual(sourceHash) && Hash(target).SequenceEqual(targetHash));
        });

        await Check("v0.3.5 固定 GitHub 发布地址、检查与安装权限分开、拒绝越界重定向", async () =>
        {
            var release = GitHubReleaseSource.FromRepository("example/ShuShuShai-Releases")!;
            Is(release.Manifest.AbsoluteUri == "https://github.com/example/ShuShuShai-Releases/releases/latest/download/update.json");
            Is(GitHubReleaseSource.FromRepository("example/..") == null
                && GitHubReleaseSource.FromRepository("example/releases/extra") == null);
            bool Allows(string address) => release.AllowsRedirect(release.Manifest, new Uri(address), "update.json");
            Is(Allows("https://github.com/example/ShuShuShai-Releases/releases/download/v0.3.6/update.json"));
            Is(Allows("https://release-assets.githubusercontent.com/example/blob?token=fixture"));
            Is(!Allows("http://release-assets.githubusercontent.com/blob")
                && !Allows("https://github.com/example/other/releases/download/v0.3.6/update.json")
                && !Allows("https://github.com/example/ShuShuShai-Releases/releases/download/v0.3.6/unexpected.exe")
                && !Allows("https://github.com@evil.test/update.json")
                && !Allows("https://release-assets.githubusercontent.com:444/blob"));
            var store = new TaskStore(Dir("release-policy"));
            var service = new UpdateService(store, new Version(0, 3, 5), true, null, release: release);
            Is(service.IsConfigured && !service.CanDownload && service.ReleasePage != null);
            Is(!new UpdateService(store, new Version(0, 3, 5), false, null, release: release).IsConfigured);
            var manifest = new UpdateManifest("0.3.6", "测试", "ShuShuShai-Setup-0.3.6-win-x64.exe", 12, new string('0', 64));
            await ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(manifest, null, default));
            await ThrowsAsync<InvalidDataException>(() => Task.Run(() =>
                InstallerSignature.Verify(Write(Dir("unsigned-package"), "unsigned.exe"), new string('0', 64))));
        });

        await Check("v0.3.5 工作区顶栏、排序、加载与全屏布局恢复", async () =>
        {
            var (window, _) = await V3Window(3, high: true);
            try
            {
                Is(WindowChrome.GetWindowChrome(window) != null);
                Is(((RowDefinition)window.FindName("HeaderRow")).Height.Value == 40);
                Is(window.FindName("StatusBar") == null && window.FindName("TaskStatsBar") == null);
                var header = (FrameworkElement)window.FindName("HeaderBar");
                var buttons = MainWindow.Descendants<Button>(header).Select(b => b.Content as string).ToArray();
                Is(buttons.Take(3).SequenceEqual(new[] { "撤回上一步", "设置", "筛选完毕" }));
                var viewer = (PhotoViewer)window.FindName("LargeViewer");
                viewer.SetZoom(1);
                Is(viewer.IsLoading, "读取原始像素时应进入加载状态");
                await Until(() => !viewer.IsLoading);
                window.VM.SortIndex = 1;
                window.VM.ViewIndex = 1;
                window.SetFullScreen(true);
                Is(header.Visibility == Visibility.Collapsed && WindowChrome.GetWindowChrome(window) == null && window.VM.IsLarge);
                Is(PhotoViewer.GetSpinnerInset(viewer) > 0);
                window.SetFullScreen(false);
                Is(header.IsVisible && window.VM.IsGrid && WindowChrome.GetWindowChrome(window) != null);
                Is(PhotoViewer.GetSpinnerInset(viewer) == 0);
                Is(await window.VM.FlushSaveAsync() && window.VM.Store.LoadAll().Single().SortByTime);

                using var db = new SqliteConnection($"Data Source={Path.Combine(window.VM.Store.DataDirectory, "tasks.db")}");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER reject_save BEFORE INSERT ON tasks BEGIN SELECT RAISE(ABORT, 'fixture'); END";
                command.ExecuteNonQuery();
                var notice = (FrameworkElement)window.FindName("SaveNotice");
                try
                {
                    Is(!await window.VM.FlushSaveAsync() && notice.IsVisible);
                }
                finally
                {
                    command.CommandText = "DROP TRIGGER reject_save";
                    command.ExecuteNonQuery();
                }
                MainWindow.Descendants<Button>(notice).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !notice.IsVisible);

                var toggle = (Button)window.FindName("LeftToggle");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var scan = window.VM.ScanAsync();
                var token = window.VM.OperationCancellation!.Token;
                var scanNotice = (FrameworkElement)window.FindName("ScanNotice");
                Is(scanNotice.IsVisible);
                MainWindow.Descendants<Button>(scanNotice).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await scan;
                Is(token.IsCancellationRequested && !scanNotice.IsVisible);
            }
            finally { await CloseV3(window); }
        });
    }
}
