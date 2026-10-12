using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccountManager.App.Services;

/// <summary>Strict command-line and bounded local IPC contracts for the Linux updater.</summary>
internal static class LinuxUpdateProtocol
{
    internal const int ProtocolVersion = 1;
    internal const int MaxMessageBytes = 2048;
    internal const string PipePrefix = "ocam-update-";
    internal const string ProductName = "OpenCodeAccountManager";
    internal const string PlatformName = "linux-x64";

    internal static string GetPhasePipeName(string baseName, LinuxUpdatePipePhase phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        if (!baseName.StartsWith(PipePrefix, StringComparison.Ordinal) ||
            !IsHexOfLength(baseName[PipePrefix.Length..], 32))
        {
            throw new ArgumentException("The update pipe base name is invalid.", nameof(baseName));
        }
        var suffix = phase switch
        {
            LinuxUpdatePipePhase.Entry => "-entry",
            LinuxUpdatePipePhase.Proof => "-proof",
            LinuxUpdatePipePhase.Ready => "-ready",
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
        return baseName + suffix;
    }

    internal static async Task WriteFrameWithinDeadlineAsync(
        Stream stream,
        ReadOnlyMemory<byte> frame,
        long deadline,
        Func<long> getTimestamp,
        CancellationToken cancellationToken)
    {
        await WithDeadlineAsync(deadline, getTimestamp, cancellationToken, async token =>
        {
            EnsureBeforeDeadline(deadline, getTimestamp);
            await stream.WriteAsync(frame, token).ConfigureAwait(false);
            EnsureBeforeDeadline(deadline, getTimestamp);
            await stream.FlushAsync(token).ConfigureAwait(false);
            EnsureBeforeDeadline(deadline, getTimestamp);
            return true;
        }).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadFrameWithinDeadlineAsync(
        Stream stream,
        long deadline,
        Func<long> getTimestamp,
        CancellationToken cancellationToken)
    {
        return await WithDeadlineAsync(deadline, getTimestamp, cancellationToken,
            token => ReadSingleFrameAsync(stream, token)).ConfigureAwait(false);
    }

    internal static void EnsureBeforeDeadline(long deadline, Func<long> getTimestamp)
    {
        if (getTimestamp() >= deadline)
        {
            throw new TimeoutException("Linux updater IPC deadline expired.");
        }
    }

    internal static async Task<T> WithDeadlineAsync<T>(
        long deadline,
        Func<long> getTimestamp,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(getTimestamp);
        ArgumentNullException.ThrowIfNull(operation);
        EnsureBeforeDeadline(deadline, getTimestamp);
        cancellationToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = deadline - getTimestamp();
        linked.CancelAfter((int)Math.Clamp(remaining, 1, int.MaxValue));
        T? result = default;
        var ownsResult = false;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            result = await operation(linked.Token).ConfigureAwait(false);
            ownsResult = true;
            cancellationToken.ThrowIfCancellationRequested();
            linked.Token.ThrowIfCancellationRequested();
            EnsureBeforeDeadline(deadline, getTimestamp);
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DisposeUncommittedResult(result, ownsResult);
            throw new TimeoutException("Linux updater IPC deadline expired.", ex);
        }
        catch
        {
            DisposeUncommittedResult(result, ownsResult);
            throw;
        }
    }

    /// <summary>Connects one owned client at a time; callers retain ownership only on success.</summary>
    internal static async Task<TClient> ConnectClientWithinDeadlineAsync<TClient>(
        string pipeName,
        LinuxUpdatePipePhase phase,
        long deadline,
        Func<long> getTimestamp,
        CancellationToken cancellationToken,
        Func<string, TClient> createClient,
        Func<TClient, int, CancellationToken, Task> connectClient)
        where TClient : IDisposable
    {
        ArgumentNullException.ThrowIfNull(getTimestamp);
        ArgumentNullException.ThrowIfNull(createClient);
        ArgumentNullException.ThrowIfNull(connectClient);
        var endpoint = GetPhasePipeName(pipeName, phase);
        return await WithDeadlineAsync(deadline, getTimestamp, cancellationToken, async token =>
        {
            Exception? lastError = null;
            while (deadline - getTimestamp() > 0)
            {
                EnsureBeforeDeadline(deadline, getTimestamp);
                var client = createClient(endpoint);
                try
                {
                    await connectClient(client, Math.Clamp((int)Math.Min(int.MaxValue, deadline - getTimestamp()), 1, 250), token)
                        .ConfigureAwait(false);
                    EnsureBeforeDeadline(deadline, getTimestamp);
                    return client;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    lastError = ex;
                    DisposeClient(client);
                    if (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); }
                    await Task.Delay(Math.Min(25, Math.Max(1, (int)Math.Clamp(deadline - getTimestamp(), 0, int.MaxValue))), token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    DisposeClient(client);
                    throw;
                }
            }
            throw new TimeoutException("Linux updater IPC connection timed out.", lastError);
        }).ConfigureAwait(false);
    }

    private static void DisposeUncommittedResult<T>(T? result, bool ownsResult)
    {
        if (ownsResult && result is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch { /* Preserve the cancellation, timeout, or operation failure. */ }
        }
    }

    private static void DisposeClient(IDisposable client)
    {
        try { client.Dispose(); }
        catch { /* Preserve the connect failure that caused this cleanup. */ }
    }

    internal static LinuxUpdatePipeSet CreatePipeSet(string baseName) => new(baseName);

    private static readonly string[] RequiredFlags =
    {
        "--linux-update-parent", "--linux-update-start", "--linux-update-pipe",
        "--linux-update-token", "--linux-update-old", "--linux-update-version",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 5,
    };

    internal static bool TryParseArguments(string[]? args, string ownBaseName, out LinuxUpdateLaunchArgs? parsed)
    {
        parsed = null;
        if (args is null || args.Length != RequiredFlags.Length * 2 || string.IsNullOrWhiteSpace(ownBaseName)) { return false; }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var key = args[index];
            if (Array.IndexOf(RequiredFlags, key) < 0 || index + 1 >= args.Length || values.ContainsKey(key)) { return false; }
            values.Add(key, args[index + 1]);
        }
        if (values.Count != RequiredFlags.Length) { return false; }

        if (!TryPositiveInt(values["--linux-update-parent"], out var pid) ||
            !TryPositiveLong(values["--linux-update-start"], out var startTicks)) { return false; }

        var pipeName = values["--linux-update-pipe"];
        if (!pipeName.StartsWith(PipePrefix, StringComparison.Ordinal) || !IsHexOfLength(pipeName[PipePrefix.Length..], 32)) { return false; }

        var token = values["--linux-update-token"];
        if (!IsHexOfLength(token, 64)) { return false; }

        var oldName = values["--linux-update-old"];
        if (!IsValidOldFileName(oldName, "/portable/" + ownBaseName)) { return false; }

        if (!UpdateProbe.TryParseThreePartVersion(values["--linux-update-version"], out var version)) { return false; }

        parsed = new LinuxUpdateLaunchArgs(pid, startTicks, pipeName, token, oldName, version);
        return true;
    }

    internal static bool IsValidOldFileName(string? fileName, string ownExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(ownExecutablePath)) { return false; }
        if (fileName.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0 || Path.IsPathRooted(fileName) || fileName.Contains("..", StringComparison.Ordinal)) { return false; }
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)) { return false; }
        var baseName = Path.GetFileName(ownExecutablePath);
        var prefix = baseName + ".old-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) { return false; }
        return IsHexOfLength(fileName[prefix.Length..], 32);
    }

    internal static bool IsValidReadinessMessage(string? json, Version expectedVersion, string expectedToken, int expectedChildPid)
    {
        if (string.IsNullOrEmpty(json) || EncodingLength(json) > MaxMessageBytes || !IsHexOfLength(expectedToken, 64) || expectedChildPid <= 0) { return false; }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!HasExactProperties(root, "product", "version", "platform", "protocol", "token", "childPid")) { return false; }
            return StringEquals(root, "product", ProductName)
                && StringEquals(root, "version", expectedVersion.ToString(3))
                && StringEquals(root, "platform", PlatformName)
                && IntEquals(root, "protocol", ProtocolVersion)
                && StringEquals(root, "token", expectedToken)
                && IntEquals(root, "childPid", expectedChildPid);
        }
        catch { return false; }
    }

    internal static byte[] EncodeFrame<T>(T message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length is 0 or > MaxMessageBytes) { throw new InvalidDataException("Update IPC message exceeds its fixed limit."); }
        var framed = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, sizeof(int)), body.Length);
        body.CopyTo(framed.AsSpan(sizeof(int)));
        return framed;
    }

    /// <summary>Reads one frame and requires EOF immediately after it; connections are one-shot.</summary>
    internal static async Task<byte[]> ReadSingleFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes) { throw new InvalidDataException("Invalid update IPC frame length."); }
        var body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Unexpected trailing update IPC data.");
        }
        return body;
    }

    internal static T DeserializeFrame<T>(byte[] body)
    {
        if (body.Length is 0 or > MaxMessageBytes) { throw new InvalidDataException("Invalid update IPC message size."); }
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
            ?? throw new InvalidDataException("Invalid update IPC message.");
    }

    internal static string CreateRandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0) { throw new EndOfStreamException("Incomplete update IPC frame."); }
            offset += count;
        }
    }

    private static bool TryPositiveInt(string value, out int number)
    {
        number = 0;
        return value.Length > 0 && value.All(IsAsciiDigit)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    private static bool TryPositiveLong(string value, out long number)
    {
        number = 0;
        return value.Length > 0 && value.All(IsAsciiDigit)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static bool IsHexOfLength(string value, int length)
        => value.Length == length && value.All(c => IsAsciiDigit(c) || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F');

    private static int EncodingLength(string value) => System.Text.Encoding.UTF8.GetByteCount(value);

    private static bool StringEquals(JsonElement element, string property, string expected)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool IntEquals(JsonElement element, string property, int expected)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var actual) && actual == expected;

    private static bool HasExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) { return false; }
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!actual.Add(property.Name)) { return false; }
        }
        return actual.Count == names.Length && names.All(actual.Contains);
    }
}

internal enum LinuxUpdatePipePhase
{
    Entry,
    Proof,
    Ready,
}

/// <summary>Three pre-bound, same-user one-shot endpoints for one locally generated base name.</summary>
internal sealed class LinuxUpdatePipeSet : IDisposable
{
    private readonly NamedPipeServerStream _proof;
    private readonly NamedPipeServerStream _ready;

    internal LinuxUpdatePipeSet(string baseName)
    {
        // Construct in locals so a bind failure disposes the already-created siblings before
        // the caller can begin a destructive swap.
        var entry = CreateServer(LinuxUpdateProtocol.GetPhasePipeName(baseName, LinuxUpdatePipePhase.Entry));
        NamedPipeServerStream? proof = null;
        NamedPipeServerStream? ready = null;
        try
        {
            proof = CreateServer(LinuxUpdateProtocol.GetPhasePipeName(baseName, LinuxUpdatePipePhase.Proof));
            ready = CreateServer(LinuxUpdateProtocol.GetPhasePipeName(baseName, LinuxUpdatePipePhase.Ready));
        }
        catch
        {
            entry.Dispose();
            proof?.Dispose();
            ready?.Dispose();
            throw;
        }
        Entry = entry;
        _proof = proof!;
        _ready = ready!;
    }

    internal NamedPipeServerStream Entry { get; }

    internal NamedPipeServerStream Proof => _proof;

    internal NamedPipeServerStream Ready => _ready;

    internal int PreparedCount => 3;

    internal void ReleaseProof()
    {
        _proof.Dispose();
    }

    public void Dispose()
    {
        Entry.Dispose();
        _proof.Dispose();
        _ready.Dispose();
    }

    private static NamedPipeServerStream CreateServer(string name)
        => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 2048, 2048);
}

internal sealed record LinuxUpdateLaunchArgs(
    int ParentPid,
    long ParentStartUtcTicks,
    string PipeName,
    string Token,
    string OldFileName,
    Version ExpectedVersion);

internal sealed record LinuxUpdateReadyMessage(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("childPid")] int ChildPid);

internal sealed record LinuxUpdateEntryMessage(
    string Product,
    string Version,
    string Platform,
    int Protocol,
    string Token,
    int ChildPid,
    long ChildStartUtcTicks);

internal sealed record LinuxUpdateProofMessage(
    string Product,
    string Version,
    string Platform,
    int Protocol,
    string Token,
    int ChildPid,
    bool Accepted,
    LinuxUpdateSwap.FileProof? OldFile,
    LinuxUpdateSwap.FileProof? NewFile);
