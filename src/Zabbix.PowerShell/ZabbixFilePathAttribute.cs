// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A file the command reads (<c>-Certificate</c>, <c>-CertificateKey</c>, <c>-CertificateAuthority</c>,
/// <c>-CertificateRevocationList</c>), as the full file-system path: a relative path or a PowerShell path
/// (<c>~/pki/ca.crt</c>, <c>.\client.key</c>) is resolved against the current PowerShell location, which is not the
/// process's working directory .NET resolves against, and a <see cref="FileInfo"/> (<c>Get-Item</c>) gives its own.
/// Whether the file exists is the reader's to say, when the connection is built.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixFilePathAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        ArgumentNullException.ThrowIfNull(engineIntrinsics);
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        return input switch
        {
            FileInfo file => file.FullName,
            string text when !string.IsNullOrWhiteSpace(text) => Resolve(engineIntrinsics, text),
            _ => input,
        };
    }

    /// <summary>The file-system path <paramref name="path"/> names from the current PowerShell location.</summary>
    /// <param name="engineIntrinsics">The engine the argument is bound in.</param>
    /// <param name="path">A path as the user wrote it.</param>
    /// <exception cref="ArgumentTransformationMetadataException">The path is not a file-system path.</exception>
    internal static string Resolve(EngineIntrinsics engineIntrinsics, string path)
    {
        try
        {
            string resolved = engineIntrinsics.SessionState.Path.GetUnresolvedProviderPathFromPSPath(path, out ProviderInfo provider, out _);
            if (provider.ImplementingType != typeof(Microsoft.PowerShell.Commands.FileSystemProvider))
                throw new ArgumentTransformationMetadataException($"The path is on the {provider.Name} drive: give a file-system path.");
            return resolved;
        }
        catch (Exception failure) when (failure is ItemNotFoundException or System.Management.Automation.DriveNotFoundException or ProviderNotFoundException or NotSupportedException or PSArgumentException)
        {
            throw new ArgumentTransformationMetadataException("The path is not a file-system path: " + failure.Message, failure);
        }
    }
}
