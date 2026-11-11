// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Represents a configuration document as sections of elements, the one model the batched export merges into. Every
/// format <c>configuration.export</c> writes has the same shape — a <c>zabbix_export</c> root whose sections are a scalar (<c>version</c>) or a list
/// (<c>templates</c>, <c>hosts</c>, …), named the same in JSON, XML and YAML — so a document is its sections in order, a
/// scalar kept from the first document that has it, a list the union of its elements compared by their exact text (the
/// server writes an object the same way every time, so the groups every export batch repeats, and a trigger that spans two
/// batches, appear once). An element is carried as the server wrote it, never re-serialized.
/// </summary>
internal sealed class ZabbixConfigurationDocument
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, string> _scalars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ZabbixConfigurationElement>> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.Ordinal);
    private string? _declaration;

    private ZabbixConfigurationDocument(string format) => Format = format;

    /// <summary>The wire format: <c>json</c>, <c>xml</c> or <c>yaml</c>.</summary>
    public string Format { get; }

    /// <summary>Reads each of <paramref name="texts"/> into one document.</summary>
    public static ZabbixConfigurationDocument Merge(IEnumerable<string> texts, string format)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var document = new ZabbixConfigurationDocument(format);
        foreach (string text in texts)
        {
            switch (format)
            {
                case "xml": document.ReadXml(text); break;
                case "yaml": document.ReadYaml(text); break;
                default: document.ReadJson(text); break;
            }
        }
        return document;
    }

    /// <summary>The document in its format.</summary>
    public string ToText() => Format switch
    {
        "xml" => WriteXml(),
        "yaml" => WriteYaml(),
        _ => WriteJson(),
    };

    private void Scalar(string name, string value)
    {
        if (!_scalars.ContainsKey(name) && !_lists.ContainsKey(name)) _order.Add(name);
        _scalars.TryAdd(name, value);
    }

    private void List(string name, IEnumerable<string> elements)
    {
        if (!_lists.TryGetValue(name, out List<ZabbixConfigurationElement>? list))
        {
            if (!_scalars.ContainsKey(name)) _order.Add(name);
            _lists[name] = list = [];
            _seen[name] = new HashSet<string>(StringComparer.Ordinal);
        }
        foreach (string element in elements)
            if (_seen[name].Add(element)) list.Add(new ZabbixConfigurationElement(element));
    }

    private void ReadJson(string text)
    {
        using JsonDocument parsed = JsonDocument.Parse(text);
        foreach (JsonProperty section in parsed.RootElement.GetProperty(TestZbxConfigurationCommand.ExportRoot).EnumerateObject())
        {
            if (section.Value.ValueKind == JsonValueKind.Array) List(section.Name, section.Value.EnumerateArray().Select(element => element.GetRawText()));
            else Scalar(section.Name, section.Value.GetRawText());
        }
    }

    private string WriteJson()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject(TestZbxConfigurationCommand.ExportRoot);
            foreach (string section in _order)
            {
                writer.WritePropertyName(section);
                if (_scalars.TryGetValue(section, out string? scalar))
                {
                    writer.WriteRawValue(scalar, skipInputValidation: true);
                    continue;
                }
                writer.WriteStartArray();
                foreach (ZabbixConfigurationElement element in _lists[section]) writer.WriteRawValue(element.Text, skipInputValidation: true);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void ReadXml(string text)
    {
        XDocument parsed = XDocument.Parse(text);
        _declaration ??= parsed.Declaration?.ToString();
        foreach (XElement section in parsed.Root!.Elements())
        {
            if (section.HasElements) List(section.Name.LocalName, section.Elements().Select(element => element.ToString(SaveOptions.DisableFormatting)));
            else Scalar(section.Name.LocalName, section.ToString(SaveOptions.DisableFormatting));
        }
    }

    private string WriteXml()
    {
        var root = new XElement(TestZbxConfigurationCommand.ExportRoot);
        foreach (string section in _order)
        {
            root.Add(_scalars.TryGetValue(section, out string? scalar)
                ? XElement.Parse(scalar)
                : new XElement(section, _lists[section].Select(element => XElement.Parse(element.Text))));
        }
        return (_declaration is null ? "" : _declaration + "\n") + root.ToString(SaveOptions.None) + "\n";
    }

    /// <summary>
    /// The YAML writer is Symfony's <c>Yaml::dump($input, 100, 2, DUMP_MULTI_LINE_LITERAL_BLOCK |
    /// DUMP_COMPACT_NESTED_MAPPING)</c> (<c>CYamlExportWriter.php</c>, 7.0–8.0): a section is a line indented exactly two
    /// spaces under <c>zabbix_export:</c>, a list element starts at a line indented exactly four spaces with <c>- </c>, and
    /// everything deeper (nested mappings, literal blocks, whose lines are indented further still) belongs to the element
    /// above it — so the layout splits the document without a YAML parser the module does not carry.
    /// </summary>
    private void ReadYaml(string text)
    {
        string? section = null;
        List<string> elements = [];
        StringBuilder? current = null;
        void Flush()
        {
            if (current is not null) elements.Add(current.ToString());
            current = null;
            if (section is not null) List(section, elements);
            elements = [];
            section = null;
        }

        foreach (string line in text.Split('\n').Skip(1))
        {
            if (line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("   ", StringComparison.Ordinal))
            {
                Flush();
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                string name = line[2..colon];
                if (line.Length > colon + 1) Scalar(name, line);
                else section = name;
            }
            else if (section is not null && line.StartsWith("    - ", StringComparison.Ordinal))
            {
                if (current is not null) elements.Add(current.ToString());
                current = new StringBuilder().Append(line).Append('\n');
            }
            else if (current is not null && line.Length > 0)
            {
                current.Append(line).Append('\n');
            }
        }
        Flush();
    }

    private string WriteYaml()
    {
        var merged = new StringBuilder().Append(TestZbxConfigurationCommand.ExportRoot).Append(":\n");
        foreach (string section in _order)
        {
            if (_scalars.TryGetValue(section, out string? scalar))
            {
                merged.Append(scalar).Append('\n');
                continue;
            }
            merged.Append("  ").Append(section).Append(":\n");
            foreach (ZabbixConfigurationElement element in _lists[section]) merged.Append(element.Text);
        }
        return merged.ToString();
    }
}
