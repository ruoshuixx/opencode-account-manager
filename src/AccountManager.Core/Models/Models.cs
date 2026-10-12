namespace AccountManager.Core.Models;

/// <summary>Which family an account row belongs to.</summary>
public enum AccountKind
{
    Codex,
    Api,
}

/// <summary>
/// One display/managed account row. Mirrors the PowerShell account projection:
/// Codex rows come from an OAuth credential, API rows from a provider definition
/// (optionally paired with a local key credential).
/// </summary>
public sealed class AccountRow
{
    /// <summary>Stable routing key. Credential id for saved rows, provider id for config-only rows.</summary>
    public string Key { get; set; } = string.Empty;

    public string CredentialId { get; set; } = string.Empty;

    public bool Active { get; set; }

    public AccountKind Kind { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Integration/provider id (e.g. <c>openai</c> or a <c>cockpit-*</c> id).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Raw, unmasked identity used for exact matching (never for display).</summary>
    public string Identity { get; set; } = string.Empty;

    public string Workspace { get; set; } = string.Empty;

    public string Plan { get; set; } = string.Empty;

    public string Expires { get; set; } = string.Empty;

    /// <summary>ChatGPT subscription expiry, if the source data provided it (else null).</summary>
    public DateTimeOffset? SubscriptionExpires { get; set; }

    public string Status { get; set; } = string.Empty;

    public string StatusCode { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public List<string> Models { get; set; } = new();

    public List<string> ModelIds { get; set; } = new();

    /// <summary>Raw credential value (contains secrets). Never displayed or logged.</summary>
    public System.Text.Json.Nodes.JsonObject? Value { get; set; }

    /// <summary>Provider projection used for import/update output. Never contains secrets.</summary>
    public ProviderProjection? Definition { get; set; }
}

/// <summary>Display projection of a provider definition (no secrets).</summary>
public sealed class ProviderProjection
{
    public string Name { get; set; } = string.Empty;

    public string Package { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public List<string> Models { get; set; } = new();

    public List<string> ModelIds { get; set; } = new();
}

/// <summary>Result of a manual switch (activate) operation.</summary>
public sealed class SwitchResult
{
    public bool Changed { get; set; }

    public string Label { get; set; } = string.Empty;

    public string CredentialID { get; set; } = string.Empty;

    public bool Active { get; set; }

    public string Status { get; set; } = string.Empty;

    public bool ReauthRequired { get; set; }
}

/// <summary>Result of a delete operation.</summary>
public sealed class DeleteResult
{
    public bool Removed { get; set; }

    public AccountKind Kind { get; set; }

    public string CredentialID { get; set; } = string.Empty;

    public string ProviderID { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Backup { get; set; } = string.Empty;

    public string Warning { get; set; } = string.Empty;
}

/// <summary>Result of an API provider save (settings and/or key rotation).</summary>
public sealed class ApiSaveResult
{
    public bool Updated { get; set; }

    public bool Complete { get; set; } = true;

    /// <summary>Null when not checked; file/credential completion is separate from runtime adoption.</summary>
    public bool? RuntimeVerified { get; set; }

    public string CredentialID { get; set; } = string.Empty;

    public string ProviderID { get; set; } = string.Empty;

    public string Backup { get; set; } = string.Empty;

    public string Warning { get; set; } = string.Empty;
}

/// <summary>Result of adding an account; Complete=false means the write needs manual inspection.</summary>
public sealed class AccountAddResult
{
    public bool Saved { get; init; }
    public bool Complete { get; init; } = true;
    public bool? RuntimeVerified { get; init; }
    public AccountKind Kind { get; init; }
    public string CredentialID { get; init; } = string.Empty;
    public string ProviderID { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Backup { get; init; } = string.Empty;
    public string Warning { get; init; } = string.Empty;
}

/// <summary>Result of an export.</summary>
public sealed class ExportResult
{
    public string Path { get; set; } = string.Empty;

    public int Count { get; set; }

    public string Warning { get; set; } = string.Empty;
}

/// <summary>A read-only snapshot of the local OpenCode state.</summary>
public sealed class AccountSnapshot
{
    public List<AccountRow> Accounts { get; set; } = new();

    public int ProviderCount { get; set; }

    /// <summary>Non-empty when the provider config could not be read (Codex rows still valid).</summary>
    public string Warning { get; set; } = string.Empty;
}
