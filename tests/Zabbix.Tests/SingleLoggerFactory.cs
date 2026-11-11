// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Tests;

/// <summary>A logger factory that gives every category the one logger a test watches, for a constructor that takes a factory.</summary>
/// <param name="logger">The logger every category gets.</param>
internal sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
{
    /// <summary>A factory over <paramref name="logger"/>, or none when there's no logger.</summary>
    /// <param name="logger">The logger, or <see langword="null"/>.</param>
    /// <returns>The factory, or <see langword="null"/>.</returns>
    public static ILoggerFactory? Of(ILogger? logger) => logger is null ? null : new SingleLoggerFactory(logger);

    public ILogger CreateLogger(string categoryName) => logger;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
