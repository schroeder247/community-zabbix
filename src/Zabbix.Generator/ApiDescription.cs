// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Generator;

/// <summary>
/// The API description (<c>schema/zabbix-api.json</c>) as the generator reads it: every version of the Zabbix API
/// merged by <c>tools/ZabbixApiExtract</c> from the Zabbix source, with the version each object, method, field and enum
/// member arrived in or left.
/// </summary>
/// <param name="Versions">The Zabbix versions the description covers, oldest first.</param>
/// <param name="Enums">The enums (define families).</param>
/// <param name="Types">The records.</param>
/// <param name="Services">One per API object.</param>
/// <param name="References">The object each id field refers to (<see cref="IdReference"/>).</param>
/// <param name="Cmdlets">The PowerShell module's reviewed shape (<see cref="CmdletDescription"/>); <see langword="null"/> reads as <see cref="CmdletDescription.Empty"/>.</param>
internal sealed record ApiDescription(
    EquatableArray<string> Versions,
    EquatableArray<EnumDescription> Enums,
    EquatableArray<TypeDescription> Types,
    EquatableArray<ServiceDescription> Services,
    EquatableArray<IdReference> References = default,
    CmdletDescription? Cmdlets = null)
{
    /// <summary>The PowerShell module's reviewed shape; empty when the description has none.</summary>
    public CmdletDescription CmdletShape => Cmdlets ?? CmdletDescription.Empty;

    /// <summary>The oldest version the description covers: nothing is gated below it.</summary>
    public string Oldest => Versions.IsEmpty ? "0.0" : Versions[0];

    /// <summary>The newest version the description covers.</summary>
    public string Newest => Versions.IsEmpty ? "0.0" : Versions[^1];

    /// <summary>A range for display: <c>7.0–8.0</c>.</summary>
    public string Range => Oldest + "–" + Newest;

    /// <summary>
    /// Reads the description, or throws <see cref="InvalidOperationException"/> naming every problem in it: for the tools
    /// and tests that read the checked-in description, which has none.
    /// </summary>
    /// <param name="json">The description's text.</param>
    public static ApiDescription Read(string json)
    {
        var problems = new List<EmitProblem>();
        ApiDescription? api = Read(json, problems);
        return api is not null && problems.Count == 0
            ? api
            : throw new InvalidOperationException("not a valid API description: " + string.Join("; ", problems.Select(p => p.Subject + ": " + p.Message)));
    }

    /// <summary>
    /// Reads the description without throwing: <see langword="null"/> and a <see cref="ZabbixDiagnostics.Unreadable"/>
    /// problem when the text is not JSON or lacks a section; otherwise the description with every malformed or repeated
    /// entry left out and a <see cref="ZabbixDiagnostics.Malformed"/> problem for each (<see cref="DescriptionValidator"/>).
    /// </summary>
    /// <param name="json">The description's text.</param>
    /// <param name="problems">Receives what was left out.</param>
    public static ApiDescription? Read(string json, List<EmitProblem> problems)
    {
        if (problems is null) throw new ArgumentNullException(nameof(problems));
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("the root is not an object");
            ApiDescription read = new(
                Section(root, "versions", "versions", static v => v.GetString() ?? throw new InvalidOperationException("a version is not a string"), problems),
                Section(root, "enums", "enum", EnumDescription.Read, problems),
                Section(root, "types", "type", TypeDescription.Read, problems),
                Section(root, "services", "service", ServiceDescription.Read, problems),
                IdReference.ReadAll(root, problems),
                CmdletDescription.Read(root, problems));
            // Named by their words once, here, for every emitter (ZabbixNames, schema/words.json): the fields, then the
            // records and enums; after validation, so a repeated name is reported, not renamed around. An update's id is
            // required before then, by the names the description gives its methods' records (ZabbixUpdateIds).
            ApiDescription valid = ZabbixUpdateIds.Require(DescriptionValidator.Validate(read, problems));
            return ZabbixNames.Rename(valid with { Types = [.. valid.Types.Select(ZabbixNames.Rename).Select(ZabbixClockPairs.Fold)] });
        }
        catch (JsonException exception)
        {
            problems.Add(new(ZabbixDiagnostics.Unreadable, exception.Message, "zabbix-api.json", null, exception.LineNumber));
            return null;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            problems.Add(new(ZabbixDiagnostics.Unreadable, exception.Message, "zabbix-api.json", null));
            return null;
        }
    }

    // One section's entries: an entry that cannot be read is a problem at its name, and left out.
    private static EquatableArray<T> Section<T>(JsonElement root, string section, string what, Func<JsonElement, T> read, List<EmitProblem> problems)
        where T : IEquatable<T>
    {
        if (!root.TryGetProperty(section, out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            throw new KeyNotFoundException("the description has no '" + section + "' array");
        var list = new List<T>();
        int index = 0;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            try
            {
                list.Add(read(entry));
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
            {
                string name = entry.ValueKind == JsonValueKind.Object ? Text(entry, "name") ?? Text(entry, "object") ?? section + "[" + index + "]" : section + "[" + index + "]";
                problems.Add(new(ZabbixDiagnostics.Malformed, "the " + what + " cannot be read and is left out: " + exception.Message, name, null));
            }
            index++;
        }
        return new([.. list]);
    }

    internal static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static bool Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    internal static EquatableArray<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(v => v.GetString() ?? string.Empty)]
            : [];
}
