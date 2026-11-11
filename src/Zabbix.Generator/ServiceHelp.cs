// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Generator;

/// <summary>
/// What the PowerShell help says about an API object, from <c>schema/overlay.json</c>'s <c>objects.&lt;object&gt;.help</c>:
/// the words for it, the example name and the example arguments the generated cmdlets' synopses, descriptions and
/// examples are composed from (<see cref="ZabbixCmdletEmitter"/>).
/// </summary>
/// <param name="Noun">The object as a reader names it, singular, no article (<c>action</c>, <c>SLA</c>).</param>
/// <param name="Plural">The plural; <see langword="null"/> when it is the noun plus <c>s</c>.</param>
/// <param name="Role">A relative clause that says what the objects are for (<c>that react to events</c>), after the plural in the Get synopsis.</param>
/// <param name="Sample">The name the examples give one (<c>Notify on disaster</c>); other objects' examples name it too.</param>
/// <param name="Create">The New example's arguments besides the name and the owner (<c>-EventSource Triggers ...</c>).</param>
/// <param name="CreateSetup">Statements the New example runs first (reading an id the arguments use).</param>
/// <param name="CreateNote">A sentence the New example's remarks add.</param>
/// <param name="Change">The Set example's arguments (<c>-Status Disabled</c>).</param>
/// <param name="ChangeNote">The Set example's remark: what the change does.</param>
/// <param name="Find">A pipeline that writes the object the Set and Remove examples change, for an object without a name.</param>
/// <param name="Found">How the remarks name the object <see cref="Find"/> writes (<c>the agent interface of web-01</c>).</param>
internal sealed record ServiceHelp(
    string Noun,
    string? Plural,
    string? Role,
    string? Sample,
    string? Create,
    string? CreateSetup,
    string? CreateNote,
    string? Change,
    string? ChangeNote,
    string? Find,
    string? Found)
{
    /// <summary>The plural as the help writes it.</summary>
    public string Nouns => Plural ?? Noun + "s";

    /// <summary>The noun with its indefinite article (<c>an action</c>, <c>a user</c>, <c>an SLA</c>).</summary>
    public string WithArticle => Article(Noun) + " " + Noun;

    public static ServiceHelp? Read(JsonElement service)
    {
        if (!service.TryGetProperty("help", out JsonElement help) || help.ValueKind != JsonValueKind.Object) return null;
        return new(
            help.GetProperty("noun").GetString()!,
            ApiDescription.Text(help, "plural"),
            ApiDescription.Text(help, "role"),
            ApiDescription.Text(help, "sample"),
            ApiDescription.Text(help, "create"),
            ApiDescription.Text(help, "createSetup"),
            ApiDescription.Text(help, "createNote"),
            ApiDescription.Text(help, "change"),
            ApiDescription.Text(help, "changeNote"),
            ApiDescription.Text(help, "find"),
            ApiDescription.Text(help, "found"));
    }

    /// <summary>
    /// <c>a</c> or <c>an</c> by the sound the word starts with: a vowel letter (but not the <c>you</c> of <c>user</c>), or
    /// an acronym whose first letter's name starts with a vowel sound (<c>an SLA</c>, <c>an HTTP agent</c>, <c>an LLD rule</c>).
    /// </summary>
    /// <param name="word">The noun phrase.</param>
    public static string Article(string word)
    {
        if (word is null) throw new ArgumentNullException(nameof(word));
        string first = word.Split(' ')[0];
        if (first.Length > 1 && first.All(char.IsUpper)) return "AEFHILMNORSX".IndexOf(first[0]) >= 0 ? "an" : "a";
        char c = char.ToLowerInvariant(word[0]);
        if (c == 'u') return word.StartsWith("un", StringComparison.OrdinalIgnoreCase) ? "an" : "a";
        return "aeio".IndexOf(c) >= 0 ? "an" : "a";
    }
}
