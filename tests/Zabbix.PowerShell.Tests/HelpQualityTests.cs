// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The help's text reads as Microsoft's cmdlet help does: a synopsis is one sentence in the third person, present tense,
/// that says what the user gets; a description adds what the command does on the server, its parameter sets, what it
/// writes and what it needs, in two to five sentences; every example is real code with real values and a remark that is
/// a sentence. Checked on the built MAML of each module, so a regression in the generator's composition, the overlay's
/// words or a help script fails here.
/// </summary>
public sealed partial class HelpQualityTests
{
    private static readonly XNamespace Command = "http://schemas.microsoft.com/maml/dev/command/2004/10";
    private static readonly XNamespace Maml = "http://schemas.microsoft.com/maml/2004/10";
    private static readonly XNamespace Dev = "http://schemas.microsoft.com/maml/dev/2004/10";

    // Synopses written from the noun alone, which this check rejects: "Gets action objects.", "Changes item objects.",
    // "Deletes reg exp objects.", "Creates a action.", "Builds a Tag (modelled on HostTagRequest) as a hashtable ...", and
    // nouns spelled from the API object's code name (reg exp, cep rule, mfa, sla, ha node, smp).
    [GeneratedRegex(@"^(Gets|Changes|Deletes) [a-z ]+ objects\.$|modelled on|as a hashtable any owner|\b(reg exps?|cep rules?|mfas?|slas?|ha nodes?|dhosts?|dservices?|drules?|dchecks?)\b(?!\.\w)")]
    private static partial Regex TemplateSynopsis();

    // "a action", "a item", "an host": an article that does not fit the word after it. "a one-time" and "an hour" fit.
    [GeneratedRegex(@"\b[Aa] (?!one\b|one-)[aeio]\w*|\b[Aa]n (?!hour)[bcdfgjklmnpqrstvwxyz]\w*")]
    private static partial Regex WrongArticle();

    // A sentence ends in . ! or ? before the next sentence's capital, a parameter (-Name), a variable or the end.
    [GeneratedRegex(@"[.!?](?=\s+[A-Z(\-$]|$)")]
    private static partial Regex SentenceEnd();

    // A placeholder an example could carry instead of a value: <name>, value1, 'value', a bare id from nowhere.
    [GeneratedRegex(@"<[a-z\-]+>|'value\d?'|'<|\$item = '\d+'")]
    private static partial Regex Placeholder();

    [Theory]
    [MemberData(nameof(ModuleUnderTest.Names), MemberType = typeof(ModuleUnderTest))]
    public void No_synopsis_is_a_template_and_each_is_one_sentence_in_the_help_voice(string module)
    {
        var violations = new List<string>();
        foreach ((string name, string synopsis, _, _) in Commands(module))
        {
            if (TemplateSynopsis().IsMatch(synopsis)) violations.Add($"{name}: template synopsis \"{synopsis}\"");
            // Third person, present tense: Gets, Creates, Builds, Asks, Applies ...
            if (!VoiceStart().IsMatch(synopsis)) violations.Add($"{name}: \"{synopsis}\" does not start with a verb in the third person");
            if (!synopsis.EndsWith('.') || SentenceEnd().Count(synopsis) != 1) violations.Add($"{name}: \"{synopsis}\" is not one sentence");
            if (synopsis.Contains('—', StringComparison.Ordinal)) violations.Add($"{name}: an em-dash in \"{synopsis}\"");
        }

        Assert.Empty(violations);
    }

    [GeneratedRegex(@"^[A-Z][a-z]+(s|es)\b")]
    private static partial Regex VoiceStart();

    [Theory]
    [MemberData(nameof(ModuleUnderTest.Names), MemberType = typeof(ModuleUnderTest))]
    public void Every_description_says_more_than_its_synopsis_in_two_to_five_sentences(string module)
    {
        var violations = new List<string>();
        foreach ((string name, string synopsis, string description, _) in Commands(module))
        {
            int sentences = SentenceEnd().Count(description);
            if (string.Equals(description, synopsis, StringComparison.Ordinal)) violations.Add($"{name}: the description repeats the synopsis");
            else if (sentences is < 2 or > 5) violations.Add($"{name}: {sentences} sentence(s) in \"{Shorten(description)}\"");
            if (description.Contains('—', StringComparison.Ordinal)) violations.Add($"{name}: an em-dash in the description");
            if (TemplateSynopsis().IsMatch(description) || WrongArticle().Match(description) is { Success: true }) violations.Add($"{name}: \"{Shorten(description)}\"");
        }

        Assert.Empty(violations);
    }

    [Theory]
    [MemberData(nameof(ModuleUnderTest.Names), MemberType = typeof(ModuleUnderTest))]
    public void No_example_carries_a_placeholder_or_a_wrong_article_and_each_command_explains_one(string module)
    {
        var violations = new List<string>();
        foreach ((string name, _, _, IReadOnlyList<(string Title, string Code, string Remarks)> examples) in Commands(module))
        {
            foreach ((string title, string code, string remarks) in examples)
            {
                if (Placeholder().Match(code) is { Success: true } placeholder) violations.Add($"{name}: '{placeholder.Value}' in \"{title}\"");
                if (WrongArticle().Match(title + " " + remarks) is { Success: true } article) violations.Add($"{name}: '{article.Value}' in \"{title}\"");
                if (title.Length == 0 || char.IsLower(title[0])) violations.Add($"{name}: the example title \"{title}\" is not a capitalized phrase");
            }
            // At least one example says what it does in a sentence: a capital (or a parameter, a variable), a period.
            if (!examples.Any(example => example.Remarks.Split('\n').Any(IsSentence))) violations.Add($"{name}: no example remark is a sentence");
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Every_hand_written_command_has_a_help_script_and_the_README_counts_them()
    {
        // A generated command's examples come from the generator (the overlay's words); every other command's from its
        // help script, which tools/Test-ZbxHelpExamples.ps1 runs against a live server.
        string[] scripts = [.. ModuleUnderTest.All.SelectMany(module => Directory.GetFiles(Path.Combine(module.ProjectDirectory, "Examples"), "*.ps1"))];
        var scripted = new HashSet<string>(scripts.Select(Path.GetFileNameWithoutExtension)!, StringComparer.OrdinalIgnoreCase);
        string[] missing = [.. ModuleUnderTest.AllCmdlets
            .Where(type => type.Namespace != "Community.Zabbix.PowerShell.Commands.Generated")
            .Select(type => type.GetCustomAttribute<CmdletAttribute>()!)
            .Select(cmdlet => cmdlet.VerbName + "-" + cmdlet.NounName)
            .Where(name => !scripted.Contains(name))];
        string readme = File.ReadAllText(Path.Combine(ModuleUnderTest.RepositoryRoot, "docs", "design", "powershell-module.md"));

        Assert.Empty(missing);
        Match claim = Assert.Single(ScriptCount().Matches(readme));
        Assert.Equal(scripts.Length, int.Parse(claim.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"\b(\d+) help scripts\b")]
    private static partial Regex ScriptCount();

    private static bool IsSentence(string remark)
    {
        string text = remark.Trim();
        return text.Length > 1 && (char.IsUpper(text[0]) || text[0] is '-' or '$') && text[^1] is '.' or '!' or '?';
    }

    private static string Shorten(string text) => text.Length <= 90 ? text : text[..90] + "...";

    // Every command of the module's built MAML: its name, synopsis, description and examples.
    private static IEnumerable<(string Name, string Synopsis, string Description, IReadOnlyList<(string Title, string Code, string Remarks)> Examples)> Commands(string module)
    {
        foreach (XElement entry in XDocument.Load(ModuleUnderTest.Named(module).HelpFile).Descendants(Command + "command"))
        {
            XElement details = entry.Element(Command + "details")!;
            string synopsis = string.Join(' ', details.Element(Maml + "description")!.Elements(Maml + "para").Select(p => p.Value.Trim()));
            string description = string.Join(' ', entry.Element(Maml + "description")!.Elements(Maml + "para").Select(p => p.Value.Trim()));
            IReadOnlyList<(string, string, string)> examples = [.. entry.Element(Command + "examples")!.Elements(Command + "example").Select(example => (
                ExampleTitle().Match(example.Element(Maml + "title")!.Value).Groups[1].Value,
                example.Element(Dev + "code")!.Value,
                string.Join('\n', example.Element(Dev + "remarks")!.Elements(Maml + "para").Select(p => p.Value))))];
            yield return (details.Element(Command + "name")!.Value, synopsis, description, examples);
        }
    }

    // "-------------------------- Example 1: Get a host by name --------------------------" -> "Get a host by name".
    [GeneratedRegex(@"Example \d+: (.*?) -+$")]
    private static partial Regex ExampleTitle();
}
