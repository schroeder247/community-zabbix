// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.Protocol;

/// <summary>
/// The exception that is thrown for a redirect the exchange could not follow: a second redirect within one exchange (upstream: "sequential redirect
/// responses detected"), a redirect older than the one already followed ("connection was reset because of service being
/// offline"), or a redirect reply reaching a façade over an exchange that does not follow redirects. Nothing was taken at
/// the node that answered, so it is an exchange failure — an <see cref="IOException"/>, retried by a send loop — whose
/// <c>error.type</c> is <c>redirect</c>. Internal plumbing: callers see an <see cref="IOException"/>.
/// </summary>
internal sealed class ZabbixRedirectException : ZabbixWireException, IZabbixErrorTyped
{
    public ZabbixRedirectException()
    {
    }

    public ZabbixRedirectException(string message) : base(message)
    {
    }

    public ZabbixRedirectException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ZabbixRedirectException(string message, ZabbixRedirect redirect) : base(message) => Redirect = redirect;

    /// <summary>The redirect that was not followed.</summary>
    public ZabbixRedirect? Redirect { get; }

    string IZabbixErrorTyped.ErrorType => "redirect";

    internal static ZabbixRedirectException Unfollowed(ZabbixRedirect redirect)
        => new($"The reply redirected the request ({redirect}) and this exchange does not follow redirects.", redirect);
}
