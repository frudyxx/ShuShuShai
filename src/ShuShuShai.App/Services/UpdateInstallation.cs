using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ShuShuShai.Core;

namespace ShuShuShai.App.Services;

internal static class UpdateInstallation
{
#if UPDATE_TEST_BUILD
    internal const string Profile = "ShuShuShai-UpdateTest";
#else
    internal const string Profile = "ShuShuShai";
#endif
    internal const string MutexName = @"Local\" + Profile + "-Installed";
    private const string InstallKey = @"Software\ShuShuShai\Installations\" + Profile;
    internal static string DataDirectory => Environment.GetEnvironmentVariable("SHUSHUSHAI_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Profile);

    internal static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallKey);
        if (key?.GetValue("InstallPath") is not string directory || !Path.IsPathFullyQualified(directory)) return false;
        var executable = Environment.ProcessPath;
        return executable != null && PathRules.Same(executable, Path.Combine(directory, "舒舒筛.exe"))
            && PathRules.Same(AppContext.BaseDirectory, directory);
    }

    internal static void StartUninstall()
    {
        if (!IsInstalled()) throw new InvalidOperationException("此程序不是当前用户注册的安装版。");
        using var key = Registry.CurrentUser.OpenSubKey(InstallKey);
        var path = key?.GetValue("UninstallerPath") as string;
        if (path == null || !Path.IsPathFullyQualified(path)
            || !PathRules.Same(Path.GetDirectoryName(path)!, AppContext.BaseDirectory)
            || !Regex.IsMatch(Path.GetFileName(path), @"\Aunins[0-9]{3}\.exe\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || !File.Exists(path) || !File.Exists(Path.ChangeExtension(path, ".dat")))
            throw new IOException("找不到本次安装的卸载程序。请在 Windows「已安装的应用」中卸载。");
        for (var entry = new FileInfo(path).Directory; entry != null; entry = entry.Parent)
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("卸载路径经过链接，已停止卸载。");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("卸载程序是链接，已停止卸载。");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add($"/WAITFORPID={Environment.ProcessId}");
        start.ArgumentList.Add("/SILENT");
        start.ArgumentList.Add("/NORESTART");
        using var process = Process.Start(start) ?? throw new IOException("未能启动卸载程序。");
    }

    internal static Uri? UpdateSource()
    {
#if UPDATE_TEST_BUILD
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("SHUSHUSHAI_TEST_UPDATE_URL"), UriKind.Absolute, out var source)
            && UpdateService.IsLoopbackSource(source))
            return source;
#endif
        return null;
    }

    internal static GitHubReleaseSource? ReleaseSource() => GitHubReleaseSource.FromRepository(BuildSetting("UpdateReleaseRepository"));
    internal static string? SigningCertificate() => BuildSetting("UpdateSigningCertificateSha256");

    private static string? BuildSetting(string key) => typeof(UpdateInstallation).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;
}
