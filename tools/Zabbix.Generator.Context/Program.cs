// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using System.Text;
using ZabbixGenerator::Community.Zabbix.Generator;

// Zabbix.Generator.Context <description.json> <output.cs>: the JSON context the Zabbix assembly compiles, written only when
// it changed so an unchanged description does not force a recompile. A description with problems fails the build step
// (exit 1, each problem on stderr) rather than writing a context that silently lacks what was left out.
if (args.Length != 2)
{
    await Console.Error.WriteLineAsync("usage: Zabbix.Generator.Context <zabbix-api.json> <ZabbixApiJson.g.cs>").ConfigureAwait(false);
    return 2;
}
var problems = new List<EmitProblem>();
ApiDescription? api = ApiDescription.Read(await File.ReadAllTextAsync(args[0]).ConfigureAwait(false), problems);
EmitResult? result = api is null ? null : new ZabbixApiEmitter(api).ContextSource();
problems.AddRange(result?.Problems ?? []);
if (result is null || problems.Count > 0)
{
    foreach (EmitProblem problem in problems)
        await Console.Error.WriteLineAsync($"{args[0]}: error {problem.Id}: {problem.Subject}: {problem.Message}").ConfigureAwait(false);
    return 1;
}
string code = result.Files[0].Code;
if (!File.Exists(args[1]) || await File.ReadAllTextAsync(args[1]).ConfigureAwait(false) != code)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    await File.WriteAllTextAsync(args[1], code, new UTF8Encoding(false)).ConfigureAwait(false);
}
return 0;
