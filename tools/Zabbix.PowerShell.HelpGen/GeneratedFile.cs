// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Xml.Linq;

namespace Community.Zabbix.PowerShell.HelpGen;

/// <summary>
/// Writes a generated file only when its content changed, so an unchanged build leaves its timestamp alone and nothing
/// downstream (the output copy's <c>PreserveNewest</c>, docfx, an editor) sees a change that is not one.
/// </summary>
internal static class GeneratedFile
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> (creating its folder) unless the file already holds exactly it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="content">Its bytes.</param>
    /// <returns><see langword="true"/> when the file was written.</returns>
    public static bool Write(string path, ReadOnlySpan<byte> content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return false;
        File.WriteAllBytes(path, content);
        return true;
    }

    /// <summary>Writes <paramref name="text"/> as UTF-8 without a byte order mark (<see cref="File.WriteAllText(string, string?)"/>'s encoding).</summary>
    /// <param name="path">The file.</param>
    /// <param name="text">Its text.</param>
    /// <returns><see langword="true"/> when the file was written.</returns>
    public static bool Write(string path, string text) => Write(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));

    /// <summary>Writes <paramref name="document"/> as UTF-8 with a byte order mark and an <c>encoding="utf-8"</c> declaration.</summary>
    /// <param name="path">The file.</param>
    /// <param name="document">Its XML.</param>
    /// <returns><see langword="true"/> when the file was written.</returns>
    public static bool Write(string path, XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, Encoding.UTF8)) document.Save(writer);
        return Write(path, stream.ToArray());
    }
}
