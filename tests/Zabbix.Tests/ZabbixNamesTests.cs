// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using System.Reflection;
using ZabbixGenerator::Community.Zabbix.Generator;

namespace Community.Zabbix.Tests;

/// <summary>
/// A field's, a record's and an enum's C# name is made of its words (<c>schema/words.json</c>, <see cref="ZabbixNames"/>),
/// not the mechanical capitalisation of a lower-case compound (<c>Sortorder</c>, <c>OpmessageGrp</c>, <c>HkTrends</c>,
/// <c>ActionOperationOpgroupRequest</c>, <c>DsvcStatus</c>).
/// </summary>
public sealed class ZabbixNamesTests
{
    private static readonly ApiDescription Description = ApiDescription.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json")));

    [Theory]
    [InlineData("sortorder", "Sortorder", "SortOrder")]
    [InlineData("evaltype", "Evaltype", "EvalType")]
    [InlineData("conditiontype", "Conditiontype", "ConditionType")]
    [InlineData("drawtype", "Drawtype", "DrawType")]
    [InlineData("viewtype", "Viewtype", "ViewType")]
    [InlineData("opmessage", "Opmessage", "OperationMessage")]
    [InlineData("opmessage_grp", "OpmessageGrp", "OperationMessageGroup")]
    [InlineData("opgroup", "Opgroup", "OperationGroup")]
    [InlineData("passwd", "Passwd", "Password")]
    [InlineData("selementid1", "Selementid1", "Element1Id")]
    [InlineData("hk_trends", "HkTrends", "HousekeepingTrends")]
    [InlineData("ipmi_authtype", "IpmiAuthtype", "IpmiAuthType")]
    [InlineData("usrgrpid", "UsrgrpId", "UserGroupId")]
    [InlineData("opdata", "Opdata", "OperationalData")]
    [InlineData("snmpv3_securityname", "Snmpv3Securityname", "Snmpv3SecurityName")]
    [InlineData("hostid", "HostId", "HostId")]
    [InlineData("templateids", "TemplateIds", "TemplateIds")]
    [InlineData("timeline", "Timeline", "Timeline")]
    [InlineData("selectHosts", "SelectHosts", "SelectHosts")]
    [InlineData("poc_1_name", "Poc1Name", "Poc1Name")]
    public void A_wire_name_is_named_by_its_words(string wire, string mechanical, string friendly)
    {
        Assert.Equal(mechanical, ZabbixNames.Mechanical(wire));
        Assert.Equal(friendly, ZabbixNames.Friendly(wire));
    }

    [Fact]
    public void Every_wire_part_the_vocabulary_cannot_split_is_a_reviewed_abbreviation()
    {
        // The dictionary's gaps: a lower-case part no known words make up stays one capitalised word. Each of these is
        // one word or abbreviation, not a compound (authn: SAML's AuthnRequest; helo: SMTP's HELO; sli: the service level
        // indicator sla.getsli answers); a new compound in a later Zabbix version fails here until schema/words.json learns
        // its words.
        string[] reviewed = ["a", "authn", "b", "c", "cert", "config", "d", "e", "exec", "exp", "helo", "num", "p", "params", "r", "regex", "sli", "stats", "x", "y"];

        string[] unsplit = [.. Description.Types.SelectMany(t => t.Fields).SelectMany(f => ZabbixNames.Unsplit(f.Wire)).Distinct().Order(StringComparer.Ordinal)];

        Assert.Equal(reviewed, unsplit);
    }

    [Fact]
    public void A_name_the_overlay_chose_is_kept_by_the_words_then_respelled_and_a_renamed_field_remembers_its_first_name()
    {
        FieldDescription[] fields = [.. Description.Types.SelectMany(t => t.Fields)];

        Assert.Contains(fields, f => f.Wire == "host" && f.Name == "HostName" && f.PreviousName == "TechnicalName");
        Assert.Contains(fields, f => f.Wire == "evaltype" && f.Name == "EvaluationType" && f.PreviousName == "Evaltype");
        Assert.Contains(fields, f => f.Wire == "name" && f.Name == "VisibleName" && f.PreviousName is null);
        Assert.DoesNotContain(fields, f => f.PreviousName is not null && f.PreviousName == f.Name);
        Assert.All(Description.Types, t => Assert.Equal(t.Fields.Length, t.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count()));
    }

    [Theory]
    [InlineData("ActionOperationOpgroupRequest", "ActionOperationGroupRequest")]
    [InlineData("ActionReadOperationOpmessageGrp", "ActionReadOperationMessageGroup")]
    [InlineData("ActionOperationOpcommandHstRequest", "ActionOperationCommandHostRequest")]
    [InlineData("HostGroupGroupdiscovery", "HostGroupDiscovery")]
    [InlineData("ValuemapMappingType", "ValueMapMappingType")]
    [InlineData("DhostStatus", "DiscoveredHostStatus")]
    [InlineData("DsvcStatus", "DiscoveredServiceStatus")]
    [InlineData("DruleStatus", "NetworkDiscoveryRuleStatus")]
    [InlineData("HttptestStatus", "HttpTestStatus")]
    [InlineData("MapSelementRequest", "MapElementRequest")]
    [InlineData("SysmapShapeLabelHalign", "MapShapeLabelHAlign")]
    [InlineData("UserUsrgrpRequest", "UserGroupRequest")]
    [InlineData("NetworkDiscoveryRule", "NetworkDiscoveryRule")]
    [InlineData("SlaRecordExcludedDowntime", "SlaRecordExcludedDowntime")]
    [InlineData("RegExpExpression", "RegExpExpression")]
    [InlineData("HostTagRequest", "HostTagRequest")]
    public void A_record_or_enum_is_named_by_its_words_a_repeated_word_said_once(string name, string named)
        => Assert.Equal(named, ZabbixNames.TypeName(name));

    [Theory]
    [InlineData("HostHostDiscovery", "HostDiscovery")]
    [InlineData("ApmLogLogAttributeFilter", "ApmLogAttributeFilter")]
    [InlineData("TemplateTemplateRequest", "TemplateRequest")]
    [InlineData("HostDiscoveryData", "HostDiscoveryData")]
    public void A_record_name_says_a_repeated_part_once(string name, string named)
    {
        Assert.Equal(named, ZabbixNames.Once(name));
        Assert.Contains(named, Description.Types.Select(t => t.Name));
    }

    [Fact]
    public void Every_record_and_enum_name_part_the_vocabulary_cannot_split_is_a_reviewed_abbreviation()
    {
        // reg and exp: RegExp, the regexp object's own name; regex: the import rule's globalRegex; sli: an SLA's service
        // level indicator (sla.getsli); h and v: HAlign and VAlign (halign, valign: a map shape's label alignment); y: the Y
        // axis of GraphYAxisType and GraphYAxisSide; a, h, i, p and u: the letters of the two-letter acronyms the
        // respellings write in capitals (HANode, the host interface's UseIP, the role rules' UI), which a name split at each
        // capital reads one at a time. Every other part of every name is words.
        string[] reviewed = ["a", "exp", "h", "i", "p", "reg", "regex", "sli", "u", "v", "y"];

        string[] unsplit = [.. Description.Types.Select(t => t.Name).Concat(Description.Enums.Select(e => e.Name)).SelectMany(ZabbixNames.UnsplitType).Distinct().Order(StringComparer.Ordinal)];

        Assert.Equal(reviewed, unsplit);
    }

    [Theory]
    [InlineData(typeof(ZabbixItemType), "HttpAgent", "ITEM_TYPE_HTTPAGENT")]
    [InlineData(typeof(ZabbixItemType), "HttpTest", "ITEM_TYPE_HTTPTEST")]
    [InlineData(typeof(ZabbixItemType), "SnmpTrap", "ITEM_TYPE_SNMPTRAP")]
    [InlineData(typeof(ZabbixEventObject), "AutoregistrationHost", "EVENT_OBJECT_AUTOREGHOST")]
    [InlineData(typeof(ZabbixEventObject), "DiscoveredHost", "EVENT_OBJECT_DHOST")]
    [InlineData(typeof(ZabbixItemState), "NotSupported", "ITEM_STATE_NOTSUPPORTED")]
    [InlineData(typeof(ZabbixTaskStatus), "InProgress", "ZBX_TM_STATUS_INPROGRESS")]
    [InlineData(typeof(ZabbixPreprocessingType), "LeftTrim", "ZBX_PREPROC_LTRIM")]
    [InlineData(typeof(ZabbixPreprocessingType), "BooleanToDecimal", "ZBX_PREPROC_BOOL2DEC")]
    [InlineData(typeof(ZabbixGraphItemCalculationFunction), "Last", "CALC_FNC_LST")]
    [InlineData(typeof(ZabbixSnmpSecurityLevel), "AuthenticationNoPrivacy", "ITEM_SNMPV3_SECURITYLEVEL_AUTHNOPRIV")]
    public void An_enum_member_is_named_by_its_words(Type type, string member, string define)
    {
        Assert.True(Enum.IsDefined(type, Enum.Parse(type, member)), member);
        Assert.Contains(Description.Enums.Single(e => "Zabbix" + e.Name == type.Name).Members, m => m.Name == member && m.Define == define);
    }

    [Fact]
    public void Every_enum_member_name_part_the_vocabulary_cannot_split_is_a_reviewed_abbreviation()
    {
        // The record and field gaps again (exp, reg, regex), the letters of the two-letter acronyms written in capitals (a,
        // h, i and p: HANode, DiscoveredHostIP, IP), and the parts a member name keeps as one word on purpose: aes192c,
        // aes256c, v2c and snmpv2c (cipher and SNMP version names); d, n, v and x (Pie3D, AtLeastN, V1, XPath); hashi, corp,
        // cyber and ark (HashiCorp and CyberArk, the vendors' own spelling).
        string[] reviewed = ["a", "aes192c", "aes256c", "ark", "corp", "cyber", "d", "exp", "h", "hashi", "i", "n", "p", "reg", "regex", "snmpv2c", "v", "v2c", "x"];

        string[] unsplit = [.. Description.Enums.SelectMany(e => e.Members).Select(m => m.Name).SelectMany(ZabbixNames.UnsplitType).Distinct().Order(StringComparer.Ordinal)];

        Assert.Equal(reviewed, unsplit);
    }

    [Fact]
    public void A_renamed_record_or_enum_is_renamed_wherever_the_description_names_it()
    {
        HashSet<string> declared = [.. Description.Types.Select(t => t.Name), .. Description.Enums.Select(e => e.Name)];
        IEnumerable<string> kinds = Description.Types.SelectMany(t => t.Fields).Select(f => f.Kind)
            .Concat(Description.Services.SelectMany(s => s.Methods).SelectMany(m => new[] { m.Parameters, m.Result }));
        string[] named = [.. kinds.SelectMany(kind => System.Text.RegularExpressions.Regex.Matches(kind, @"(?:enum|object):(\w+)").Select(m => m.Groups[1].Value))];

        Assert.All(named, name => Assert.Contains(name, declared));
        Assert.All(Description.Services.Where(s => s.Model is not null), s => Assert.Contains(s.Model!, declared));
        Assert.Equal(declared.Count, Description.Types.Length + Description.Enums.Length);
        Assert.Contains("ActionOperationGroupRequest", declared);
        Assert.DoesNotContain("ActionOperationOpgroupRequest", declared);
    }

    [Fact]
    public void One_wire_field_has_one_name_in_every_record_of_its_object()
    {
        // A request names a field as the read record does, and so does the nested request it holds where the read record
        // holds a nested record under the same wire field. The reviewed exceptions are one wire name with two values: a
        // template's read templates are the templates linked to it, a request's the templates it links; a mass removal's
        // macros are names or ids, not macros; a .get's sortorder is the sort direction, a record's its position.
        string[] reviewed =
        [
            "GraphItemGetParameters.sortorder: SortOrder (read GraphItem.Position)",
            "HostMassRemoveRequest.macros: MacroNames (read Host.Macros)",
            "ServiceGetParameters.sortorder: SortOrder (read ServiceRecord.Position)",
            "TemplateCreateRequest.templates: LinkedTemplates (read Template.ChildTemplates)",
            "TemplateMassAddRequest.templates: Templates (read Template.ChildTemplates)",
            "TemplateMassRemoveRequest.macros: MacroIds (read Template.Macros)",
            "TemplateMassUpdateRequest.templates: Templates (read Template.ChildTemplates)",
            "TemplateUpdateRequest.templates: LinkedTemplates (read Template.ChildTemplates)",
        ];

        Dictionary<string, TypeDescription> types = Description.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var differences = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();
        void Compare(TypeDescription read, TypeDescription other)
        {
            if (!seen.Add((read.Name, other.Name))) return;
            Dictionary<string, FieldDescription> named = read.Fields.ToDictionary(f => f.Wire, StringComparer.Ordinal);
            foreach (FieldDescription field in other.Fields)
            {
                if (!named.TryGetValue(field.Wire, out FieldDescription? readField)) continue;
                if (readField.Name != field.Name) differences.Add($"{other.Name}.{field.Wire}: {field.Name} (read {read.Name}.{readField.Name})");
                if (Nested(readField.Kind) is { } readNested && Nested(field.Kind) is { } otherNested
                    && types.TryGetValue(readNested, out TypeDescription? r) && types.TryGetValue(otherNested, out TypeDescription? o))
                    Compare(r, o);
            }
        }
        foreach (ServiceDescription service in Description.Services.Where(s => s.Model is not null && types.ContainsKey(s.Model)))
        {
            foreach (TypeDescription other in Description.Types.Where(t => t.Object == service.Object && t.Role is "request" or "get"))
                Compare(types[service.Model!], other);
        }

        Assert.Equal(reviewed, differences);

        static string? Nested(string kind) => System.Text.RegularExpressions.Regex.Match(kind, @"object:(\w+)$") is { Success: true } m ? m.Groups[1].Value : null;
    }

    [Theory]
    [InlineData(typeof(ZabbixActionOperationRequest), "OperationMessage", "opmessage")]
    [InlineData(typeof(ZabbixActionOperationRequest), "OperationMessageGroup", "opmessage_grp")]
    [InlineData(typeof(ZabbixHousekeepingSettings), "HousekeepingTrends", "hk_trends")]
    [InlineData(typeof(ZabbixMapLinkRequest), "Element1Id", "selementid1")]
    [InlineData(typeof(ZabbixHostInterfaceDetail), "SecurityLevel", "securitylevel")]
    public void The_generated_record_carries_the_name_and_keeps_the_wire_field(Type record, string property, string wire)
    {
        PropertyInfo? found = record.GetProperty(property);

        Assert.NotNull(found);
        Assert.Equal(wire, found.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name);
    }
}
