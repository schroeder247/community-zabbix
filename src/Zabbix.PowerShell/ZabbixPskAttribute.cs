// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A pre-shared key (<c>-Psk</c>), as a <see cref="SecureString"/>: a secure string passes through, and the path of a
/// key file (Zabbix's <c>TLSPSKFile</c>: the key's hex digits on one line) or a <see cref="FileInfo"/> is read into one,
/// relative to the current PowerShell location. Any other text is refused without being repeated in the error: it may be
/// the key itself, typed where the history keeps it.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixPskAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        ArgumentNullException.ThrowIfNull(engineIntrinsics);
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        string? path = input switch
        {
            FileInfo file => file.FullName,
            string text => ResolveOrNull(engineIntrinsics, text) ?? throw NoFile(),
            _ => null,
        };
        if (path is null) return input;
        if (!File.Exists(path)) throw NoFile();
        try
        {
            return Read(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentTransformationMetadataException($"The key file '{path}' cannot be read: {failure.Message}", failure);
        }
    }

    // The failure never carries the text: a resolution error's message would repeat it.
    private static string? ResolveOrNull(EngineIntrinsics engineIntrinsics, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return ZabbixFilePathAttribute.Resolve(engineIntrinsics, text);
        }
        catch (ArgumentTransformationMetadataException)
        {
            return null;
        }
    }

    private static ArgumentTransformationMetadataException NoFile() => new(
        "-Psk takes a SecureString or the path of a key file, and the text given names no file. A key typed as text stays in the history: read it with Read-Host -AsSecureString, or keep it in a file.");

    // The key file's first line, its surrounding white space trimmed, as upstream reads TLSPSKFile; the bytes and characters
    // it passed through are cleared once the secure string holds it.
    private static SecureString Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        char[] text = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int length = Encoding.UTF8.GetChars(bytes, text);
            ReadOnlySpan<char> line = text.AsSpan(0, length);
            int end = line.IndexOfAny('\r', '\n');
            if (end >= 0) line = line[..end];
            line = line.Trim();
            var key = new SecureString();
            foreach (char c in line) key.AppendChar(c);
            key.MakeReadOnly();
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Array.Clear(text);
        }
    }
}
