// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents an API object the web frontend has a page for: a host, an item, a dashboard, and every other record
/// <see cref="ZabbixPageKind"/> names, and a <see cref="ZabbixPageTarget"/> for a bare id. <c>ZabbixFrontend.UriFor</c>
/// takes any of them: <c>frontend.UriFor(host)</c>, <c>frontend.UriFor(item, ZabbixPageView.Graph)</c>.
/// </summary>
/// <example>
/// <code language="csharp">
/// ZabbixFrontend frontend = await zabbix.GetFrontendAsync();
/// foreach (IZabbixFrontendPage page in pages)
///     Console.WriteLine(frontend.UriFor(page));
/// </code>
/// </example>
public interface IZabbixFrontendPage
{
    /// <summary>
    /// Gets what the frontend's routes read off the object: its kind, its id, and the name, context, host, event source
    /// or trigger a route asks for, as far as the record carries them.
    /// </summary>
    /// <returns>The object's page target; its <see cref="ZabbixPageTarget.Id"/> is <c>0</c> when the record was read without its id.</returns>
    ZabbixPageTarget ToPageTarget();
}
