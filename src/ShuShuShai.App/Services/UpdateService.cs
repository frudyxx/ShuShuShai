using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ShuShuShai.Core;

namespace ShuShuShai.App.Services;

internal sealed record UpdateManifest(string Version, string Notes, string FileName, long Size, string Sha256);
internal sealed record DownloadedUpdate(UpdateManifest Manifest, Guid Id);

internal sealed class UpdateService
{
    private const int MaxManifestBytes = 64 * 1024;
    private const long MaxInstallerBytes = 512 * 1024 * 1024;
    private const string AttemptKey = "update-attempt-v1";
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = NetworkTimeout
    }) { Timeout = NetworkTimeout };
    private static readonly HttpClient ReleaseClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, ConnectTimeout = NetworkTimeout
    }) { Timeout = NetworkTimeout };

    private readonly TaskStore store;
    private readonly Version current;
    private readonly Uri? source;
    private readonly GitHubReleaseSource? release;
    private readonly string? signingCertificate;
    private readonly string staging;
    private readonly Func<ProcessStartInfo, Process?> startInstaller;

    internal bool IsInstalled { get; }
    internal bool IsConfigured => source != null;
    internal bool CanDownload => IsConfigured && (release == null || signingCertificate != null);
    internal Uri? ReleasePage => release?.ReleasePage;

    internal UpdateService(TaskStore store, Version current, bool installed, Uri? source,
        Func<ProcessStartInfo, Process?>? startInstaller = null, GitHubReleaseSource? release = null,
        string? signingCertificate = null)
    {
        this.store = store;
        this.current = current;
        IsInstalled = installed;
        bool loopback = installed && source != null && IsLoopbackSource(source);
        this.release = installed && !loopback ? release : null;
        this.source = loopback ? source : this.release?.Manifest;
        this.signingCertificate = signingCertificate is { Length: 64 } && signingCertificate.All(Uri.IsHexDigit)
            ? signingCertificate : null;
        staging = Path.Combine(store.DataDirectory, "updates", "staging");
        this.startInstaller = startInstaller ?? Process.Start;
    }

    internal static bool IsLoopbackSource(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp
        && uri.Host is "127.0.0.1" or "[::1]" && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private Uri RequireSource() => source ?? throw new InvalidOperationException("当前版本尚未配置受信任的更新源。请使用新版安装程序。");

    internal async Task<UpdateManifest?> CheckAsync(CancellationToken token)
    {
        using var response = await GetAsync(RequireSource(), "update.json", token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxManifestBytes) throw new InvalidDataException("更新清单过大。");
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await ReadAsync(body, MaxManifestBytes, async (chunk, ct) => await buffer.WriteAsync(chunk, ct), token).ConfigureAwait(false);
        UpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Json)
                ?? throw new InvalidDataException("更新清单为空。");
        }
        catch (JsonException ex) { throw new InvalidDataException("更新清单格式无效。", ex); }
        var version = Validate(manifest);
        return version > current ? manifest : null;
    }

    private static Version Validate(UpdateManifest manifest)
    {
        if (!Version.TryParse(manifest.Version, out var version) || version.Build < 0 || version.Revision != -1 || version.ToString(3) != manifest.Version)
            throw new InvalidDataException("更新版本号无效。");
        if (!string.Equals(manifest.FileName, $"ShuShuShai-Setup-{manifest.Version}-win-x64.exe", StringComparison.Ordinal))
            throw new InvalidDataException("安装包文件名与版本不符。");
        if (manifest.Size <= 0 || manifest.Size > MaxInstallerBytes
            || manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("安装包大小或 SHA-256 无效。");
        if (string.IsNullOrWhiteSpace(manifest.Notes) || manifest.Notes.Length > 8192)
            throw new InvalidDataException("更新说明缺失或过长。");
        return version;
    }

    internal async Task<DownloadedUpdate> DownloadAsync(UpdateManifest manifest, IProgress<long>? progress, CancellationToken token)
    {
        if (Validate(manifest) <= current) throw new InvalidDataException("只允许下载更高版本。");
        RequireDownload();
        var location = release?.Installer(manifest) ?? new Uri(RequireSource(), manifest.FileName);
        var package = new DownloadedUpdate(manifest, Guid.NewGuid());
        var temp = Path.Combine(staging, package.Id.ToString("N") + ".partial");
        EnsureStaging();
        try
        {
            using var response = await GetAsync(location, manifest.FileName, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long size && size != manifest.Size)
                throw new InvalidDataException("下载大小与清单不符。");
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                bytes = await ReadAsync(body, manifest.Size, async (chunk, ct) =>
                {
                    await file.WriteAsync(chunk, ct).ConfigureAwait(false);
                    hash.AppendData(chunk.Span);
                    progress?.Report(file.Position);
                }, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
            }
            RequireMatch(manifest, bytes, hash.GetHashAndReset());
            token.ThrowIfCancellationRequested();
            VerifyPublisher(temp, manifest);
            token.ThrowIfCancellationRequested();
            File.Move(temp, InstallerPath(package), overwrite: false);
            return package;
        }
        finally { TryDiscard(temp); }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri address, string asset, CancellationToken token)
    {
        if (release == null && !IsLoopbackSource(address)) throw new InvalidOperationException("本地验收只允许访问回环地址。");
        var client = release == null ? Client : ReleaseClient;
        for (int redirects = 0; ; redirects++)
        {
            var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            int status = (int)response.StatusCode;
            var location = response.Headers.Location;
            response.Dispose();
            if (release == null || status is not (301 or 302 or 303 or 307 or 308))
                throw new HttpRequestException($"更新服务返回 HTTP {status}。");
            if (redirects >= 3 || location == null || !Uri.TryCreate(address, location, out var next)
                || !release.AllowsRedirect(address, next, asset))
                throw new InvalidDataException("更新文件被重定向到未获允许的地址。");
            address = next;
        }
    }

    private void RequireDownload()
    {
        if (!CanDownload) throw new InvalidOperationException("当前版本可检查更新，请前往发布页查看下载方式。");
    }

    private void VerifyPublisher(string path, UpdateManifest manifest)
    {
        if (release == null) return;
        InstallerSignature.Verify(path, signingCertificate!);
        var info = FileVersionInfo.GetVersionInfo(path);
        if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
            != Version.Parse(manifest.Version + ".0"))
            throw new InvalidDataException("签名安装包的实际版本与更新清单不一致。");
    }

    private static async Task<long> ReadAsync(Stream source, long limit, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> consume, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long size = 0;
        try
        {
            while (true)
            {
                idle.CancelAfter(NetworkTimeout);
                int read = await source.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0) return size;
                size += read;
                if (size > limit) throw new InvalidDataException("实际下载数据超过声明大小。");
                await consume(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void RequireMatch(UpdateManifest manifest, long size, byte[] hash)
    {
        if (size != manifest.Size || !Convert.ToHexString(hash).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包大小或 SHA-256 校验失败，请重新下载。");
    }

    internal string InstallerPath(DownloadedUpdate package) => Path.Combine(staging, package.Id.ToString("N") + ".exe");

    internal void Discard(DownloadedUpdate package) => TryDiscard(InstallerPath(package));

    private void TryDiscard(string path)
    {
        if (!Directory.Exists(staging)) return;
        try { CheckStaging(); TryDelete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void EnsureStaging()
    {
        Directory.CreateDirectory(staging);
        CheckStaging();
    }

    private void CheckStaging()
    {
        for (var directory = new DirectoryInfo(staging); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新暂存目录经过链接，已停止更新。");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record Attempt(string Version, Guid Id);

    internal string? PreviousAttempt()
    {
        var json = store.GetSetting(AttemptKey);
        if (json == null) return null;
        var attempt = JsonSerializer.Deserialize<Attempt>(json, Json);
        if (attempt == null || !Version.TryParse(attempt.Version, out var target)) return null;
        var path = Path.Combine(staging, attempt.Id.ToString("N") + ".exe");
        if (current >= target) TryDiscard(path);
        return current >= target
            ? $"上次更新已完成，当前版本 v{current.ToString(3)}。"
            : $"上次更新到 v{target.ToString(3)} 未完成。若已关闭安装向导，可重新检查并下载。";
    }

    internal Task LaunchInstallerAsync(DownloadedUpdate package) => Task.Run(async () =>
    {
        RequireDownload();
        if (Validate(package.Manifest) <= current) throw new InvalidDataException("只允许安装更高版本。");
        EnsureStaging();
        var path = InstallerPath(package);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("安装包路径经过链接，已停止更新。");
        // Keep the verified file open without write/delete sharing until Windows has started it.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        RequireMatch(package.Manifest, file.Length, await SHA256.HashDataAsync(file).ConfigureAwait(false));
        VerifyPublisher(path, package.Manifest);
        store.SetSetting(AttemptKey, JsonSerializer.Serialize(new Attempt(package.Manifest.Version, package.Id), Json));
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = staging };
        start.ArgumentList.Add($"/WAITFORPID={Environment.ProcessId}");
        start.ArgumentList.Add("/LOG=" + Path.Combine(store.DataDirectory, "updates", "latest-install.log"));
        using var installer = startInstaller(start) ?? throw new IOException("无法启动安装向导。");
    });
}
