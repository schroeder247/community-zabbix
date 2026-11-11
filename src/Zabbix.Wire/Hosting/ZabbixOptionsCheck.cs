// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Hosting;

/// <summary>Holds what a component constructed directly was given to the validators a registration runs at start.</summary>
internal static class ZabbixOptionsCheck
{
    /// <summary>Refuses to start: the component's state is what is wrong (Framework Design Guidelines, standard exception types).</summary>
    /// <typeparam name="TOptions">The options.</typeparam>
    /// <param name="name">What the failure calls the component.</param>
    /// <param name="options">The options to check.</param>
    /// <param name="validators">The validators to run.</param>
    /// <exception cref="InvalidOperationException">A validator failed; the message says what to set, and the inner exception carries each failure.</exception>
    public static void RequireValid<TOptions>(string name, TOptions options, params IEnumerable<IValidateOptions<TOptions>> validators)
        where TOptions : class
    {
        string[] failures = Failures(name, options, validators);
        if (failures.Length > 0)
            throw new InvalidOperationException($"'{name}' cannot start as it is set: {string.Join(" ", failures)}", new OptionsValidationException(name, typeof(TOptions), failures));
    }

    /// <summary>Refuses an argument: what the caller just passed is what is wrong.</summary>
    /// <typeparam name="TOptions">The options.</typeparam>
    /// <param name="parameter">The parameter that holds them.</param>
    /// <param name="options">The options to check.</param>
    /// <param name="validators">The validators to run.</param>
    /// <exception cref="ArgumentException">A validator failed; the inner exception carries each failure.</exception>
    public static void RequireValidArgument<TOptions>(string parameter, TOptions options, params IEnumerable<IValidateOptions<TOptions>> validators)
        where TOptions : class
    {
        string[] failures = Failures(parameter, options, validators);
        if (failures.Length > 0)
            throw new ArgumentException(string.Join(" ", failures), parameter, new OptionsValidationException(parameter, typeof(TOptions), failures));
    }

    private static string[] Failures<TOptions>(string name, TOptions options, IEnumerable<IValidateOptions<TOptions>> validators)
        where TOptions : class
        => [.. validators.Select(validator => validator.Validate(name, options)).Where(result => result.Failed).SelectMany(result => result.Failures ?? [])];
}
