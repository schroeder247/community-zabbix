// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Reads and writes the document files the configuration commands take and write (<c>-Path</c>, <c>-LiteralPath</c>),
/// on the pipeline thread, answering a failure as the error record to write: a path that names no file
/// is <c>ObjectNotFound</c>, a file <c>-NoClobber</c> keeps is <c>ResourceExists</c>, a denied one
/// <c>PermissionDenied</c>, any other I/O failure <c>ReadError</c>/<c>WriteError</c>. The caller writes it
/// non-terminating, so one bad file among piped ones stops only its own record (as <c>Get-Content</c> does), or, for a
/// <c>Test-</c>, reports it as the test's finding. A path is a PowerShell path (a drive, <c>~</c>, a relative path
/// against the current location) resolved by the provider: <c>-Path</c> with its wildcards expanded, <c>-LiteralPath</c>
/// without.
/// </summary>
internal static class ZabbixDocumentFile
{
    /// <summary>
    /// Reads every file <paramref name="paths"/> names: <c>-Path</c>'s PowerShell wildcards expanded by the provider
    /// (<c>*.json</c>; a pattern that matches nothing names no file), or <c>-LiteralPath</c>'s paths as written. Each
    /// file comes with its text, or with the failure to resolve or read it as the error record to write.
    /// </summary>
    /// <param name="cmdlet">The command reading.</param>
    /// <param name="paths">The paths as given.</param>
    /// <param name="literal">Whether the paths are literal (<c>-LiteralPath</c>) rather than patterns (<c>-Path</c>).</param>
    public static IEnumerable<(string Path, string Text, ErrorRecord? Failure)> ReadAll(PSCmdlet cmdlet, IEnumerable<string> paths, bool literal)
    {
        ArgumentNullException.ThrowIfNull(cmdlet);
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
        {
            IReadOnlyList<string> files;
            ErrorRecord? unresolved = null;
            if (literal)
            {
                files = [path];
            }
            else
            {
                try
                {
                    files = [.. cmdlet.GetResolvedProviderPathFromPSPath(path, out _)];
                }
                catch (Exception failure) when (Category(failure, ErrorCategory.ReadError) is { } category)
                {
                    files = [];
                    unresolved = new ErrorRecord(failure, category == ErrorCategory.ObjectNotFound ? "ZabbixFileNotFound" : "ZabbixFileReadFailed", category, path);
                }
            }
            if (unresolved is not null)
            {
                yield return (path, string.Empty, unresolved);
                continue;
            }
            foreach (string file in files)
            {
                ErrorRecord? failure = Read(cmdlet, file, out string text);
                yield return (file, text, failure);
            }
        }
    }

    /// <summary>
    /// The file an export writes to: <c>-LiteralPath</c> as written, or <c>-Path</c> with its wildcards expanded when
    /// they match one existing file (as <c>Export-Csv -Path</c> does), as written when they match none, and a failure when
    /// they match several.
    /// </summary>
    /// <param name="cmdlet">The command writing.</param>
    /// <param name="path">The path as given.</param>
    /// <param name="literal">Whether the path is literal (<c>-LiteralPath</c>).</param>
    /// <param name="file">The file to write.</param>
    public static ErrorRecord? ResolveOutput(PSCmdlet cmdlet, string path, bool literal, out string file)
    {
        ArgumentNullException.ThrowIfNull(cmdlet);
        file = path;
        if (literal || !WildcardPattern.ContainsWildcardCharacters(path)) return null;
        try
        {
            string[] matches = [.. cmdlet.GetResolvedProviderPathFromPSPath(path, out _)];
            if (matches.Length > 1)
            {
                return new ErrorRecord(new PSArgumentException($"{path} matches {matches.Length} files; an export writes to one. Name one, or use -LiteralPath."),
                    "ZabbixFileAmbiguous", ErrorCategory.InvalidArgument, path);
            }
            if (matches.Length == 1) file = matches[0];
        }
        catch (ItemNotFoundException)
        {
            // Matches nothing: a new file of that name.
        }
        return null;
    }

    /// <summary>Reads the file at <paramref name="literalPath"/>: <see langword="null"/>, or the failure as an error record.</summary>
    /// <param name="cmdlet">The command reading.</param>
    /// <param name="literalPath">The path as given.</param>
    /// <param name="text">The file's text; empty on failure.</param>
    public static ErrorRecord? Read(PSCmdlet cmdlet, string literalPath, out string text)
    {
        text = string.Empty;
        try
        {
            text = File.ReadAllText(cmdlet.GetUnresolvedProviderPathFromPSPath(literalPath));
            return null;
        }
        catch (Exception failure) when (Category(failure, ErrorCategory.ReadError) is { } category)
        {
            return new ErrorRecord(failure, category == ErrorCategory.ObjectNotFound ? "ZabbixFileNotFound" : "ZabbixFileReadFailed", category, literalPath);
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> to the file at <paramref name="literalPath"/> (UTF-8, no byte order mark), replacing
    /// it unless <paramref name="noClobber"/>: <see langword="null"/>, or the failure as an error record.
    /// </summary>
    /// <param name="cmdlet">The command writing.</param>
    /// <param name="literalPath">The path as given.</param>
    /// <param name="text">The text.</param>
    /// <param name="noClobber">Whether an existing file is left as it is (an error) rather than replaced.</param>
    public static ErrorRecord? Write(PSCmdlet cmdlet, string literalPath, string text, bool noClobber)
    {
        try
        {
            string path = cmdlet.GetUnresolvedProviderPathFromPSPath(literalPath);
            using var file = new FileStream(path, noClobber ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(text);
            return null;
        }
        catch (IOException exists) when (noClobber && exists is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException) && File.Exists(cmdlet.GetUnresolvedProviderPathFromPSPath(literalPath)))
        {
            return new ErrorRecord(exists, "ZabbixFileExists", ErrorCategory.ResourceExists, literalPath)
            {
                ErrorDetails = new ErrorDetails($"{literalPath} exists and -NoClobber was given; nothing was written"),
            };
        }
        catch (Exception failure) when (Category(failure, ErrorCategory.WriteError) is { } category)
        {
            return new ErrorRecord(failure, category == ErrorCategory.ObjectNotFound ? "ZabbixFileNotFound" : "ZabbixFileWriteFailed", category, literalPath);
        }
    }

    // A path PowerShell cannot resolve (no such drive or provider, not a file-system path) or a file the system refuses.
    private static ErrorCategory? Category(Exception failure, ErrorCategory otherwise) => failure switch
    {
        FileNotFoundException or DirectoryNotFoundException or System.IO.DriveNotFoundException or System.Management.Automation.DriveNotFoundException or ItemNotFoundException => ErrorCategory.ObjectNotFound,
        UnauthorizedAccessException => ErrorCategory.PermissionDenied,
        ProviderNotFoundException or NotSupportedException or ArgumentException => ErrorCategory.InvalidArgument,
        IOException => otherwise,
        _ => null,
    };
}
