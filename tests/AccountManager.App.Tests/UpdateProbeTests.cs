using System.Text.Json;
using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

public sealed class UpdateProbeTests
{
    [Fact]
    public void TryHandle_EmitsOnlyTheActualNumericVersionAndCurrentPlatformForExactProbe()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            var handled = UpdateProbe.TryHandle(new[] { "--update-probe" }, out var exitCode);

            Assert.True(handled);
            Assert.Equal(0, exitCode);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("OpenCodeAccountManager", json.RootElement.GetProperty("product").GetString());
            Assert.Equal(AppUpdateInfo.Version.ToString(3), json.RootElement.GetProperty("version").GetString());
            var expectedPlatform = OperatingSystem.IsLinux() ? "linux-x64" : "win-x64";
            Assert.Equal(expectedPlatform, json.RootElement.GetProperty("platform").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("protocol").GetInt32());
            Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void TryHandle_RejectsProbeMixedWithOtherArgumentsWithoutEmittingJson()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            var handled = UpdateProbe.TryHandle(new[] { "--update-probe", "--linux-update-parent", "12" }, out var exitCode);

            Assert.True(handled);
            Assert.NotEqual(0, exitCode);
            Assert.Empty(output.ToString());
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void TryHandle_DoesNotClaimOrdinaryStartupArguments()
    {
        Assert.False(UpdateProbe.TryHandle(new[] { "--help" }, out _));
    }

    [Fact]
    public void TryHandle_RejectsUnknownReservedProbeArguments()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            var handled = UpdateProbe.TryHandle(new[] { "--update-probe-extra" }, out var exitCode);

            Assert.True(handled);
            Assert.NotEqual(0, exitCode);
            Assert.Empty(output.ToString());
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void CandidateProbeOutputMustBeExactlyOneJsonLine()
    {
        const string json = "{\"product\":\"OpenCodeAccountManager\",\"version\":\"1.2.3\",\"platform\":\"linux-x64\",\"protocol\":1}";

        Assert.True(UpdateProbe.IsExpectedOutput(json + "\n", new Version(1, 2, 3), "linux-x64"));
        Assert.False(UpdateProbe.IsExpectedOutput(json + "\n\n", new Version(1, 2, 3), "linux-x64"));
        Assert.False(UpdateProbe.IsExpectedOutput(json + "\nextra", new Version(1, 2, 3), "linux-x64"));
    }

    [Fact]
    public void Program_HandlesProbeBeforeSingleInstanceStartup()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            var exitCode = Program.Main(new[] { "--update-probe" });

            Assert.Equal(0, exitCode);
            Assert.Null(Program.Instance);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("OpenCodeAccountManager", json.RootElement.GetProperty("product").GetString());
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void Program_RejectsMixedProbeBeforeNormalStartup()
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            var exitCode = Program.Main(new[] { "--update-probe", "unexpected" });

            Assert.NotEqual(0, exitCode);
            Assert.Null(Program.Instance);
            Assert.Empty(output.ToString());
        }
        finally { Console.SetOut(original); }
    }
}
