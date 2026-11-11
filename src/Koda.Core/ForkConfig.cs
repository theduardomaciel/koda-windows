namespace Koda.Core;

/// <summary>
/// Fork-only links. Upstream's own URLs stay where they are; every surface that
/// sends someone to Koda's repository or releases reads the values here instead,
/// so a fork build never points its users at upstream's downloads.
/// </summary>
public static class ForkConfig
{
    public const string Repo = "theduardomaciel/koda-windows";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string LatestReleaseUrl = ReleasesUrl + "/latest";
    public const string IssuesUrl = RepoUrl + "/issues";
}
