# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Reads the newline-delimited JSON a Zabbix connector sends and hands each record on.
.DESCRIPTION
The Read-ZbxConnectorStream cmdlet parses the lines a Zabbix connector exports, piped in or read from -Path (a file or a
named pipe the receiver of the connector's POSTs writes to): each line is an item value (ZabbixConnectorValue) or an
event (ZabbixConnectorEvent). -ItemHandler and -EventHandler run for each record of their kind, with the record as $_;
a record no handler takes is written to the pipeline. It is not an HTTP listener and sends nothing to the server.
#>
#region Parse connector lines
# One item value and one event, as a connector exports them; each comes out as its record.
$lines = @(
    '{"host":{"host":"web-01","name":"web-01"},"groups":["Linux servers"],"itemid":44457,"name":"CPU utilization","clock":1767225600,"ns":0,"value":12.5,"type":0}'
    '{"clock":1767225660,"ns":0,"value":1,"eventid":1271,"name":"High CPU utilization on web-01","severity":4,"hosts":[{"host":"web-01","name":"web-01"}],"groups":["Linux servers"],"tags":[]}'
)
$lines | Read-ZbxConnectorStream | Select-Object Clock, Name, Value
#endregion

#region Handle values and events separately
# Each handler sees its records as $_; a record a handler took is not written to the pipeline.
'{"host":{"host":"web-01","name":"web-01"},"itemid":44457,"name":"CPU utilization","clock":1767225600,"ns":0,"value":12.5,"type":0}' |
    Read-ZbxConnectorStream -ItemHandler { '{0} = {1}' -f $_.Name, $_.Value } -EventHandler { 'event {0}: {1}' -f $_.EventId, $_.Name }
#endregion
