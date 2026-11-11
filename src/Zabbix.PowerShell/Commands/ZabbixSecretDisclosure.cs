// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Provides the disclosure decision every command that can emit plaintext secrets shares: redact by default, unless
/// <c>-Unredacted</c> was given or the operator confirms through <c>PSCmdlet.ShouldContinue</c>.
/// A host that cannot prompt (no UI, e.g. this module's own test harness, or a background job) throws out of
/// <c>ShouldContinue</c>; that failure-to-prompt is treated as a decline — fail closed, the finding stays redacted —
/// rather than as a terminating error, so an unattended run gets a safe answer instead of a crash.
/// </summary>
internal static class ZabbixSecretDisclosure
{
    public static bool ShouldDisclose(PSCmdlet cmdlet, int plaintextCount, bool includeSecrets, string action)
    {
        ArgumentNullException.ThrowIfNull(cmdlet);
        if (plaintextCount == 0) return true;
        if (includeSecrets) return true;
        cmdlet.WriteWarning(
            $"{plaintextCount} plaintext credential(s) (media type / IPMI / OAuth passwords) were found and are redacted by default. " +
            "Pass -Unredacted to include them.");
        try
        {
            bool yesToAll = false, noToAll = false;
            return cmdlet.ShouldContinue(
                $"{action} contains {plaintextCount} plaintext credential(s). Include them unredacted?",
                "Plaintext credentials", hasSecurityImpact: true, ref yesToAll, ref noToAll);
        }
        catch (Exception failure) when (failure is not PipelineStoppedException and not FlowControlException and not OutOfMemoryException)
        {
            // A host that cannot prompt (no console: a CI runner) answers no, and says why; Ctrl+C at the prompt is a stop,
            // never a "no".
            cmdlet.WriteWarning($"the host cannot ask whether to include them ({failure.Message}); they stay redacted: pass -Unredacted to include them");
            return false;
        }
    }
}
