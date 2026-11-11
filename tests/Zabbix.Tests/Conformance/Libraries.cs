// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyModel;

namespace Community.Zabbix.Tests.Conformance;

/// <summary>
/// The assemblies under conformance, computed. Every project this test project references (its
/// <c>deps.json</c> lists each as a <c>project</c> library, transitively), minus Roslyn components (a source generator
/// references <c>Microsoft.CodeAnalysis</c> and is analyzer code, built to <c>netstandard2.0</c> rules). A new library
/// project is under every rule the day the test project references it; nothing here is a hand-kept list.
/// </summary>
internal static class Libraries
{
    public static IReadOnlyList<Assembly> All { get; } = Load();

    /// <summary>The root namespace every library's public surface grows from: the longest common namespace prefix.</summary>
    public static string RootNamespace { get; } = CommonRoot(All.SelectMany(library => library.GetExportedTypes()).Select(type => type.Namespace ?? ""));

    public static IEnumerable<Type> Types => All.SelectMany(library => library.GetTypes());

    public static IEnumerable<Type> PublicTypes => All.SelectMany(library => library.GetExportedTypes());

    public static bool Contains(Assembly assembly) => All.Contains(assembly);

    /// <summary>
    /// The project folder of <paramref name="library"/> under <c>src</c>: its assembly name, which is its package id
    /// (docs/adr/0002-package-ids.md), without <c>Community.</c>.
    /// </summary>
    public static string Folder(Assembly library) => library.GetName().Name!["Community.".Length..];

    private static List<Assembly> Load()
    {
        string self = typeof(Libraries).Assembly.GetName().Name!;
        DependencyContext context = DependencyContext.Load(typeof(Libraries).Assembly)
            ?? throw new InvalidOperationException("the test project has no deps.json");
        // Each library is loaded by its runtime asset's file name, its assembly name (Community.Zabbix.Sdk), as the
        // deps.json lists it.
        return [.. context.RuntimeLibraries
            .Where(library => library.Type == "project" && library.Name != self)
            .SelectMany(library => library.RuntimeAssemblyGroups.SelectMany(group => group.AssetPaths))
            .Select(path => Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(path))))
            .Where(assembly => !assembly.GetReferencedAssemblies().Any(reference => reference.Name == "Microsoft.CodeAnalysis"))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)];
    }

    private static string CommonRoot(IEnumerable<string> namespaces)
    {
        string[]? common = null;
        foreach (string[] parts in namespaces.Select(name => name.Split('.')))
        {
            if (common is null) { common = parts; continue; }
            int shared = 0;
            while (shared < common.Length && shared < parts.Length && common[shared] == parts[shared]) shared++;
            common = common[..shared];
        }
        return string.Join('.', common ?? []);
    }
}

/// <summary>
/// The libraries' call graph, read from their IL. An edge is a <c>call</c>, <c>callvirt</c>, <c>newobj</c>,
/// <c>ldftn</c> or <c>ldvirtftn</c> (so a lambda or local function is reached from the method that creates it), plus
/// the compiler's own indirections: a kickoff method reaches its state machine's <c>MoveNext</c>, and a call to a
/// library-declared interface or virtual method reaches every library implementation of it. Methods are identified by
/// module and metadata token, so a call through a constructed generic type meets its definition.
/// </summary>
internal sealed class CallGraph
{
    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    private readonly Dictionary<MethodKey, MethodBase> _methods = [];
    private readonly Dictionary<MethodKey, HashSet<MethodKey>> _callees = [];
    private readonly Dictionary<MethodKey, HashSet<MethodKey>> _callers = [];
    private readonly Dictionary<MethodKey, List<(OpCode Op, object Operand)>> _instructions = [];

    static CallGraph()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)op.Value);
            if (value < 0x100) OneByte[value] = op;
            else if ((value & 0xFF00) == 0xFE00) TwoByte[value & 0xFF] = op;
        }
    }

    private CallGraph(IEnumerable<Assembly> assemblies)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        List<Type> types = [.. assemblies.SelectMany(assembly => assembly.GetTypes())];
        foreach (Type type in types)
        {
            foreach (MethodBase method in type.GetMethods(declared).Cast<MethodBase>().Concat(type.GetConstructors(declared)).Concat(type.TypeInitializer is { } cctor ? [cctor] : []))
            {
                MethodKey key = MethodKey.Of(method);
                _methods[key] = method;
                List<(OpCode, object)> body = Read(method);
                _instructions[key] = body;
                foreach ((OpCode op, object operand) in body)
                    if (operand is MethodBase callee && IsCall(op)) Link(key, callee);
                if (method.GetCustomAttribute<StateMachineAttribute>() is { StateMachineType: { } machine }
                    && machine.GetMethod("MoveNext", declared) is { } moveNext)
                    Link(key, moveNext);
            }
        }

        // Dispatch: a call to a library-declared abstraction reaches its library implementations.
        foreach (Type type in types.Where(type => !type.IsInterface))
        {
            foreach (Type contract in type.GetInterfaces().Where(contract => Libraries.Contains(contract.Assembly)))
            {
                InterfaceMapping map;
                try { map = type.GetInterfaceMap(contract); }
                catch (ArgumentException) { continue; }
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                    if (map.TargetMethods[i].DeclaringType == type) Link(MethodKey.Of(map.InterfaceMethods[i]), map.TargetMethods[i]);
            }
            foreach (MethodInfo method in type.GetMethods(declared).Where(method => method.IsVirtual))
            {
                MethodInfo root = method.GetBaseDefinition();
                if (root != method && Libraries.Contains(root.Module.Assembly)) Link(MethodKey.Of(root), method);
            }
        }
    }

    private static readonly Lazy<CallGraph> Built = new(() => new(Libraries.All));

    // Lazy: a static initializer would run before the static constructor fills the opcode tables.
    public static CallGraph Of => Built.Value;

    /// <summary>Every method declared in the libraries, compiler-generated ones included.</summary>
    public IEnumerable<MethodBase> Methods => _methods.Values;

    public IEnumerable<(OpCode Op, object Operand)> Instructions(MethodBase method)
        => _instructions.TryGetValue(MethodKey.Of(method), out List<(OpCode, object)>? body) ? body : [];

    public IEnumerable<MethodBase> Callers(MethodBase method)
        => _callers.TryGetValue(MethodKey.Of(method), out HashSet<MethodKey>? callers) ? callers.Select(key => _methods[key]) : [];

    /// <summary>Whether <paramref name="method"/> calls something <paramref name="target"/> accepts, directly or through anything it reaches.</summary>
    public bool Reaches(MethodBase method, Func<MethodBase, bool> target)
    {
        HashSet<MethodKey> seen = [];
        Stack<MethodKey> pending = new([MethodKey.Of(method)]);
        while (pending.TryPop(out MethodKey key))
        {
            if (!seen.Add(key) || !_callees.TryGetValue(key, out HashSet<MethodKey>? callees)) continue;
            foreach (MethodKey callee in callees)
            {
                if (_resolved.TryGetValue(callee, out MethodBase? resolved) && target(resolved)) return true;
                pending.Push(callee);
            }
        }
        return false;
    }

    /// <summary>The methods <paramref name="method"/> calls directly.</summary>
    public IEnumerable<MethodBase> Callees(MethodBase method)
        => _callees.TryGetValue(MethodKey.Of(method), out HashSet<MethodKey>? callees) ? callees.Select(key => _resolved[key]) : [];

    private readonly Dictionary<MethodKey, MethodBase> _resolved = [];

    private void Link(MethodKey caller, MethodBase callee)
    {
        MethodKey key = MethodKey.Of(callee);
        _resolved.TryAdd(key, callee);
        if (!_callees.TryGetValue(caller, out HashSet<MethodKey>? callees)) _callees[caller] = callees = [];
        callees.Add(key);
        if (!_callers.TryGetValue(key, out HashSet<MethodKey>? callers)) _callers[key] = callers = [];
        callers.Add(caller);
    }

    private static bool IsCall(OpCode op)
        => op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn;

    /// <summary>The method's instructions with their operands resolved (a member, a type, a string), or none.</summary>
    private static List<(OpCode, object)> Read(MethodBase method)
    {
        List<(OpCode, object)> instructions = [];
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (InvalidOperationException) { return instructions; }
        if (il is null) return instructions;
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        Type[]? methodArguments = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null;
        int at = 0;
        while (at < il.Length)
        {
            byte code = il[at++];
            OpCode op = code == 0xFE ? TwoByte[il[at++]] : OneByte[code];
            int size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
                _ => 4,
            };
            object operand = op.OperandType switch
            {
                OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok
                    => Resolve(method.Module, BitConverter.ToInt32(il, at), typeArguments, methodArguments),
                OperandType.InlineString => method.Module.ResolveString(BitConverter.ToInt32(il, at)),
                _ => DBNull.Value,
            };
            instructions.Add((op, operand));
            at += size;
        }
        return instructions;
    }

    private static object Resolve(Module module, int token, Type[]? typeArguments, Type[]? methodArguments)
    {
        try { return module.ResolveMember(token, typeArguments, methodArguments) ?? (object)DBNull.Value; }
        catch (ArgumentException) { return DBNull.Value; }
        catch (BadImageFormatException) { return DBNull.Value; }
        catch (TypeLoadException) { return DBNull.Value; }
        catch (FileNotFoundException) { return DBNull.Value; }
    }

    private readonly record struct MethodKey(Module Module, int Token)
    {
        public static MethodKey Of(MethodBase method) => new(method.Module, method.MetadataToken);
    }
}
