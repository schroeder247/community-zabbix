// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net.Sockets;

namespace Community.Zabbix.Tests;

/// <summary>Reads a connection the way its peer sees the listener cut it.</summary>
internal static class ListenerCuts
{
    /// <summary>
    /// Reads the next bytes from <paramref name="stream"/>: 0 when the listener ended the connection. Linux and macOS report
    /// the cut as the end of the stream, and Windows can report the same cut as a reset.
    /// </summary>
    /// <param name="stream">The peer's side of the connection.</param>
    /// <returns>The number of bytes read; 0 when the connection was cut.</returns>
    public static async Task<int> ReadOrResetAsync(NetworkStream stream)
    {
        try
        {
            return await stream.ReadAsync(new byte[16], TestContext.Current.CancellationToken);
        }
        catch (IOException reset) when (reset.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            return 0;
        }
    }
}
