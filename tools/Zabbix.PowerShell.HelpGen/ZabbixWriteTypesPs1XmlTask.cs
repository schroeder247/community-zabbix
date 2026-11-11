// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using Microsoft.Build.Framework;
using ZabbixGenerator::Community.Zabbix.Generator;
// Not `using Microsoft.Build.Utilities;`: ImplicitUsings brings in System.Threading.Tasks, whose Task type would
// then collide with Microsoft.Build.Utilities.Task (CS0104) on the base-class reference below.
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Community.Zabbix.PowerShell.HelpGen;

/// <summary>
/// The MSBuild task Zabbix.PowerShell.csproj's <c>ZabbixGenerateTypesPs1Xml</c> target runs after
/// <c>ZabbixGenerateManifest</c>: reads the API description at <see cref="SchemaPath"/> (the same
/// <c>zabbix-api.cmdlets.json</c> copy <c>ZabbixCmdletDescriptionCopy</c> already makes for the cmdlet generator),
/// turns it into <c>Community.Zabbix.types.ps1xml</c>'s and <c>Community.Zabbix.format.ps1xml</c>'s text through <see cref="ZabbixTypesEmitter.Generate"/> (a pure
/// function over <see cref="ApiDescription"/> — no file I/O of its own, so it is exercised directly by
/// <c>Zabbix.Tests</c> without this task), and writes it to every path in <see cref="OutputPaths"/> whose content it
/// changes (<see cref="GeneratedFile"/>, as the manifest and the help are written).
/// </summary>
public sealed class ZabbixWriteTypesPs1XmlTask : MSBuildTask
{
    /// <summary>The API description (<c>zabbix-api.cmdlets.json</c>) to read.</summary>
    [Required]
    public string SchemaPath { get; set; } = string.Empty;

    /// <summary>Every path the generated <c>Community.Zabbix.types.ps1xml</c> text is written to.</summary>
    [Required]
    public ITaskItem[] OutputPaths { get; set; } = [];
    /// <summary>Every path the generated <c>Community.Zabbix.format.ps1xml</c> text (<see cref="ZabbixTypesEmitter.GenerateFormat"/>) is written to.</summary>
    [Required]
    public ITaskItem[] FormatOutputPaths { get; set; } = [];

    /// <inheritdoc/>
    public override bool Execute()
    {
        ApiDescription description = ApiDescription.Read(File.ReadAllText(SchemaPath));
        string xml = ZabbixTypesEmitter.Generate(description);
        int bound = description.Types.Count(t => t.Role is "request" or "nested-request" or "get");
        int adapted = description.Types.Count(t => t.Role == "read" && t.ExtensionData);
        Log.LogMessage(MessageImportance.High, $"Community.Zabbix.types.ps1xml: {bound} TypeConverter, {adapted} TypeAdapter entries.");
        Write(OutputPaths, xml);
        string format = ZabbixTypesEmitter.GenerateFormat(description);
        Log.LogMessage(MessageImportance.High, $"Community.Zabbix.format.ps1xml: {format.Split("<View>").Length - 1} views.");
        Write(FormatOutputPaths, format);
        return true;
    }

    private static void Write(ITaskItem[] paths, string text)
    {
        foreach (ITaskItem path in paths) GeneratedFile.Write(path.ItemSpec, text);
    }
}
