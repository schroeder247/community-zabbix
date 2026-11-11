// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;

// CA1819 (properties should not return arrays), on each MSBuild task parameter that takes an item list: the engine binds
// an item list to a task property by reflection, and only to ITaskItem[] (or string[]) — a read-only collection type
// would not bind. Microsoft.Build.Utilities.Task's own parameters are arrays throughout the SDK.
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "MSBuild binds an item list only to an ITaskItem[] task parameter.", Scope = "member", Target = "~P:Community.Zabbix.PowerShell.HelpGen.ZabbixBuildHelpTask.Sources")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "MSBuild binds an item list only to an ITaskItem[] task parameter.", Scope = "member", Target = "~P:Community.Zabbix.PowerShell.HelpGen.ZabbixBuildHelpTask.BaseClassSources")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "MSBuild binds an item list only to an ITaskItem[] task parameter.", Scope = "member", Target = "~P:Community.Zabbix.PowerShell.HelpGen.ZabbixBuildHelpTask.ExampleSources")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "MSBuild binds an item list only to an ITaskItem[] task parameter.", Scope = "member", Target = "~P:Community.Zabbix.PowerShell.HelpGen.ZabbixBuildHelpTask.OutputPaths")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "MSBuild binds an item list only to an ITaskItem[] task parameter.", Scope = "member", Target = "~P:Community.Zabbix.PowerShell.HelpGen.ZabbixBuildHelpTask.DocumentationFiles")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "MSBuild binds an item list only to an ITaskItem[] task parameter.", Scope = "member", Target = "~P:Community.Zabbix.PowerShell.HelpGen.ZabbixWriteManifestExportsTask.Sources")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "MSBuild binds an item list only to an ITaskItem[] task parameter.", Scope = "member", Target = "~P:Community.Zabbix.PowerShell.HelpGen.ZabbixWriteManifestExportsTask.OutputPaths")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "MSBuild binds an item list only to an ITaskItem[] task parameter.", Scope = "member", Target = "~P:Community.Zabbix.PowerShell.HelpGen.ZabbixWriteTypesPs1XmlTask.OutputPaths")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "MSBuild binds an item list only to an ITaskItem[] task parameter.", Scope = "member", Target = "~P:Community.Zabbix.PowerShell.HelpGen.ZabbixWriteTypesPs1XmlTask.FormatOutputPaths")]
