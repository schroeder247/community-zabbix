// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Security;

namespace Community.Zabbix.PowerShell.Commands.Generated;

// The hand-written part of the generated New-ZbxToken: -Generate. token.create stores a token without its secret, and
// token.generate is the one call that answers it, once; without this switch a script must call it through
// Invoke-ZbxApi and read the secret as a plain string.
public sealed partial class NewTokenCommand
{
    /// <summary>
    /// Also generates each new token's secret (<c>token.generate</c>) and adds it to the written token as the property
    /// <c>Secret</c>, a <c>SecureString</c> that <c>Connect-ZbxServer -ApiToken</c> takes as it is. The server answers the
    /// secret this once and cannot show it again: keep it (a secret vault, or <c>Export-Clixml</c>). It is never written to
    /// a stream, a log or an error.
    /// </summary>
    [Parameter]
    public SwitchParameter Generate { get; set; }

    /// <inheritdoc/>
    private protected override async Task<IReadOnlyList<object>> CompleteAsync(IReadOnlyList<ulong> ids, IReadOnlyList<ZabbixToken> created, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(created);
        if (!Generate) return await base.CompleteAsync(ids, created, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ZabbixGeneratedToken> generated = await ZabbixClient.Tokens.GenerateAsync(ids, cancellationToken).ConfigureAwait(false);
        // token.generate answers one token per id.
        var secrets = new Dictionary<ulong, SecureString>();
        foreach (ZabbixGeneratedToken entry in generated)
        {
            if (entry is { TokenId: ulong id, Token: { } token }) secrets[id] = Secure(token);
        }
        var written = new List<object>(created.Count);
        foreach (ZabbixToken record in created)
        {
            var shell = new PSObject(record);
            if (record.TokenId is { } tokenId && secrets.TryGetValue(tokenId, out SecureString? secret))
                shell.Properties.Add(new PSNoteProperty("Secret", secret));
            written.Add(shell);
        }
        return written;
    }

    // The reply's string is the one plain copy of the secret, and nothing keeps it once the SecureString is filled.
    private static SecureString Secure(string token)
    {
        var secret = new SecureString();
        foreach (char c in token) secret.AppendChar(c);
        secret.MakeReadOnly();
        return secret;
    }
}
