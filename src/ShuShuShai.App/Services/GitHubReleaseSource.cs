using System.Text.RegularExpressions;

namespace ShuShuShai.App.Services;

internal sealed class GitHubReleaseSource
{
    private readonly string repository;

    private GitHubReleaseSource(string repository) => this.repository = repository;

    internal static GitHubReleaseSource? FromRepository(string? repository) =>
        repository != null && Regex.IsMatch(repository, @"\A[A-Za-z0-9-]+/(?!\.{1,2}\z)[A-Za-z0-9_.-]+\z")
            ? new(repository) : null;

    internal Uri ReleasePage => new($"https://github.com/{repository}/releases/latest");
    internal Uri Manifest => new($"https://github.com/{repository}/releases/latest/download/update.json");
    internal Uri Installer(UpdateManifest manifest) =>
        new($"https://github.com/{repository}/releases/download/v{manifest.Version}/{manifest.FileName}");

    internal bool AllowsRedirect(Uri from, Uri to, string asset)
    {
        if (to.Scheme != Uri.UriSchemeHttps || !to.IsDefaultPort || to.UserInfo.Length != 0
            || to.Fragment.Length != 0 || from.Host != "github.com")
            return false;
        if (to.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com")
            return true;
        var prefix = $"/{repository}/releases/download/";
        if (to.Host != "github.com" || to.Query.Length != 0
            || !to.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = to.AbsolutePath[prefix.Length..].Split('/');
        return parts.Length == 2 && Regex.IsMatch(parts[0], @"\Av[0-9]+\.[0-9]+\.[0-9]+\z")
            && parts[1].Equals(asset, StringComparison.Ordinal);
    }
}
