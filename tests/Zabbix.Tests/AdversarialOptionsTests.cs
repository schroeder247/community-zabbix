// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// Adversarial tests of the hosts' options: a duration or a size no timer, deadline or buffer can take (zero,
/// negative, beyond what a <see cref="CancellationTokenSource"/> accepts) fails validation at start, named, instead of
/// faulting a loop or every connection later. The rule is held by reflection over every public <c>*Options</c> type of
/// the hosting packages, so a new member is under it the day it is added.
/// </summary>
public sealed class AdversarialOptionsTests
{
    private static readonly Assembly[] Hosting = [typeof(ZabbixSendOptions).Assembly, typeof(ZabbixListenerOptions).Assembly];

    // Members whose zero or negative value is not checked here, with the reason.
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        [$"{nameof(ZabbixComponentHealthCheckOptions)}.{nameof(ZabbixComponentHealthCheckOptions.StaleAfter)}"] = "its own validator (ZabbixComponentHealthCheckStaleAfterValidator), run by AddHealthCheck",
    };

    public static TheoryData<string> Members()
    {
        var members = new TheoryData<string>();
        foreach (PropertyInfo property in OptionsMembers()) members.Add($"{property.DeclaringType!.Name}.{property.Name}");
        return members;
    }

    private static IEnumerable<PropertyInfo> OptionsMembers()
        => Hosting.SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.Name.EndsWith("Options", StringComparison.Ordinal) && type is { IsClass: true, IsAbstract: false } && type.GetConstructor(Type.EmptyTypes) is not null)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite && property.DeclaringType == type))
            .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) is var t && (t == typeof(TimeSpan) || t == typeof(int) || t == typeof(long)));

    private static List<ValidationResult> Validate(object options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        if (options is IValidatableObject validatable && results.Count == 0) results.AddRange(validatable.Validate(new ValidationContext(options)));
        return results;
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void Every_duration_and_size_of_a_hosting_options_type_refuses_a_negative_value(string member)
    {
        if (Exempt.ContainsKey(member)) return;
        PropertyInfo property = OptionsMembers().Single(candidate => $"{candidate.DeclaringType!.Name}.{candidate.Name}" == member);
        object options = Activator.CreateInstance(property.DeclaringType!)!;
        if (options is ZabbixListenerOptions listener) listener.AllowedPeers.Add("127.0.0.1");
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object negative = type == typeof(TimeSpan) ? TimeSpan.FromSeconds(-1) : type == typeof(int) ? -1 : (object)-1L;
        Assert.Empty(Validate(options));                                                    // the defaults are valid

        property.SetValue(options, negative);

        Assert.Contains(Validate(options), result => result.MemberNames.Contains(property.Name) || (result.ErrorMessage ?? string.Empty).Contains(property.Name, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_listener_whose_timeout_is_not_positive_fails_at_start_named_instead_of_faulting_every_connection(int seconds)
    {
        var options = new ZabbixListenerOptions { Timeout = TimeSpan.FromSeconds(seconds) };
        options.AllowedPeers.Add("127.0.0.1");

        ValidateOptionsResult result = new ZabbixListenerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(ZabbixListenerOptions.Timeout), result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_listener_timeout_longer_than_a_deadline_can_be_fails_validation()
    {
        var options = new ZabbixListenerOptions { Timeout = TimeSpan.FromDays(30) };
        options.AllowedPeers.Add("127.0.0.1");

        Assert.True(new ZabbixListenerOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(nameof(ZabbixProxyOptions.ConfigInterval), 0)]
    [InlineData(nameof(ZabbixProxyOptions.DataInterval), 0)]
    [InlineData(nameof(ZabbixProxyOptions.DrainTimeout), -1)]
    [InlineData(nameof(ZabbixProxyOptions.OfflineBuffer), 0)]
    public void A_proxy_whose_cadence_or_drain_window_no_timer_takes_fails_its_generated_validator(string member, int seconds)
    {
        var options = new ZabbixProxyOptions();
        options.Trapper.AllowedPeers.Add("127.0.0.1");
        typeof(ZabbixProxyOptions).GetProperty(member)!.SetValue(options, TimeSpan.FromSeconds(seconds));

        ValidateOptionsResult result = new ZabbixProxyOptionsValidator().Validate("proxy-01", options);

        Assert.True(result.Failed);
        Assert.Contains(member, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proxy_drain_window_of_zero_is_valid_it_means_no_drain()
    {
        var options = new ZabbixProxyOptions { DrainTimeout = TimeSpan.Zero };
        options.Trapper.AllowedPeers.Add("127.0.0.1");

        Assert.False(new ZabbixProxyOptionsValidator().Validate("proxy-01", options).Failed);
    }

    [Fact]
    public void An_active_agent_whose_send_flush_interval_is_zero_fails_through_the_nested_send_options()
    {
        var options = new ZabbixActiveAgentOptions { ServerActive = "127.0.0.1" };
        options.Send.FlushInterval = TimeSpan.Zero;

        ValidateOptionsResult result = new ZabbixActiveAgentOptionsValidator().Validate("agent", options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(ZabbixSendOptions.FlushInterval), result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_passive_agent_check_timeout_of_zero_fails_its_generated_validator()
    {
        var options = new ZabbixPassiveAgentOptions { CheckTimeout = TimeSpan.Zero };
        options.AllowedPeers.Add("127.0.0.1");

        ValidateOptionsResult result = new ZabbixPassiveAgentOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(ZabbixPassiveAgentOptions.CheckTimeout), result.FailureMessage, StringComparison.Ordinal);
    }
}
