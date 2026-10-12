using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using AccountManager.App.Services;
using AccountManager.Core.OpenCode;

namespace AccountManager.Linux.IntegrationHarness;

/// <summary>
/// Process entry for the native integration scenarios. The reserved production probe and updater
/// startup hooks run first, matching AccountManager.App.Program without constructing the UI.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    private const string ChildBehaviorVariable = "OCAM_LINUX_HARNESS_CHILD_BEHAVIOR";
    private const string ChildStartFileVariable = "OCAM_LINUX_HARNESS_CHILD_START_FILE";
    private const string ChildResultFileVariable = "OCAM_LINUX_HARNESS_CHILD_RESULT_FILE";
    private const string ChildCompleteFileVariable = "OCAM_LINUX_HARNESS_CHILD_COMPLETE_FILE";

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.Error.WriteLine("HARNESS_UNSUPPORTED platform=linux-x64-required");
            return 78;
        }

        try
        {
            if (UpdateProbe.TryHandle(args, out var probeExitCode)) { return probeExitCode; }

            var isUpdateChild = IsLinuxUpdateInvocation(args);
            if (isUpdateChild)
            {
                if (Environment.GetEnvironmentVariable(ChildBehaviorVariable) == "exit-before-entry")
                {
                    WriteChildResult("suppressed-before-entry");
                    return 86;
                }
                if (Environment.GetEnvironmentVariable(ChildBehaviorVariable) == "hold-before-entry")
                {
                    WriteChildResult(ChildIdentityMarker("held-before-entry"));
                    Thread.Sleep(TimeSpan.FromSeconds(90));
                    return 86;
                }
                WriteChildStart();
            }

            if (!SelfUpdater.HandleStartup(args))
            {
                if (isUpdateChild) { WriteChildResult(ChildIdentityMarker("startup-rejected")); }
                return 1;
            }

            if (isUpdateChild)
            {
                var completionFile = Environment.GetEnvironmentVariable(ChildCompleteFileVariable);
                if (!string.IsNullOrWhiteSpace(completionFile))
                {
                    // This is the production owned-backup cleanup boundary. The harness reaches it
                    // after the real managed-entry handshake, but does not open an Avalonia window.
                    SelfUpdater.CompleteStartup();
                    HarnessSupport.WriteAtomic(completionFile, "mechanism-startup-complete\n");
                }
                return 0;
            }

            if (args.Length == 0) { return UsageError(); }
            return args[0] switch
            {
                "--harness-info" => PrintHarnessInfo(),
                "--native-suite" => await LinuxNativeScenarios.RunAllAsync(args).ConfigureAwait(false),
                "--key-write" => await LinuxNativeScenarios.RunKeyWriterChildAsync(args).ConfigureAwait(false),
                "--key-read" => await LinuxNativeScenarios.RunKeyReaderChildAsync(args).ConfigureAwait(false),
                "--instance-race-worker" => await LinuxNativeScenarios.RunInstanceRaceWorkerAsync(args).ConfigureAwait(false),
                "--instance-owner" => await LinuxNativeScenarios.RunInstanceOwnerAsync(args).ConfigureAwait(false),
                "--instance-client" => await LinuxNativeScenarios.RunInstanceClientAsync(args).ConfigureAwait(false),
                "--update-success" => await LinuxUpdateScenarios.RunSuccessfulParentAsync(args).ConfigureAwait(false),
                "--update-readiness-timeout" => await LinuxUpdateScenarios.RunReadinessTimeoutParentAsync(args).ConfigureAwait(false),
                "--update-parent-exit-timeout" => await LinuxUpdateScenarios.RunParentExitTimeoutParentAsync(args).ConfigureAwait(false),
                "--update-readonly-refusal" => await LinuxUpdateScenarios.RunReadOnlyRefusalAsync(args).ConfigureAwait(false),
                _ => UsageError(),
            };
        }
        catch (Exception ex)
        {
            // Do not echo process output, environment, file paths, update tokens, or exception text.
            Console.Error.WriteLine("HARNESS_FAILURE type=" + ex.GetType().Name);
            return 2;
        }
    }

    private static int PrintHarnessInfo()
    {
        var entry = Assembly.GetEntryAssembly();
        var app = typeof(AppUpdateInfo).Assembly;
#pragma warning disable IL3000
        var singleFile = entry is not null && string.IsNullOrEmpty(entry.Location);
#pragma warning restore IL3000
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            entryAssembly = entry?.GetName().Name,
            entryVersion = entry?.GetName().Version?.ToString(3),
            appAssembly = app.GetName().Name,
            appVersion = AppUpdateInfo.Version.ToString(3),
            framework = RuntimeInformation.FrameworkDescription,
            processBasename = Path.GetFileName(Environment.ProcessPath),
            singleFile,
        }));
        return 0;
    }

    private static bool IsLinuxUpdateInvocation(string[] args)
        => args.Any(argument => argument.StartsWith("--linux-update", StringComparison.Ordinal));

    private static void WriteChildStart()
    {
        var path = Environment.GetEnvironmentVariable(ChildStartFileVariable);
        if (!string.IsNullOrWhiteSpace(path)) { HarnessSupport.WriteAtomic(path, ChildIdentityMarker("managed-child-entered") + "\n"); }
    }

    private static string ChildIdentityMarker(string phase)
    {
        var identity = ProcessIdentityReader.Read(Environment.ProcessId)
            ?? throw new InvalidOperationException("The harness child process identity could not be read.");
        return $"{phase}:{identity.ProcessId}:{identity.StartTimeTicks}:{Environment.TickCount64}";
    }

    private static void WriteChildResult(string value)
    {
        var path = Environment.GetEnvironmentVariable(ChildResultFileVariable);
        if (!string.IsNullOrWhiteSpace(path)) { HarnessSupport.WriteAtomic(path, value + "\n"); }
    }

    private static int UsageError()
    {
        Console.Error.WriteLine("HARNESS_USAGE invalid-command-or-arguments");
        return 64;
    }
}
