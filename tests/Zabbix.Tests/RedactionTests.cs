// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The taxonomy fails closed: a secret never reaches the log even when the
/// template names it, and the fallback erases a class nobody gave a policy. Driven through the real redaction pipeline
/// and the extended <c>[LoggerMessage]</c> generator the SDK's own events use.
/// </summary>
public sealed partial class RedactionTests
{
    private const string Token = "tok-2c26b46b68ffc68ff99b453c1d304134";

    [LoggerMessage(EventId = 9901, Level = LogLevel.Warning, Message = "Token {Token} for {User}")]
    private static partial void TokenFor(ILogger logger, [ZabbixSecret] string token, [ZabbixPersonal] string user);

    private static FakeLogRecord LogOnce(Action<ILoggingBuilder> logging)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddFakeLogging();
            logging(builder);
        });
        using ServiceProvider provider = services.BuildValidated();
        TokenFor(provider.GetRequiredService<ILoggerFactory>().CreateLogger("test"), Token, "alice");
        return provider.GetFakeLogCollector().LatestRecord;
    }

    private static IEnumerable<string?> Rendered(FakeLogRecord record)
        => (record.StructuredState?.Select(pair => pair.Value) ?? []).Append(record.Message);

    [Fact]
    public void A_secret_never_renders_even_when_the_template_names_it()
    {
        FakeLogRecord record = LogOnce(logging => logging.AddZabbixRedaction());

        Assert.DoesNotContain(Rendered(record), text => text?.Contains(Token, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void The_fallback_erases_a_class_that_has_no_policy()
    {
        FakeLogRecord record = LogOnce(logging => logging.AddZabbixRedaction());

        Assert.DoesNotContain(Rendered(record), text => text?.Contains("alice", StringComparison.Ordinal) == true);
    }

    // The measured claim behind ZabbixDataClassifications: without any redaction registered, the extended generator
    // still keeps a classified value out of the message and the state.
    [Fact]
    public void A_host_that_never_enables_redaction_still_does_not_render_a_secret()
    {
        FakeLogRecord record = LogOnce(_ => { });

        Assert.DoesNotContain(Rendered(record), text => text?.Contains(Token, StringComparison.Ordinal) == true);
    }

    private const string SecretKey = "web.page.get[intranet,/admin,user:hunter2]";

    [Theory]
    [InlineData("web.page.get[intranet,/admin,user:hunter2]", "web.page.get[***]")]
    [InlineData("db.odbc.select[q,,\"Driver=x;Pwd=hunter2\"]", "db.odbc.select[***]")]
    [InlineData("system.uptime", "system.uptime")]
    [InlineData("vfs.fs.size[/,pfree]", "vfs.fs.size[***]")]
    [InlineData("[hunter2]", "")]
    [InlineData("not a key hunter2", "")]
    [InlineData("", "")]
    public void The_item_key_redactor_keeps_the_name_and_masks_the_parameters(string key, string redacted)
    {
        var redactor = new ZabbixItemKeyRedactor();

        Assert.Equal(redacted, redactor.Redact(key));
        Assert.Equal(redacted.Length, redactor.GetRedactedLength(key));
    }

    [Fact]
    public void The_item_key_redactor_refuses_a_destination_too_short()
        => Assert.Throws<ArgumentException>("destination", () => new ZabbixItemKeyRedactor().Redact("vfs.fs.size[/]", new char[5]));

    [Fact]
    public void The_wire_getter_logs_an_item_key_s_name_and_masks_its_parameters()
    {
        FakeLogRecord record = LogFromWire(logging => logging.AddZabbixRedaction());

        Assert.DoesNotContain(Rendered(record), text => text?.Contains("hunter2", StringComparison.Ordinal) == true);
        Assert.Contains(Rendered(record), text => text?.Contains("web.page.get[***]", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void A_wire_only_application_registers_the_item_key_redactor_on_its_own_redaction_builder()
    {
        FakeLogRecord record = LogFromWire(logging =>
        {
            logging.EnableRedaction();
            logging.Services.AddRedaction(redaction => redaction
                .SetRedactor<ErasingRedactor>(ZabbixDataClassifications.Secret)
                .SetRedactor<ZabbixItemKeyRedactor>(ZabbixDataClassifications.ItemKey));
        });

        Assert.DoesNotContain(Rendered(record), text => text?.Contains("hunter2", StringComparison.Ordinal) == true);
        Assert.Contains(Rendered(record), text => text?.Contains("web.page.get[***]", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void The_wire_getter_never_renders_an_item_key_on_a_plain_logger()
    {
        FakeLogRecord record = LogFromWire(_ => { });

        Assert.DoesNotContain(Rendered(record), text => text?.Contains("hunter2", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void A_pass_through_registered_after_the_policy_shows_the_whole_key()
    {
        FakeLogRecord record = LogFromWire(logging =>
        {
            logging.AddZabbixRedaction();
            logging.Services.AddRedaction(redaction => redaction.SetRedactor<NullRedactor>(ZabbixDataClassifications.ItemKey));
        });

        Assert.Contains(Rendered(record), text => text?.Contains(SecretKey, StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("Unknown host \"web-01\" for key \"web.page.get[x,,user:hunter2]\"", "[redacted]")]
    [InlineData("x", "[redacted]")]
    [InlineData("", "")]
    public void The_peer_text_redactor_marks_a_text_and_keeps_an_empty_one_empty(string text, string redacted)
    {
        var redactor = new ZabbixPeerTextRedactor();

        Assert.Equal(redacted, redactor.Redact(text));
        Assert.Equal(redacted.Length, redactor.GetRedactedLength(text));
    }

    [Fact]
    public void The_peer_text_redactor_refuses_a_destination_too_short()
        => Assert.Throws<ArgumentException>("destination", () => new ZabbixPeerTextRedactor().Redact("refused", new char[5]));

    private const string PeerInfo = "Unsupported item key \"web.page.get[intranet,/admin,user:hunter2]\".";

    // An erased value would leave the event ending in ": "; the policy marks it instead, and never renders it.
    [Fact]
    public void The_policy_marks_a_peer_s_text_where_the_event_names_it()
    {
        FakeLogRecord record = LogPeerText(logging => logging.AddZabbixRedaction());

        Assert.DoesNotContain(Rendered(record), text => text?.Contains("hunter2", StringComparison.Ordinal) == true);
        Assert.EndsWith(": [redacted]", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pass_through_registered_after_the_policy_shows_the_peer_s_text()
    {
        FakeLogRecord record = LogPeerText(logging =>
        {
            logging.AddZabbixRedaction();
            logging.Services.AddRedaction(redaction => redaction.SetRedactor<NullRedactor>(ZabbixDataClassifications.PeerText));
        });

        Assert.EndsWith(": " + PeerInfo, record.Message, StringComparison.Ordinal);
    }

    // The logger's discriminator (on by default) hands a redactor "value:Tag": a bare key would not parse as a key and would be
    // erased, and a pass-through would render the tag after the text. The policy turns the discriminator off.
    [Fact]
    public void The_policy_keeps_a_bare_key_s_name()
    {
        FakeLogRecord record = LogPeerText(logging => logging.AddZabbixRedaction());

        Assert.StartsWith("Agent agent:10050 refused the request for item key \"system.uptime\": ", record.Message, StringComparison.Ordinal);
    }

    private static FakeLogRecord LogPeerText(Action<ILoggingBuilder> logging)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddFakeLogging();
            logging(builder);
        });
        using ServiceProvider provider = services.BuildValidated();
        WireLog.GetterRefused(provider.GetRequiredService<ILoggerFactory>().CreateLogger("test"), "agent:10050", ZabbixItemKey.Parse("system.uptime", null), PeerInfo);
        return provider.GetFakeLogCollector().LatestRecord;
    }

    private static FakeLogRecord LogFromWire(Action<ILoggingBuilder> logging)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddFakeLogging().SetMinimumLevel(LogLevel.Debug);
            logging(builder);
        });
        using ServiceProvider provider = services.BuildValidated();
        WireLog.GetterAnswered(provider.GetRequiredService<ILoggerFactory>().CreateLogger("test"), "agent:10050", ZabbixItemKey.Parse(SecretKey, null), supported: true);
        return provider.GetFakeLogCollector().LatestRecord;
    }
}
