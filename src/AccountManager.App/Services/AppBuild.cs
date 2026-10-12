namespace AccountManager.App.Services;

/// <summary>
/// Build stamp derived from the executable's own file time, so any copy of the portable exe
/// shows when it was published — an easy "is this the latest build?" check.
/// </summary>
public static class AppBuild
{
    /// <summary>For example "构建 2026-10-04 02:35"; empty when the path cannot be resolved.</summary>
    public static string Stamp()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return "构建 " + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
        }
        catch
        {
            // Best effort only.
        }

        return string.Empty;
    }
}
