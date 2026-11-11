// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// The taxonomy fails closed: a secret never reaches the log even when the
/// template names it, and the fallback erases a class nobody gave a policy. Driven through the real redaction pipeline
/// and the extended <c>[LoggerMessage]</c> generator the SDK's own events use, composed as an application composes it:
/// <c>EnableRedaction()</c> with its defaults, and <c>AddRedaction(r =&gt; r.SetZabbixRedactors())</c>.
/// </summary>
public sealed partial class RedactionTests
{
    private const string Token = "tok-2c26b46b68ffc68ff99b453c1d304134";

    /// <summary>What an application on the SDK's redactors writes: redaction on, with the logger's own defaults.</summary>
    private static void SdkRedactors(ILoggingBuilder logging)
    {
        logging.EnableRedaction();
        logging.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());
    }

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
        FakeLogRecord record = LogOnce(SdkRedactors);

        Assert.DoesNotContain(Rendered(record), text => text?.Contains(Token, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void The_fallback_erases_a_class_that_has_no_policy()
    {
        FakeLogRecord record = LogOnce(SdkRedactors);

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
        FakeLogRecord record = LogFromWire(SdkRedactors);

        Assert.DoesNotContain(Rendered(record), text => text?.Contains("hunter2", StringComparison.Ordinal) == true);
        Assert.Contains(Rendered(record), text => text?.Contains("web.page.get[***]", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void A_wire_only_application_sets_the_same_redactors_on_its_own_redaction_builder()
    {
        FakeLogRecord record = LogFromWire(logging =>
        {
            logging.EnableRedaction(options => options.ApplyDiscriminator = false);
            logging.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());
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
    public void A_pass_through_set_after_the_sdk_s_redactors_shows_the_whole_key()
    {
        FakeLogRecord record = LogFromWire(logging =>
        {
            logging.EnableRedaction(options => options.ApplyDiscriminator = false);
            logging.Services.AddRedaction(redaction => redaction.SetZabbixRedactors().SetRedactor<NullRedactor>(ZabbixDataClassifications.ItemKey));
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
        FakeLogRecord record = LogPeerText(SdkRedactors);

        Assert.DoesNotContain(Rendered(record), text => text?.Contains("hunter2", StringComparison.Ordinal) == true);
        Assert.EndsWith(": [redacted]", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pass_through_set_after_the_sdk_s_redactors_shows_the_peer_s_text()
    {
        FakeLogRecord record = LogPeerText(logging =>
        {
            logging.EnableRedaction(options => options.ApplyDiscriminator = false);
            logging.Services.AddRedaction(redaction => redaction.SetZabbixRedactors().SetRedactor<NullRedactor>(ZabbixDataClassifications.PeerText));
        });

        Assert.EndsWith(": " + PeerInfo, record.Message, StringComparison.Ordinal);
    }

    // The logger's discriminator (on by default) hands a redactor "value:tag". The item-key redactor reads a name followed by
    // the discriminator as the bare key, so the application keeps the logger's default and a bare key keeps its name.
    [Fact]
    public void A_bare_key_keeps_its_name_with_the_logger_s_discriminator_on()
    {
        FakeLogRecord record = LogPeerText(SdkRedactors);

        Assert.StartsWith("Agent agent:10050 refused the request for item key \"system.uptime\": [redacted]", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_key_keeps_its_name_with_the_logger_s_discriminator_off()
    {
        FakeLogRecord record = LogPeerText(logging =>
        {
            logging.EnableRedaction(options => options.ApplyDiscriminator = false);
            logging.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());
        });

        Assert.StartsWith("Agent agent:10050 refused the request for item key \"system.uptime\": [redacted]", record.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("system.uptime:key", "system.uptime")]
    [InlineData("web.page.get[intranet,/admin,user:hunter2]:key", "web.page.get[***]")]
    [InlineData("system.uptime:", "")]
    [InlineData("system.uptime:a b", "")]
    [InlineData("user:hunter2 x", "")]
    public void The_item_key_redactor_reads_a_value_with_the_logger_s_discriminator(string discriminated, string redacted)
        => Assert.Equal(redacted, new ZabbixItemKeyRedactor().Redact(discriminated));

    [Theory]
    [InlineData(":info", "")]
    [InlineData("refused:info", "[redacted]")]
    [InlineData(":", "[redacted]")]
    public void The_peer_text_redactor_keeps_an_empty_text_empty_with_the_logger_s_discriminator(string discriminated, string redacted)
        => Assert.Equal(redacted, new ZabbixPeerTextRedactor().Redact(discriminated));

    // The extension sets redactors for the SDK's own classes and nothing else: the logger's options and the fallback are the
    // application's, so another library's data is redacted as the application decided, not as the SDK would.
    [Fact]
    public void The_sdk_s_redactors_leave_the_logger_s_options_and_the_fallback_to_the_application()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.EnableRedaction());
        services.AddRedaction(redaction => redaction.SetZabbixRedactors().SetFallbackRedactor<NullRedactor>());
        using ServiceProvider provider = services.BuildValidated();
        IRedactorProvider redactors = provider.GetRequiredService<IRedactorProvider>();

        Assert.True(provider.GetRequiredService<IOptions<LoggerRedactionOptions>>().Value.ApplyDiscriminator);
        Assert.IsType<NullRedactor>(redactors.GetRedactor(new DataClassification("Other", "Private")));
        Assert.IsType<NullRedactor>(redactors.GetRedactor(ZabbixDataClassifications.Personal));
        Assert.IsType<ZabbixItemKeyRedactor>(redactors.GetRedactor(ZabbixDataClassifications.ItemKey));
        Assert.IsType<ZabbixPeerTextRedactor>(redactors.GetRedactor(ZabbixDataClassifications.PeerText));
        Assert.Equal(string.Empty, redactors.GetRedactor(ZabbixDataClassifications.Secret).Redact(Token));
    }

    // A pass-through fallback, a development setting, still never renders a secret: the SDK's own redactor erases it.
    [Fact]
    public void A_secret_stays_erased_under_a_pass_through_fallback_while_the_application_s_classes_follow_it()
    {
        FakeLogRecord record = LogOnce(logging =>
        {
            logging.EnableRedaction();
            logging.Services.AddRedaction(redaction => redaction.SetZabbixRedactors().SetFallbackRedactor<NullRedactor>());
        });

        Assert.DoesNotContain(Rendered(record), text => text?.Contains(Token, StringComparison.Ordinal) == true);
        Assert.Contains(Rendered(record), text => text?.Contains("alice", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Setting_the_sdk_s_redactors_on_no_builder_is_refused()
        => Assert.Throws<ArgumentNullException>("builder", () => ZabbixRedactionBuilderExtensions.SetZabbixRedactors(null!));

    private static FakeLogRecord LogPeerText(Action<ILoggingBuilder> logging)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddFakeLogging();
            logging(builder);
        });
        using ServiceProvider provider = services.BuildValidated();
        WireLog.GetterRefused(provider.GetRequiredService<ILoggerFactory>().CreateLogger("test"), "agent:10050", ZabbixItemKey.Parse("system.uptime"), PeerInfo);
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
        WireLog.GetterAnswered(provider.GetRequiredService<ILoggerFactory>().CreateLogger("test"), "agent:10050", ZabbixItemKey.Parse(SecretKey), supported: true);
        return provider.GetFakeLogCollector().LatestRecord;
    }
}
