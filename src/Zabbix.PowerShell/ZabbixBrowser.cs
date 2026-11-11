// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Diagnostics;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the hand-off of a page to the user's browser, as PowerShell's own <c>Start-Process</c> hands it a URL: a
/// shell-executed <see cref="ProcessStartInfo"/>, which .NET resolves per platform (the URL's registered handler on
/// Windows, <c>open</c> on macOS, <c>xdg-open</c> and its fallbacks on Linux and the BSDs).
/// </summary>
internal static class ZabbixBrowser
{
    /// <summary>Hands <paramref name="page"/> to the browser.</summary>
    /// <param name="page">The page.</param>
    /// <returns><see langword="null"/> when it was handed over; else why not, for the warning.</returns>
    public static string? Open(Uri page)
    {
        ArgumentNullException.ThrowIfNull(page);
        // On Linux and the BSDs xdg-open without a graphical session falls back to a text browser in the same terminal
        // (or fails after the fact), so a session with neither display variable (SSH, a CI runner, a container) has none.
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return "this session has no graphical display (neither DISPLAY nor WAYLAND_DISPLAY is set)";
        }
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception failed)
        {
            return $"no program opens the page here ({failed.Message})";
        }
    }
}
