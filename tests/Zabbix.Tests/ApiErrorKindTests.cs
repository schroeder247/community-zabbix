// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Tests;

/// <summary>
/// The error family, classified from the code and the start of <c>data</c>. The
/// texts are the servers' own: the rows marked "measured" were answered by 7.0.31 and 8.0.0rc1 word for word.
/// </summary>
public sealed class ApiErrorKindTests
{
    [Theory]
    [InlineData(-32602, "Not authorized.", ZabbixApiErrorKind.NotAuthorized)]                                               // measured
    [InlineData(-32602, "Session terminated, re-login, please.", ZabbixApiErrorKind.SessionTerminated)]                     // measured
    [InlineData(-32500, "API token expired.", ZabbixApiErrorKind.TokenExpired)]
    [InlineData(-32500, "Incorrect user name or password or account is temporarily blocked.", ZabbixApiErrorKind.LoginFailed)] // measured
    [InlineData(-32500, "Authentication failed: supplied credentials are not unique.", ZabbixApiErrorKind.LoginFailed)]
    [InlineData(-32602, "No permissions for system access.", ZabbixApiErrorKind.LoginFailed)]                               // CUser.php 7.0:2734
    [InlineData(-32500, "No permissions to call \"hanode.get\".", ZabbixApiErrorKind.Permission)]
    [InlineData(-32500, "You do not have permission to perform this operation.", ZabbixApiErrorKind.Permission)]
    [InlineData(-32500, "No permissions to referred object or it does not exist!", ZabbixApiErrorKind.PermissionOrNotFound)] // measured
    [InlineData(-32601, "Incorrect API \"nosuch\".", ZabbixApiErrorKind.MethodNotFound)]                                    // measured
    [InlineData(-32601, "Incorrect method \"host.nosuch\".", ZabbixApiErrorKind.MethodNotFound)]
    [InlineData(-32602, "The \"apiinfo.version\" method must be called without authorization header.", ZabbixApiErrorKind.AuthorizationHeaderRefused)] // measured on 7.0
    [InlineData(-32603, "SQLSTATE[08006] could not connect", ZabbixApiErrorKind.Transient)]
    [InlineData(-32500, "Database error occurred.", ZabbixApiErrorKind.Database)]                                          // measured
    [InlineData(-32500, "System error occurred. Please contact Zabbix administrator.", ZabbixApiErrorKind.Database)]
    [InlineData(-32602, "Invalid parameter \"/1\": unexpected parameter \"bogus\".", ZabbixApiErrorKind.Validation)]
    [InlineData(-32500, "Host with the same name \"a\" already exists.", ZabbixApiErrorKind.Validation)]
    [InlineData(-32600, "Invalid parameter \"/jsonrpc\": value must be \"2.0\".", ZabbixApiErrorKind.Validation)]
    [InlineData(0, null, ZabbixApiErrorKind.Unknown)]
    public void An_error_object_is_classified_by_its_code_and_the_start_of_its_data(int code, string? data, ZabbixApiErrorKind kind)
        => Assert.Equal(kind, ZabbixApiException.Classify(code, data));

    // A validation message that merely mentions a session or a login is not an authentication failure.
    [Theory]
    [InlineData("Invalid parameter \"/1/name\": value \"Session terminated\" is not unique.")]
    [InlineData("Incorrect value for field \"login\".")]
    public void A_validation_text_that_mentions_a_session_or_a_login_stays_validation(string data)
        => Assert.Equal(ZabbixApiErrorKind.Validation, ZabbixApiException.Classify(-32602, data));

    [Fact]
    public void Only_a_transient_kind_is_transient()
    {
        Assert.True(new ZabbixApiException(-32603, "Internal error.", "db down", "host.get").IsTransient);
        Assert.False(new ZabbixApiException(-32602, "Invalid params.", "Not authorized.", "host.get").IsTransient);
        // an SQL failure inside the method (ZBX_API_ERROR_DB) fails again: bad input the validators passed (measured with correlation.get)
        Assert.False(new ZabbixApiException(-32500, "Application error.", "Database error occurred.", "correlation.get").IsTransient);
    }

    // 8.0 in debug mode appends to data, so the match is on the start.
    [Fact]
    public void Text_appended_after_the_known_start_does_not_change_the_kind()
        => Assert.Equal(ZabbixApiErrorKind.NotAuthorized, ZabbixApiException.Classify(-32602, "Not authorized. Wrong number of JWT segments."));
}
