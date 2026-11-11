// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// The reference documentation of the client packages, held as StyleCop's SA1611 (every parameter), SA1615 (a return
/// value) and SA1618 (every type parameter) would hold it, read from the XML documentation the build writes: a public
/// method that returns something says what, and a parameter's documentation is a phrase that says what it is for, not
/// two words. CS1591 already requires a summary on every public member.
/// </summary>
public sealed partial class DesignConformanceTests
{
    [Fact]
    public void Every_public_method_of_the_client_packages_documents_its_return_parameters_and_type_parameters()
        => AssertNone(UndocumentedSignatures(Libraries.All.Where(library => ClientPackages.Contains(library.GetName().Name))));

    /// <summary>
    /// A parameter documented in two words or fewer ("The text.", "Where.") says nothing its name does not. The client
    /// packages' count may only fall: lower the ceiling when a sentence replaces one.
    /// </summary>
    [Fact]
    public void Terse_parameter_documentation_in_the_client_packages_only_falls()
    {
        const int Ceiling = 133;
        int terse = Libraries.All.Where(library => ClientPackages.Contains(library.GetName().Name))
            .SelectMany(library => XDocument.Load(Path.ChangeExtension(library.Location, ".xml")).Descendants("param"))
            .Count(param => string.Join(' ', param.Value.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Split(' ').Length <= 2);

        Assert.True(terse <= Ceiling, $"{terse} parameters are documented in two words or fewer; the ceiling is {Ceiling}");
    }

    internal static IEnumerable<string> UndocumentedSignatures(IEnumerable<Assembly> libraries)
    {
        foreach (Assembly library in libraries)
        {
            string xml = Path.ChangeExtension(library.Location, ".xml");
            Dictionary<string, XElement> members = XDocument.Load(xml).Descendants("member")
                .Where(member => member.Attribute("name") is not null)
                .ToDictionary(member => member.Attribute("name")!.Value, StringComparer.Ordinal);
            foreach (MethodBase method in library.GetExportedTypes()
                .Where(type => !typeof(Delegate).IsAssignableFrom(type))
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                    .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)))
                .Where(method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)
                .Where(method => !method.IsSpecialName || method is ConstructorInfo || method.Name.StartsWith("op_", StringComparison.Ordinal)))
            {
                if (method.DeclaringType!.IsEnum) continue;
                if (!members.TryGetValue(DocumentationId(method), out XElement? doc) || doc.Element("inheritdoc") is not null) continue;
                string where = $"{method.DeclaringType!.FullName}.{method.Name}";
                if (method is MethodInfo { ReturnType: var returns } && returns != typeof(void) && doc.Element("returns") is null)
                    yield return $"{where}: no <returns>";
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    XElement? param = doc.Elements("param").FirstOrDefault(element => element.Attribute("name")?.Value == parameter.Name);
                    if (param is null) yield return $"{where}: no <param name=\"{parameter.Name}\">";
                }
                if (method.IsGenericMethodDefinition)
                {
                    foreach (Type generic in method.GetGenericArguments())
                        if (!doc.Elements("typeparam").Any(element => element.Attribute("name")?.Value == generic.Name)) yield return $"{where}: no <typeparam name=\"{generic.Name}\">";
                }
            }
        }
    }

    /// <summary>The documentation id of a method (ECMA-334 annex D), as the compiler writes it into the XML file.</summary>
    private static string DocumentationId(MethodBase method)
    {
        var id = new StringBuilder("M:").Append(TypeId(method.DeclaringType!, generic: false)).Append('.');
        id.Append(method is ConstructorInfo ? "#ctor" : method.Name);
        if (method.IsGenericMethodDefinition) id.Append("``").Append(method.GetGenericArguments().Length);
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length > 0) id.Append('(').Append(string.Join(',', parameters.Select(parameter => TypeId(parameter.ParameterType, generic: true)))).Append(')');
        if (method.Name is "op_Implicit" or "op_Explicit" && method is MethodInfo conversion) id.Append('~').Append(TypeId(conversion.ReturnType, generic: true));
        return id.ToString();
    }

    private static string TypeId(Type type, bool generic)
    {
        if (type.IsGenericParameter) return (type.DeclaringMethod is null ? "`" : "``") + type.GenericParameterPosition;
        if (type.IsByRef) return TypeId(type.GetElementType()!, generic) + "@";
        if (type.IsArray) return TypeId(type.GetElementType()!, generic) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsPointer) return TypeId(type.GetElementType()!, generic) + "*";
        string name = type.IsNested ? TypeId(type.DeclaringType!, generic: false) + "." + type.Name : (type.Namespace is { Length: > 0 } ns ? ns + "." : "") + type.Name;
        if (!generic || !type.IsGenericType) return name;
        int tick = name.LastIndexOf('`');
        string bare = tick >= 0 ? name[..tick] : name;
        Type[] arguments = type.GetGenericArguments();
        if (type.IsNested && type.DeclaringType!.IsGenericType) arguments = arguments[type.DeclaringType.GetGenericArguments().Length..];
        return arguments.Length == 0 ? bare : bare + "{" + string.Join(',', arguments.Select(argument => TypeId(argument, generic: true))) + "}";
    }
}
