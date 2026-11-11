// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// The API's replies that the frontend builds by hand read into their records through the generated clients, as the
/// pinned 7.0, 7.4 and 8.0 sources write them (tools/zabbix-sources.lock): <c>CScript::execute</c>, <c>CSla::getSli</c>,
/// <c>CToken::generate</c>, <c>CUser::checkAuthentication</c> and <c>login</c>, <c>CUserDirectory::test</c>,
/// <c>CConfiguration::import</c> and <c>importcompare</c>, the singletons' updates, and 8.0's <c>CCepRule</c> and
/// <c>CDevice</c>.
/// </summary>
public sealed class ApiReplyModelTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static (Harness Harness, ZabbixClient Client) Client(string version, ScriptedApi api)
    {
        Harness harness = TransportHarness.Build(api.On("apiinfo.version", $"\"{version}\""));
        return (harness, harness.Provider.GetRequiredService<ZabbixClient>());
    }

    // apiinfo.version answers ZABBIX_API_VERSION: 8.0.0 on the 8.0 release candidates (defines.inc.php 8.0:17).
    public static TheoryData<string> Versions => ["7.0.31", "7.4.15", "8.0.0"];

    // ----- script.execute ------------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Versions))]
    public async Task A_script_s_result_reads_its_output_and_an_empty_debug_as_none(string version)
    {
        // CScript::execute: response, the command's output, and CZabbixServer's debug, an empty PHP array for a script that
        // isn't a webhook (CZabbixServer.php: protected $debug = []).
        using var api = new ScriptedApi().On("script.execute", """{"response":"success","value":"PING 10.0.0.2: 3 packets","debug":[]}""");
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        ZabbixScriptExecuteResult result = await client.Scripts.ExecuteAsync(new ZabbixScriptExecuteRequest { ScriptId = 1, HostId = 10084 }, Cancel);

        Assert.Equal(("success", "PING 10.0.0.2: 3 packets"), (result.Response, result.Value));
        Assert.Null(result.Debug);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task A_webhook_script_s_result_reads_its_log(string version)
    {
        using var api = new ScriptedApi().On("script.execute", """
            {"response":"success","value":"{\"ok\":true}","debug":{"logs":[{"level":4,"ms":1,"message":"start"},{"level":3,"ms":250,"message":"slow reply"}],"ms":251}}
            """);
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        ZabbixScriptExecuteResult result = await client.Scripts.ExecuteAsync(new ZabbixScriptExecuteRequest { ScriptId = 4, EventId = 77 }, Cancel);

        Assert.Equal(TimeSpan.FromMilliseconds(251), result.Debug!.Elapsed);
        Assert.Equal([(4, TimeSpan.FromMilliseconds(1), "start"), (3, TimeSpan.FromMilliseconds(250), "slow reply")],
            result.Debug.Entries!.Select(entry => (entry.Level!.Value, entry.Elapsed!.Value, entry.Message)));
    }

    // ----- sla.getsli ----------------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Versions))]
    public async Task An_sla_report_reads_its_periods_services_and_each_service_s_sli_per_period(string version)
    {
        // CSla::getSli: serviceids are the keys of a preservekeys read (array_keys), so PHP writes them as numbers;
        // calculateSli's cells hold seconds, the SLI (-1 without data) and the error budget, negative once spent.
        using var api = new ScriptedApi().On("sla.getsli", """
            {"periods":[{"period_from":1730419200,"period_to":1731024000},{"period_from":1731024000,"period_to":1731628800}],
            "serviceids":[4,7],
            "sli":[[{"uptime":604800,"downtime":0,"sli":100,"error_budget":3628,"excluded_downtimes":[]},
                    {"uptime":0,"downtime":0,"sli":-1,"error_budget":0,"excluded_downtimes":[]}],
                   [{"uptime":598800,"downtime":6000,"sli":99.00793650793651,"error_budget":-2372,
                     "excluded_downtimes":[{"name":"Patch night","period_from":1731200000,"period_to":1731203600}]},
                    {"uptime":604800,"downtime":0,"sli":100,"error_budget":3628,"excluded_downtimes":[]}]]}
            """);
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        ZabbixSliReport report = await client.Slas.GetSliAsync(new ZabbixSlaGetSliRequest(2), Cancel);

        Assert.Equal([4UL, 7UL], report.ServiceIds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1731024000), report.Periods![1].PeriodFrom);
        ZabbixServiceSli spent = report.Sli![1][0];
        Assert.Equal((TimeSpan.FromSeconds(6000), TimeSpan.FromSeconds(-2372)), (spent.Downtime!.Value, spent.ErrorBudget!.Value));
        Assert.Equal("Patch night", Assert.Single(spent.ExcludedDowntimes!).Name);
        Assert.Equal(-1, report.Sli[0][1].Sli);
        var contract = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<ZabbixSliReport>)ZabbixJson.Options.GetTypeInfo(typeof(ZabbixSliReport));
        Assert.Equal(report, JsonSerializer.Deserialize(JsonSerializer.Serialize(report, contract), contract));
    }

    // ----- token.generate, user.login, user.checkAuthentication, user.logout -----------------------------------------

    [Theory, MemberData(nameof(Versions))]
    public async Task Generated_tokens_read_their_ids_and_secrets_which_never_print(string version)
    {
        using var api = new ScriptedApi().On("token.generate", """[{"tokenid":"3","token":"5e6f2b0c2d0f3b4d7a8c9e1f0a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d"},{"tokenid":"4","token":"00ff"}]""");
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        IReadOnlyList<ZabbixGeneratedToken> tokens = await client.Tokens.GenerateAsync([3UL, 4UL], Cancel);

        Assert.Equal([3UL, 4UL], tokens.Select(token => token.TokenId!.Value));
        Assert.Equal("00ff", tokens[1].Token);
        Assert.DoesNotContain("5e6f2b0c", tokens[0].ToString(), StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task A_session_s_user_reads_the_user_its_groups_give_and_its_session_which_never_prints(string version)
    {
        // CUser::checkAuthentication: the users row, the group fields, the authentication and user types, the address,
        // and the session's id and secret; 8.0 adds the user's default maintenance period.
        string maintenance = version.StartsWith('8') ? ",\"default_maintenance_period\":\"1h\"" : string.Empty;
        using var api = new ScriptedApi().On("user.checkauthentication", $$"""
            {"userid":"1","username":"Admin","name":"Zabbix","surname":"Administrator","url":"","autologin":"1","autologout":"0",
            "lang":"en_US","refresh":"30s","theme":"default","attempt_failed":"0","attempt_ip":"","attempt_clock":"0","rows_per_page":"50",
            "timezone":"UTC","roleid":"3","userdirectoryid":"0","ts_provisioned":"0"{{maintenance}},"debug_mode":0,"deprovisioned":false,
            "gui_access":"0","mfaid":0,"auth_type":0,"type":3,"userip":"10.0.0.9","sessionid":"0424bd59b807674191e7d77572075f33","secret":"5d1e6c36f2b1a8"}
            """);
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        ZabbixAuthenticatedUser user = await client.Users.CheckAuthenticationAsync(new ZabbixUserCheckAuthenticationRequest { SessionId = "0424bd59b807674191e7d77572075f33" }, Cancel);

        Assert.Equal((1UL, "Admin", ZabbixRoleType.SuperAdmin, ZabbixAuthenticationType.Internal), (user.UserId!.Value, user.UserName, user.Type!.Value, user.AuthenticationType!.Value));
        Assert.Equal((ZabbixDuration.Parse("30s"), false, ZabbixGuiAccess.System), (user.Refresh!.Value, user.Deprovisioned!.Value, user.GuiAccess!.Value));
        Assert.Equal("0424bd59b807674191e7d77572075f33", user.SessionId);
        Assert.Equal(version.StartsWith('8') ? ZabbixDuration.Parse("1h") : null, user.DefaultMaintenancePeriod);
        Assert.DoesNotContain("0424bd59", user.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("5d1e6c36", user.ToString(), StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task A_login_reads_the_session_id_it_answers_and_the_user_it_answers_with_user_data(string version)
    {
        // CUser::login answers the session id, or the user with userData (CUser.php 8.0:2458).
        using var api = new ScriptedApi().On("user.login", parameters => parameters.TryGetProperty("userData", out JsonElement asked) && asked.GetBoolean()
            ? """{"userid":"1","username":"Admin","mfaid":0,"sessionid":"6c1f0a"}"""
            : "\"0424bd59b807674191e7d77572075f33\"");
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        ZabbixAuthenticatedUser session = await client.Users.LogOnAsync(new ZabbixUserLoginRequest("Admin", "zabbix"), Cancel);
        ZabbixAuthenticatedUser user = await client.Users.LogOnAsync(new ZabbixUserLoginRequest("Admin", "zabbix") { UserData = true }, Cancel);

        Assert.Equal(new ZabbixAuthenticatedUser { SessionId = "0424bd59b807674191e7d77572075f33" }, session);
        Assert.Equal(("Admin", "6c1f0a"), (user.UserName, user.SessionId));
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task A_logout_and_an_update_answered_with_true_complete_and_anything_else_is_invalid_data(string version)
    {
        using var api = new ScriptedApi().On("user.logout", "true").On("autoregistration.update", "{}");
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        await client.Users.LogOffAsync(Cancel);
        InvalidDataException wrong = await Assert.ThrowsAsync<InvalidDataException>(() => client.Autoregistration.UpdateAsync(new ZabbixAutoregistrationUpdateRequest(), Cancel));

        Assert.Contains("autoregistration.update", wrong.Message, StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task A_singleton_s_update_reads_the_names_of_the_parameters_it_changed(string version)
    {
        // CAuthentication::update, CHousekeeping::update and CSettings::update answer array_keys of what they wrote.
        using var api = new ScriptedApi()
            .On("authentication.update", """["authentication_type","passwd_min_length"]""")
            .On("housekeeping.update", """["hk_events_mode"]""")
            .On("settings.update", """["default_theme","work_period"]""");
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        Assert.Equal(["authentication_type", "passwd_min_length"], await client.Authentication.UpdateAsync(new ZabbixAuthenticationUpdateRequest(), Cancel));
        Assert.Equal(["hk_events_mode"], await client.Housekeeping.UpdateAsync(new ZabbixHousekeepingUpdateRequest(), Cancel));
        Assert.Equal(["default_theme", "work_period"], await client.Settings.UpdateAsync(new ZabbixSettingsUpdateRequest(), Cancel));
    }

    // ----- userdirectory.test --------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Versions))]
    public async Task A_directory_test_reads_the_test_user_and_what_provisioning_gives_them(string version)
    {
        // CUserDirectory::test: the user name, and with provisioning on what CLdap::getProvisionedData maps.
        using var api = new ScriptedApi().On("userdirectory.test", """
            {"username":"jdoe","name":"John","surname":"Doe","medias":[{"name":"Work e-mail","mediatypeid":"1","sendto":["jdoe@example.com"]}],
            "usrgrps":[{"usrgrpid":"7"},{"usrgrpid":"8"}],"roleid":"1","userdirectoryid":"2"}
            """);
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        ZabbixUserDirectoryTestResult result = await client.UserDirectories.TestAsync(new ZabbixUserDirectoryTestRequest("ldap.example.com", 389, "dc=example,dc=com", "uid", "jdoe", "secret") { UserDirectoryId = 2 }, Cancel);

        Assert.Equal(("jdoe", "John", "Doe", 1UL, 2UL), (result.UserName, result.FirstName, result.LastName, result.RoleId!.Value, result.UserDirectoryId!.Value));
        Assert.Equal(["jdoe@example.com"], Assert.Single(result.Medias!).SendTo);
        Assert.Equal([7UL, 8UL], result.UserGroups!.Select(group => group.UserGroupId!.Value));
    }

    // ----- configuration.import and importcompare ------------------------------------------------------------------

    [Theory, MemberData(nameof(Versions))]
    public async Task An_import_answered_with_true_reads_as_no_missing_objects(string version)
    {
        using var api = new ScriptedApi().On("configuration.import", "true");
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        ZabbixConfigurationImportResult result = await client.Configuration.ImportAsync(new ZabbixConfigurationImportRequest { Format = "json", Source = "{}", Rules = new() }, Cancel);

        Assert.Equal(new ZabbixConfigurationImportResult(), result);
    }

    [Fact]
    public async Task An_8_0_import_asked_for_its_missing_objects_reads_them_by_type()
    {
        // CConfiguration::import with returnMissingObjects (8.0): CMissingObjectCollector names each object by its fields.
        using var api = new ScriptedApi().On("configuration.import", """
            {"missingObjects":{"hosts":[{"name":"web-02"}],"items":[{"host":"web-02","key":"agent.ping"}],"users":[{"username":"jdoe"}]}}
            """);
        (Harness harness, ZabbixClient client) = Client("8.0.0", api);
        using Harness _ = harness;

        ZabbixConfigurationImportResult result = await client.Configuration.ImportAsync(new ZabbixConfigurationImportRequest { Format = "json", Source = "{}", Rules = new(), ReturnMissingObjects = true }, Cancel);

        Assert.Equal("web-02", Assert.Single(result.MissingObjects!.Hosts!).Name);
        Assert.Equal(("web-02", "agent.ping"), (result.MissingObjects.Items![0].HostName, result.MissingObjects.Items[0].Key));
        Assert.Equal("jdoe", result.MissingObjects.Users![0].UserName);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task A_comparison_reads_each_type_s_changes_with_both_sides_and_the_changes_to_what_an_entity_holds(string version)
    {
        // CConfigurationImportcompare::compareByStructure: an entity is before and after, and the changes to what it holds,
        // by type; the entities are the import format's, untyped.
        using var api = new ScriptedApi().On("configuration.importcompare", """
            {"templates":{"updated":[{"before":{"uuid":"a","template":"Web App","description":"Shop"},"after":{"uuid":"a","template":"Web App","description":"Shop, v2"},
            "items":{"added":[{"after":{"uuid":"b","name":"Orders","key":"orders.count"},"triggers":{"added":[{"after":{"uuid":"c","name":"No orders"}}]}}],
            "removed":[{"before":{"uuid":"d","name":"Old","key":"old.key"}}]}}]},
            "template_groups":{"added":[{"after":{"uuid":"e","name":"Templates/Shop"}}]}}
            """);
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        ZabbixConfigurationComparison diff = await client.Configuration.ImportCompareAsync(new ZabbixConfigurationImportRequest { Format = "json", Source = "{}", Rules = new() }, Cancel);

        ZabbixConfigurationChange template = Assert.Single(diff.Templates!.Updated!);
        Assert.Equal(("Shop", "Shop, v2"), (template.Before!.Value.GetProperty("description").GetString(), template.After!.Value.GetProperty("description").GetString()));
        Assert.Equal("orders.count", template.Items!.Added![0].After!.Value.GetProperty("key").GetString());
        Assert.Equal("No orders", template.Items.Added[0].Triggers!.Added![0].After!.Value.GetProperty("name").GetString());
        Assert.Null(template.Items.Removed![0].After);
        Assert.Equal("Templates/Shop", diff.TemplateGroups!.Added![0].After!.Value.GetProperty("name").GetString());
        Assert.Null(diff.HostGroups);
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task A_comparison_with_no_differences_reads_as_an_empty_one(string version)
    {
        // An empty PHP array: nothing would change.
        using var api = new ScriptedApi().On("configuration.importcompare", "[]");
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        ZabbixConfigurationComparison diff = await client.Configuration.ImportCompareAsync(new ZabbixConfigurationImportRequest { Format = "json", Source = "{}", Rules = new() }, Cancel);

        Assert.Equal(new ZabbixConfigurationComparison(), diff);
    }

    [Fact]
    public async Task An_8_0_comparison_reads_global_dashboards_their_pages_and_widgets()
    {
        // 8.0 compares global dashboards, their pages and the pages' widgets (CConfigurationImportcompare.php 8.0:40-140).
        using var api = new ScriptedApi().On("configuration.importcompare", """
            {"dashboards":{"updated":[{"before":{"name":"Ops"},"after":{"name":"Ops"},"pages":{"updated":[{"before":{"name":"p1"},"after":{"name":"p1"},
            "widgets":{"added":[{"after":{"type":"graph"}}]}}]}}]}}
            """);
        (Harness harness, ZabbixClient client) = Client("8.0.0", api);
        using Harness _ = harness;

        ZabbixConfigurationComparison diff = await client.Configuration.ImportCompareAsync(new ZabbixConfigurationImportRequest { Format = "json", Source = "{}", Rules = new() }, Cancel);

        Assert.Equal("graph", diff.Dashboards!.Updated![0].Pages!.Updated![0].Widgets!.Added![0].After!.Value.GetProperty("type").GetString());
    }

    // ----- 8.0's correlation rules and devices ---------------------------------------------------------------------

    [Fact]
    public async Task An_8_0_rule_s_reset_and_a_device_s_enrolment_read_their_records_and_keep_the_secrets_out_of_the_text()
    {
        using var api = new ScriptedApi()
            .On("ceprule.resettimewindows", """{"cep_ruleid":"5"}""")
            .On("device.init", """{"uuid":"0b6f6b1e","enrollment_token":"et-secret","enrollment_token_expires_at":1731303600,"mobile_enrollment_token":"mt-secret","bridge_adapter_encryption_key":"key-secret","bridge_enrollment_url":"https://bridge.example/enrol"}""")
            .On("device.onboard", """{"token":"device-secret"}""")
            .On("device.offboard", """{"uuid":"0b6f6b1e"}""");
        (Harness harness, ZabbixClient client) = Client("8.0.0", api);
        using Harness _ = harness;

        ZabbixCepRuleResetTimeWindowsResult reset = await client.CepRules.ResetTimeWindowsAsync(new ZabbixCepRuleResetTimeWindowsRequest(5), Cancel);
        ZabbixDeviceInitResult init = await client.Devices.InitAsync(new ZabbixDeviceInitRequest { UserId = 1 }, Cancel);
        ZabbixDeviceOnboardResult onboard = await client.Devices.OnboardAsync(new ZabbixDeviceOnboardRequest(
            "et-secret", new("P-256", "EC", "id-1", "x1", "y1"), new("P-256", "EC", "enc-1", "x2", "y2"), "push-1", "phone"), Cancel);
        ZabbixDeviceOffboardResult offboard = await client.Devices.OffboardAsync(new ZabbixDeviceOffboardRequest("0b6f6b1e"), Cancel);

        Assert.Equal(5UL, reset.CepRuleId);
        Assert.Equal((DateTimeOffset.FromUnixTimeSeconds(1731303600), "et-secret"), (init.EnrollmentTokenExpiresAt!.Value, init.EnrollmentToken));
        Assert.Equal(("device-secret", "0b6f6b1e"), (onboard.Token, offboard.Uuid));
        Assert.DoesNotContain("secret", init.ToString() + onboard.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("7.0.31")]
    [InlineData("7.4.15")]
    public async Task A_7_x_server_has_no_rule_reset_and_the_call_is_refused_before_it_is_sent(string version)
    {
        using var api = new ScriptedApi();
        (Harness harness, ZabbixClient client) = Client(version, api);
        using Harness _ = harness;

        await Assert.ThrowsAsync<ZabbixVersionException>(() => client.CepRules.ResetTimeWindowsAsync(new ZabbixCepRuleResetTimeWindowsRequest(5), Cancel));

        Assert.Empty(api.ParamsOf("ceprule.resettimewindows"));
    }
}
