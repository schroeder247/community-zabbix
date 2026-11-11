// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Security.Authentication;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// The client half against a real server, per version, in the states a long-running caller meets: its API token revoked
/// while it runs, a user that must complete multi-factor authentication, a login session ended by the caller's disposal,
/// and a history of ten thousand values (six
/// thousand in one second) read back through the tree with their exact nanoseconds.
/// </summary>
public sealed class LiveApiAdversarialTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_token_revoked_while_a_caller_uses_it_is_refused_as_not_authorized_and_not_renewed(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "adv-token");
        string tokenId = (await scope.Server.RpcAsync("token.create", new { name = scope.Name("token"), userid = "1" })).Id("tokenids");
        scope.Defer(async () =>
        {
            JsonElement left = await scope.Server.RpcAsync("token.get", new { tokenids = new[] { tokenId }, output = new[] { "tokenid" } });
            if (left.GetArrayLength() > 0) await scope.Server.RpcAsync("token.delete", new[] { tokenId });
        });
        string token = (await scope.Server.RpcAsync("token.generate", new[] { tokenId }))[0].GetProperty("token").GetString()!;
        using var caller = new ZabbixClient(scope.Target.Endpoint, new ZabbixApiTokenCredential(token));
        await caller.HostGroups.GetAsync(new ZabbixHostGroupGetParameters { Limit = 1 }, Cancel);

        await scope.Server.RpcAsync("token.delete", new[] { tokenId });
        ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => caller.HostGroups.GetAsync(new ZabbixHostGroupGetParameters { Limit = 1 }, Cancel));

        Assert.Equal(ZabbixApiErrorKind.NotAuthorized, refused.Kind);
        Assert.IsAssignableFrom<ZabbixRefusedException>(refused);
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_user_whose_group_requires_multi_factor_authentication_is_refused_once_with_the_reason(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "adv-mfa");
        string mfa = (await scope.Server.RpcAsync("mfa.create", new { type = 1, name = scope.Name("totp"), hash_function = 1, code_length = 6 })).Id("mfaids");
        scope.Delete("mfa", mfa);
        JsonElement authentication = await scope.Server.RpcAsync("authentication.get", new { output = new[] { "mfa_status", "mfaid" } });
        string status = authentication.GetProperty("mfa_status").GetString()!, previous = authentication.GetProperty("mfaid").GetString()!;
        await scope.Server.RpcAsync("authentication.update", new { mfa_status = 1, mfaid = mfa });
        scope.Defer(async () => await scope.Server.RpcAsync("authentication.update", new { mfa_status = int.Parse(status, CultureInfo.InvariantCulture), mfaid = previous == "0" ? mfa : previous }));
        string group = (await scope.Server.RpcAsync("usergroup.create", new { name = scope.Name("group"), mfa_status = 1, mfaid = mfa })).Id("usrgrpids");
        scope.Delete("usergroup", group);
        JsonElement role = await scope.Server.RpcAsync("role.get", new { output = new[] { "roleid" }, filter = new { name = new[] { "User role" } } });
        string password = "Zbx-" + Guid.NewGuid().ToString("N");
        string user = (await scope.Server.RpcAsync("user.create", new { username = scope.Name("user"), passwd = password, roleid = role[0].GetProperty("roleid").GetString(), usrgrps = new[] { new { usrgrpid = group } } })).Id("userids");
        scope.Delete("user", user);
        using var caller = new ZabbixClient(scope.Target.Endpoint, new ZabbixSessionCredential(scope.Name("user"), password));

        AuthenticationException refused = await Assert.ThrowsAsync<AuthenticationException>(() => caller.HostGroups.GetAsync(new ZabbixHostGroupGetParameters { Limit = 1 }, Cancel));
        await Assert.ThrowsAsync<AuthenticationException>(() => caller.HostGroups.GetAsync(new ZabbixHostGroupGetParameters { Limit = 1 }, Cancel));

        Assert.Contains("multi-factor", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(scope.Name("user"), refused.Message, StringComparison.Ordinal);    // a user name is never in a message
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_session_ends_on_the_server_when_its_server_is_disposed_asynchronously(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "adv-logout");
        string group = (await scope.Server.RpcAsync("usergroup.create", new { name = scope.Name("group") })).Id("usrgrpids");
        scope.Delete("usergroup", group);
        // 8.0's built-in roles below super admin have no API access by default, so the test's user gets a role that has.
        string role = (await scope.Server.RpcAsync("role.create", new Dictionary<string, object> { ["name"] = scope.Name("role"), ["type"] = 1, ["rules"] = new Dictionary<string, object> { ["api.access"] = 1 } })).Id("roleids");
        scope.Delete("role", role);
        string password = "Zbx-" + Guid.NewGuid().ToString("N");
        string user = (await scope.Server.RpcAsync("user.create", new { username = scope.Name("user"), passwd = password, roleid = role, usrgrps = new[] { new { usrgrpid = group } } })).Id("userids");
        scope.Delete("user", user);
        var credential = new ZabbixSessionCredential(scope.Name("user"), password);
        var caller = new ZabbixClient(scope.Target.Endpoint, credential);
        await caller.Users.GetAsync(new ZabbixUserGetParameters { Limit = 1 }, Cancel);
        string session = credential.Session!;
        await scope.Server.RpcAsync("user.checkAuthentication", new { sessionid = session });             // live before

        await caller.DisposeAsync();

        await Assert.ThrowsAsync<ZabbixApiException>(() => scope.Server.RpcAsync("user.checkAuthentication", new { sessionid = session }));
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Ten_thousand_values_six_thousand_in_one_second_come_back_through_the_tree_to_the_nanosecond(string version)
    {
        (LiveTarget _, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "adv-history");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "history", [("adv.history", ZabbixItemValueType.Unsigned)]);
        long second = TimeProvider.System.GetUtcNow().AddMinutes(-30).ToUnixTimeSeconds();          // data, not a measurement
        ZabbixValue[] sent = [.. Enumerable.Range(0, 10_000).Select(n => new ZabbixValue(host.Host, "adv.history", n.ToString(CultureInfo.InvariantCulture),
            n < 6_000 ? new ZabbixClock(second, n * 1_000 + 7) : new ZabbixClock(second + 1 + (n - 6_000) / 100, (n % 100) * 10_000_000 + 3)))];
        using var sender = new ZabbixSender([trapper]);

        ZabbixSenderResponse response = await LiveFixtures.SendWhenKnownAsync(sender, sent);
        Assert.Equal(10_000, response.Processed);
        await LiveFixtures.HistoryAsync(scope.Server, host.Items[0], 10_000, TimeSpan.FromSeconds(90));
        List<ZabbixValue> read = await scope.Server.Hosts[host.Host].Item("adv.history")
            .GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(second), DateTimeOffset.FromUnixTimeSeconds(second + 100), Cancel).ToListAsync(Cancel);

        Assert.Equal(sent.Select(value => (value.Clock, value.Value)), read.Select(value => (value.Clock, value.Value)));
    }
}
