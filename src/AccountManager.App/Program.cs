using System;
using AccountManager.App.Services;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;

namespace AccountManager.App;

internal static class Program
{
    /// <summary>Set while this process is the single running instance.</summary>
    public static SingleInstance? Instance { get; private set; }

    // Avalonia configuration; keep this method beside Main so the visual designer
    // and the runtime pick up the same setup.
    [STAThread]
    public static int Main(string[] args)
    {
        if (UpdateProbe.TryHandle(args, out var probeExitCode)) { return probeExitCode; }
        if (!SelfUpdater.HandleStartup(args)) { return 1; }

        var instance = new SingleInstance("OpenCodeAccountManager-" + Environment.UserName);
        if (!instance.IsFirstInstance)
        {
            // Another window is running. If the hand-off failed, fail loudly with a nonzero exit
            // instead of pretending the first window was activated (or starting a second primary).
            if (instance.ActivationFailed)
            {
                Console.Error.WriteLine(instance.OwnershipUnconfirmed
                    ? "OpenCodeAccountManager: could not establish single-instance ownership (unsafe or inaccessible runtime directory); refusing to start."
                    : "OpenCodeAccountManager: could not activate the running window (single-instance hand-off failed).");
                instance.Dispose();
                return 3;
            }

            instance.Dispose();
            return 0;
        }

        Instance = instance;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        finally
        {
            Instance = null;
            instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        return (OperatingSystem.IsLinux() ? WithLinuxSystemFont(builder) : builder.WithInterFont())
            .LogToTrace();
    }

    /// <summary>
    /// Same face as Windows: bundled Inter for Latin text, with an installed CJK face supplying the
    /// Chinese glyphs (Windows gets Microsoft YaHei there). Enumeration is best effort.
    /// </summary>
    private static AppBuilder WithLinuxSystemFont(AppBuilder builder)
    {
        builder = builder.WithInterFont();
        try
        {
            var installed = SKFontManager.Default.GetFontFamilies();
            var family = UiFontSelector.Pick(installed, Environment.GetEnvironmentVariable(UiFontSelector.OverrideVariable));
            var fallbacks = UiFontSelector.Fallbacks(installed, family);
            if (family is not null || fallbacks.Count > 0)
            {
                return builder.With(new FontManagerOptions
                {
                    DefaultFamilyName = family ?? InterFamily,
                    FontFallbacks = fallbacks
                        .Select(name => new FontFallback { FontFamily = new FontFamily(name) })
                        .ToArray(),
                });
            }
        }
        catch
        {
            // Keep the bundled face and fontconfig's fallback.
        }

        return builder;
    }

    // Family key that WithInterFont registers as the default.
    private const string InterFamily = "fonts:Inter#Inter";
}
