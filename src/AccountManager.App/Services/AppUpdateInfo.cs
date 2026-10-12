using System.Reflection;

namespace AccountManager.App.Services;

public static class AppUpdateInfo
{
    public const string Owner = "ruoshuixx";
    public const string Repository = "opencode-account-manager";
    public const string ReleaseUrl = "https://github.com/ruoshuixx/opencode-account-manager/releases/latest";
    public static Version Version => typeof(AppUpdateInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>
    /// Shown in the title bar and update dialog. Prefers an explicit informational version
    /// (e.g. <c>1.0.4B</c> for a test build); falls back to <c>major.minor.patch</c>. The
    /// numeric <see cref="Version"/> used for update comparisons is unaffected.
    /// </summary>
    public static string DisplayVersion
    {
        get
        {
            var info = typeof(AppUpdateInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                if (plus >= 0) { info = info[..plus]; }
                if (!string.IsNullOrWhiteSpace(info) &&
                    !string.Equals(info, Version.ToString(3), StringComparison.Ordinal))
                {
                    return info;
                }
            }

            return Version.ToString(3);
        }
    }
}
