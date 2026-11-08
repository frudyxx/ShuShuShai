using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using ShuShuShai.App;
using ShuShuShai.App.Services;
using ShuShuShai.App.Views;
using ShuShuShai.Core;

internal static partial class Program
{
    private static readonly JsonSerializerOptions UpdateJson = new(JsonSerializerDefaults.Web);

    private static void SnapshotUpdateData(string directory)
    {
        var store = new TaskStore(directory);
        var tasks = store.LoadAll().Select(task => new
        {
            task.Id, task.CurrentId,
            Photos = task.Photos.Select(photo => new { photo.Id, photo.JpgPath, photo.RawPath, photo.Stars, photo.Decision })
        });
        Console.WriteLine("UPDATE_DATA " + JsonSerializer.Serialize(new
        {
            Tasks = tasks, Layout = store.GetSetting("layout-v3"), Shortcuts = store.GetSetting("keyboard-shortcuts-v1")
        }, UpdateJson));
    }

    private static async Task ExerciseUpdateHandoffAsync(string data, Uri endpoint, string receipt, bool cancelInstall, Version? installedVersion = null)
    {
        Environment.SetEnvironmentVariable("SHUSHUSHAI_DATA_DIR", data);
        var store = new TaskStore(data);
        var window = new MainWindow(store);
        window.Show();
        await window.InitializeWorkspaceAsync();
        await window.VM.OpenAsync(window.VM.History.Single());
        if (cancelInstall) window.VM.Apply(stars: 4);
        using var installedMutex = new Mutex(false, @"Local\ShuShuShai-UpdateTest-Installed");
        var service = new UpdateService(store, installedVersion ?? App.CurrentVersion, true, endpoint, start =>
        {
            start.ArgumentList.Add("/VERYSILENT");
            start.ArgumentList.Add("/SUPPRESSMSGBOXES");
            start.ArgumentList.Add("/NORESTART");
            if (cancelInstall) start.ArgumentList.Add("/QACANCEL=1");
            var process = Process.Start(start) ?? throw new IOException("无法启动验收安装器。");
            File.WriteAllLines(receipt, [process.Id.ToString(), start.FileName, Environment.ProcessId.ToString()]);
            return process;
        });
        var settings = new SettingsWindow(store, window.Shortcuts, () => true, service, package => window.PrepareUpdateAsync(service, package))
            { Owner = window };
        var result = new TaskCompletionSource<bool?>();
        _ = window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { result.SetResult(settings.ShowDialog()); }
            catch (Exception ex) { result.SetException(ex); }
        }));
        Button? Find(string name) => MainWindow.Descendants<Button>(settings).FirstOrDefault(b => b.IsVisible && Equals(b.Content, name));
        void Click(string name) => (Find(name) ?? throw new InvalidOperationException("没有找到 " + name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Find("关于我们") != null);
        Click("关于我们");
        await Until(() => Find("检查更新") != null);
        Click("检查更新");
        await Until(() => Find("下载更新") != null);
        Is(!File.Exists(receipt) && Find("安装更新") == null, "检查不能启动安装");
        Click("下载更新");
        await Until(() => Find("安装更新")?.IsEnabled == true);
        Is(!File.Exists(receipt), "下载不能启动安装");
        Click("安装更新");
        Is(await result.Task.WaitAsync(TimeSpan.FromSeconds(30)) == true);
        Is(File.Exists(receipt));
        window.Close();
        Is(!window.IsVisible);
        Console.WriteLine("UPDATE_HANDOFF_CONFIRMED");
    }

    private static async Task ServeUpdateFixtureAsync(string root)
    {
        await using var server = new UpdateFixture(path =>
        {
            if (path != "/update.json" && !Regex.IsMatch(path, @"^/ShuShuShai-Setup-\d+\.\d+\.\d+-win-x64\.exe$")) return null;
            var file = Path.Combine(root, path[1..]);
            return File.Exists(file) ? File.OpenRead(file) : null;
        }, () => File.Exists(Path.Combine(root, "slow.flag")) ? 100 : 0);
        await File.WriteAllTextAsync(Path.Combine(root, "endpoint.txt"), server.Address.AbsoluteUri);
        Console.WriteLine("UPDATE_FIXTURE " + server.Address.AbsoluteUri);
        var deadline = DateTime.UtcNow.AddHours(1);
        while (!File.Exists(Path.Combine(root, "stop.flag")) && DateTime.UtcNow < deadline) await Task.Delay(300);
    }

    private static async Task V034Specs()
    {
        var current = new Version(0, 3, 4);
        var payload = Enumerable.Range(0, 256 * 1024).Select(n => (byte)n).ToArray();
        var release = new UpdateManifest("0.3.5", "回环验收说明", "ShuShuShai-Setup-0.3.5-win-x64.exe",
            payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        Stream Manifest(UpdateManifest manifest) => new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJson));

        await Check("v0.3.4 只接受回环来源、合法清单和更高版本", async () =>
        {
            var manifest = release;
            await using var fixture = new UpdateFixture(path => path == "/update.json" ? Manifest(manifest) : null);
            var store = new TaskStore(Dir("updates-policy"));
            var service = new UpdateService(store, current, true, fixture.Address);
            Is(await service.CheckAsync(default) == release);
            Is(await new UpdateService(store, new Version(0, 3, 5), true, fixture.Address).CheckAsync(default) == null);
            Is(await new UpdateService(store, new Version(0, 3, 6), true, fixture.Address).CheckAsync(default) == null);
            Is(!new UpdateService(store, current, false, fixture.Address).IsConfigured);
            Is(!new UpdateService(store, current, true, new("https://example.com/update.json")).IsConfigured);
            Is(!UpdateService.IsLoopbackSource(new("http://localhost/update.json")));
            manifest = release with { FileName = "../unsafe.exe" };
            await ThrowsAsync<InvalidDataException>(() => service.CheckAsync(default));
            manifest = release with { Sha256 = "BAD" };
            await ThrowsAsync<InvalidDataException>(() => service.CheckAsync(default));
        });

        await Check("v0.3.4 下载进度、大小及 SHA-256，启动前复核", async () =>
        {
            await using var fixture = new UpdateFixture(path => path == "/update.json" ? Manifest(release) : new MemoryStream(payload));
            var store = new TaskStore(Dir("updates-good"));
            var service = new UpdateService(store, current, true, fixture.Address);
            long last = 0;
            var package = await service.DownloadAsync(release, new ProgressSink(bytes => last = bytes), default);
            var path = service.InstallerPath(package);
            Is(last == payload.Length && File.ReadAllBytes(path).SequenceEqual(payload));
            var changed = payload.ToArray();
            changed[0] ^= 1;
            File.WriteAllBytes(path, changed);
            await ThrowsAsync<InvalidDataException>(() => service.LaunchInstallerAsync(package));
            Is(service.PreviousAttempt() == null);
            service.Discard(package);
            Is(!File.Exists(path));
        });

        await Check("v0.3.4 断线、取消、尺寸错误与损坏包均可重试", async () =>
        {
            var bytes = payload.ToArray();
            int wait = 200;
            var fixture = new UpdateFixture(path => path == "/update.json" ? Manifest(release) : new MemoryStream(bytes), () => wait);
            var store = new TaskStore(Dir("updates-retry"));
            var service = new UpdateService(store, current, true, fixture.Address);
            var staging = Path.Combine(store.DataDirectory, "updates", "staging");
            try
            {
                using var cancel = new CancellationTokenSource();
                await ThrowsAsync<OperationCanceledException>(() => service.DownloadAsync(release,
                    new ProgressSink(_ => cancel.Cancel()), cancel.Token));
                Is(!Directory.EnumerateFiles(staging).Any());
                wait = 0;
                bytes = payload[..^1];
                await ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, null, default));
                bytes = payload.ToArray();
                bytes[0] ^= 1;
                await ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, null, default));
                Is(!Directory.EnumerateFiles(staging).Any());
                bytes = payload;
                var good = await service.DownloadAsync(release, null, default);
                service.Discard(good);
            }
            finally { await fixture.DisposeAsync(); }
            await ThrowsAsync<HttpRequestException>(() => service.CheckAsync(default));
        });

        await Check("v0.3.4 安装版显示检查入口，便携版维持手动更新", async () =>
        {
            await using var fixture = new UpdateFixture(_ => Manifest(release));
            var store = new TaskStore(Dir("updates-about"));
            var installed = new UpdatePanel(new(store, current, true, fixture.Address), _ => throw new Exception("不得自动安装"));
            var portable = new UpdatePanel(new(store, current, false, fixture.Address), _ => throw new Exception("不得自动安装"));
            var window = new Window { Content = new StackPanel { Children = { installed, portable } } };
            window.Show();
            try
            {
                var check = MainWindow.Descendants<Button>(installed).Single(b => Equals(b.Content, "检查更新"));
                Is(!MainWindow.Descendants<Button>(portable).Any(b => Equals(b.Content, "检查更新")));
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => MainWindow.Descendants<TextBlock>(installed).Any(t => t.Text.Contains("发现新版 v0.3.5")));
                Is(MainWindow.Descendants<Button>(installed).Any(b => b.IsVisible && Equals(b.Content, "下载更新")));
                Is(!MainWindow.Descendants<Button>(installed).Any(b => b.IsVisible && Equals(b.Content, "安装更新")));
            }
            finally { window.Close(); }
        });

        await Check("v0.3.4 扫描、导出窗口与保存失败阻止更新交接", async () =>
        {
            var (window, _) = await V3Window(3);
            var service = new UpdateService(window.VM.Store, current, true, null);
            var package = new DownloadedUpdate(release, Guid.NewGuid());
            try
            {
                var scan = window.VM.ScanAsync();
                Is((await window.PrepareUpdateAsync(service, package))?.Contains("仍在扫描") == true);
                await scan;
                window.VM.Apply(decision: Decision.Keep);
                var button = MainWindow.Descendants<Button>(window).Single(b => Equals(b.Content, "筛选完毕"));
                _ = window.Dispatcher.BeginInvoke(new Action(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))), DispatcherPriority.Normal);
                await Until(() => window.OwnedWindows.OfType<ExportWindow>().Any(w => w.IsVisible));
                var export = window.OwnedWindows.OfType<ExportWindow>().Single();
                Task<string?>? blocked = null;
                export.ExecutionStarting += () => blocked = window.PrepareUpdateAsync(service, package);
                await export.BuildAsync();
                await export.ExecuteAsync();
                Is(blocked != null && (await blocked)?.Contains("先完成导出") == true);
                export.Close();
                await Task.Delay(120);
                using var db = new SqliteConnection($"Data Source={Path.Combine(window.VM.Store.DataDirectory, "tasks.db")}");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "DROP TABLE settings";
                command.ExecuteNonQuery();
                try { Is((await window.PrepareUpdateAsync(service, package))?.Contains("布局保存失败") == true); }
                finally
                {
                    command.CommandText = "CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL)";
                    command.ExecuteNonQuery();
                }
            }
            finally { await CloseV3(window); }
        });
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("预期抛出 " + typeof(T).Name);
    }

    private sealed class ProgressSink(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private sealed class UpdateFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancel = new();
        private readonly Func<string, Stream?> open;
        private readonly Func<int> delay;
        private readonly Task serve;
        public Uri Address { get; }

        public UpdateFixture(Func<string, Stream?> open, Func<int>? delay = null)
        {
            this.open = open;
            this.delay = delay ?? (() => 0);
            listener.Start();
            Address = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/update.json");
            serve = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                    _ = RespondAsync(await listener.AcceptTcpClientAsync(cancel.Token));
            }
            catch (OperationCanceledException) { }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            try
            {
                var ct = cancel.Token;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                var line = await reader.ReadLineAsync(ct);
                string? header;
                do { header = await reader.ReadLineAsync(ct); } while (!string.IsNullOrEmpty(header));
                var path = line?.Split(' ').ElementAtOrDefault(1) ?? "";
                await using var content = open(path);
                var status = content == null ? "404 Not Found" : "200 OK";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {content?.Length ?? 0}\r\nConnection: close\r\n\r\n"), ct);
                if (content == null) return;
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (path.EndsWith(".exe")) await Task.Delay(delay(), ct);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            cancel.Cancel();
            await serve;
            listener.Stop();
            cancel.Dispose();
        }
    }
}
