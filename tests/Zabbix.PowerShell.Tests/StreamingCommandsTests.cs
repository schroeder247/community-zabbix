// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Connector;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The connector stream reader: <c>Read-ZbxConnectorStream</c>.</summary>
public sealed class StreamingCommandsTests
{
    private const string ItemLine = """{"itemid":100,"name":"CPU","clock":1000,"ns":0,"value":42}""";
    private const string EventLine = """{"eventid":200,"name":"High CPU","clock":1001,"ns":0,"value":1}""";

    [Fact]
    public void Piped_lines_with_no_handlers_are_written_to_the_pipeline_as_typed_records()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"'{ItemLine}','{EventLine}' | Read-ZbxConnectorStream");

        Assert.Empty(run.Errors);
        Assert.Equal(2, run.Output.Count);
        var value = Assert.IsType<ZabbixConnectorValue>(run.Output[0].BaseObject);
        Assert.Equal(100UL, value.ItemId);
        var @event = Assert.IsType<ZabbixConnectorEvent>(run.Output[1].BaseObject);
        Assert.Equal(200UL, @event.EventId);
    }

    [Fact]
    public void An_item_value_line_reaches_ItemHandler_and_never_the_pipeline()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"'{ItemLine}' | Read-ZbxConnectorStream -ItemHandler {{ \"seen:$($_.ItemId)\" }}");

        Assert.Empty(run.Errors);
        Assert.Equal("seen:100", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void An_event_line_reaches_EventHandler_and_never_the_pipeline()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"'{EventLine}' | Read-ZbxConnectorStream -EventHandler {{ \"seen:$($_.EventId)\" }}");

        Assert.Empty(run.Errors);
        Assert.Equal("seen:200", Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public void A_malformed_line_is_one_non_terminating_error_and_does_not_stop_the_rest()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"'not json','{ItemLine}' | Read-ZbxConnectorStream");

        Assert.Single(run.Errors);
        var value = Assert.IsType<ZabbixConnectorValue>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(100UL, value.ItemId);
    }

    [Fact]
    public void Path_reads_NDJSON_lines_from_a_file_after_any_pipeline_input()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, ItemLine + "\n" + EventLine + "\n");
            using Shell shell = Shell.Open();

            Run run = shell.Invoke($"Read-ZbxConnectorStream -Path '{path}'");

            Assert.Empty(run.Errors);
            Assert.Equal(2, run.Output.Count);
            Assert.IsType<ZabbixConnectorValue>(run.Output[0].BaseObject);
            Assert.IsType<ZabbixConnectorEvent>(run.Output[1].BaseObject);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
