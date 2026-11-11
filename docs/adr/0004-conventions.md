<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# 0004. The tree follows Microsoft's own repository conventions

- **Status:** Accepted; amended 2026-10-07 (`InternalsVisibleTo` across packages), 2026-10-12 (the package ids, the icon) and 2026-10-30 (the cmdlet reference is committed; below)
- **Date:** 2026-10-05

## Context

A review on 2026-10-04 held the tree against the conventions Microsoft's own repositories keep (dotnet/runtime,
dotnet/extensions, dotnet/roslyn) and the .NET library guidance on Learn, and found it deviating in nine places: folders
that did not name their namespaces, files of up to 20 types, generated files checked into `src/`, package versions in
every project, packages without readmes or symbols, a source generator on `net10.0`, a global `CS0436` suppression, and
style rules that were practised but not written down. None of it was a defect a user could see; all of it made the tree
harder to navigate and to review than the platform's own.

## Decision

Each rule is enforced by the build or a test, not by review.

| Rule | Enforced by | Microsoft reference |
| --- | --- | --- |
| A file's namespace is its project's `RootNamespace` plus its folder path. The SDK libraries are rooted at `Community.Zabbix`; a project whose types all live in one deeper namespace is rooted there (`Community.Zabbix.PowerShell`, `Community.Zabbix.Storage.EntityFrameworkCore`, `Community.Zabbix.Generator`), as `Microsoft.Extensions.Logging.Abstractions` is rooted at `Microsoft.Extensions.Logging`. Move the file, not the namespace. | IDE0130 at error (`.editorconfig`) | Learn, IDE0130 "Namespace does not match folder structure"; dotnet/runtime's `src/libraries/*/src` layout |
| `Add*` extensions on `IServiceCollection`, and every verb on an `IZabbix…Builder` they return, stay in the library's root namespace (`Community.Zabbix`), never `Microsoft.Extensions.DependencyInjection` and never a layer's sub-namespace. | `DesignConformanceTests.Every_add_extension_lives_in_the_root_namespace`, `Every_builder_verb_lives_in_the_root_namespace` | Learn, "Options pattern guidance for .NET library authors", Namespace guidance: "DO NOT use the `Microsoft.Extensions.DependencyInjection` namespace for non-official Microsoft packages" |
| One public type per file, the file named after the type: `Name_N.cs` for a generic sharing its name with a non-generic type (`ZabbixQuery_2.cs`), `Name.Part.cs` for a part of a partial type declared elsewhere (`ZabbixHostClient.MassUpdate.cs`). One cmdlet per file under `Commands/`. A small internal helper may stay beside its user. | `DesignConformanceTests.Layout` (no allow-list entries) | dotnet/runtime's source layout (`ImmutableArray_1.cs`, `String.Manipulation.cs`) |
| Nothing a build writes is committed: the JSON context, the module manifests' export lists, the MAML help, the types/format views and the reference pages are written under `obj/`/`bin/` only; docfx reads the reference pages from the modules' Release `obj/`. | CI `build` job: `git diff --exit-code` and an empty `git status --porcelain` after the build and the format check | dotnet/runtime's generated-sources check (a build that changes the tree fails) |
| Every package version is declared once, in `Directory.Packages.props`; a `PackageReference` carries only asset metadata. | NuGet (NU1008 for a `Version` on a `PackageReference`) | Learn, "Central Package Management" |
| Every package carries a readme, the project URL, Source Link, embedded untracked sources and a `.snupkg`; CI builds are deterministic (`ContinuousIntegrationBuild`). No icon exists yet. | `dotnet pack` (each `.nupkg` has `README.md`; one `.snupkg` per package) | Learn, "Package authoring best practices", "Source Link", "Symbol packages (.snupkg)" |
| The source generator targets `netstandard2.0`, references the lowest `Microsoft.CodeAnalysis.CSharp` that compiles it (4.9.2) and defines the compiler's marker types internally; build-time executables live in `tools/`. | `RefusesToShipANonPortableAnalyzer` target; the generator project | Roslyn, "Source Generators Cookbook" / analyzer packaging guidance |
| The options generator's helper types do not clash across the family: a nested options type validated in several assemblies (`ZabbixConnectionOptions`, `ZabbixTlsOptions`) has one explicit `[OptionsValidator]` in `Zabbix.Sdk`, named by every `[ValidateObjectMembers(typeof(…))]`; `InternalsVisibleTo` is limited to the pairs that compile against each other's internals. No `CS0436` suppression. | the build (`CS0436` is a warning, `-warnaserror`) | Learn, "Compile-time options validation source generation" (`ValidateObjectMembersAttribute(Type)`) |
| Style: `_camelCase` private and internal instance fields, `s_camelCase` private and internal static fields, `PascalCase` constants (dotnet/runtime's naming rules, at error); `var` preferences as dotnet/runtime writes them (explicit types), at suggestion because applying them would rewrite ~300 files. | IDE1006 at error; IDE0007/IDE0008 at suggestion | dotnet/runtime `.editorconfig`; Learn, "Code-style naming rules" |

## Consequences

- 212 source files became 554; a type is found by its name and a diff names the type it changes.
- A build leaves `git status` empty, Debug and Release; the help, the views and the reference pages are reproducible
  from source and never drift from it in review.
- `Get-Help -Online` and each reference page link to the documentation site (`reference/<module>/<command>.html`), which
  is where the pages are published; GitHub Pages must be enabled for the links to resolve outside the repository.
- The generator loads in any compiler host from Roslyn 4.9 on; it is still consumed by `ProjectReference` only.
- Namespace and file moves are breaking only where a type changed namespace, which none did; the DI extensions kept
  theirs (the root-namespace rule above).
- `InternalsVisibleTo` was measured pair by pair (each removed, the solution and both test projects rebuilt): 25 of 34
  pairs are needed and kept — `Zabbix.Abstractions` → `Zabbix.Api`, `Zabbix.Sdk`, `Zabbix.Agent`, `Zabbix.Proxy`, the
  three PowerShell modules, both test projects; `Zabbix.Api` → `Zabbix.Sdk`; `Zabbix.Sdk` → `Zabbix.Agent`,
  `Zabbix.Proxy`, `Zabbix.PowerShell.Proxy`, both test projects; `Zabbix.Agent` → `Zabbix.Proxy`,
  `Zabbix.PowerShell.Agent`, `Zabbix.Tests`; `Zabbix.Proxy` → `Zabbix.PowerShell.Proxy`, `Zabbix.Tests`;
  `Zabbix.PowerShell` → `Zabbix.PowerShell.Agent`, `Zabbix.PowerShell.Proxy`, `Zabbix.PowerShell.Tests`; each sibling
  module → `Zabbix.PowerShell.Tests`. A new pair is added with the code that needs it.
- 116 private/internal static fields were renamed to `s_camelCase` (through Roslyn's renamer: `dotnet format` cannot
  apply IDE1006).

## Amendment (2026-10-07): `InternalsVisibleTo` across packages

- The count above is from 2026-10-05. The tree now has 36 `InternalsVisibleTo` pairs: nine between NuGet packages that
  version separately (`Zabbix.Abstractions` to `Zabbix.Api`, `Zabbix.Sdk`, `Zabbix.Wire`, `Zabbix.Agent` and
  `Zabbix.Proxy`; `Zabbix.Api` to `Zabbix.Sdk`; `Zabbix.Wire` to `Zabbix.Agent` and `Zabbix.Proxy`; `Zabbix.Agent` to
  `Zabbix.Proxy`), nine from a library to a PowerShell module, two between the modules, two to build-time tools and
  fourteen to test projects.
- Microsoft's guidance: internal members seen through `InternalsVisibleTo` "can introduce breaking changes"
  ([version and update considerations](https://learn.microsoft.com/dotnet/csharp/whats-new/version-update-considerations#introducing-breaking-changes-in-your-libraries)),
  so an internal one package uses from another is public surface without the public surface's review or package
  validation. The rule is: no `InternalsVisibleTo` between packages that version separately; what two packages share is
  public and reviewed, or shared source compiled into each. Pairs to test projects, to build-time tools, and within one
  module folder stay.
- The nine library pairs are a divergence until they are closed
  ([decisions register](../design/decisions.md#documented-divergences-to-be-closed)); until then the family ships at
  one version, pinned exactly at pack time.
- The pin is the `ZabbixExactFamilyVersions` target in `Directory.Build.targets`: after NuGet's
  `_GetProjectReferenceVersions` it rewrites each `ProjectReference`'s version as an exact range, so a packed nuspec
  reads `<dependency id="Zabbix.Agent" version="[1.0.0]" />`, the shape Roslyn's packages use for
  `Microsoft.CodeAnalysis.Common`. It applies to every family dependency, not only the nine pairs, so the family
  resolves as one set.

## Amendment (2026-10-12): the package ids

The package ids carry `Community.` before the assembly name ([0002](0002-package-ids.md), amendment), so the pin above
now reads `<dependency id="Community.Zabbix.Agent" version="[1.0.0]" />`. The target is unchanged: NuGet
names a family dependency by the referenced project's `PackageId`.

## Amendment (2026-10-12): the icon

The packaging row above says "No icon exists yet". Every package now carries `assets/icon.png` (`PackageIcon` in
`Directory.Build.props`): a white "C" ring with a green node on a navy square, the project's own picture. It is not the
Zabbix logo and has no "Z"; Zabbix's trademark policy treats a package icon as packaging, which needs a licence for the
logo ([decisions register](../design/decisions.md#zabbixs-trademark-and-licences)).

## Amendment (2026-10-30): the cmdlet reference is committed

One kind of build output is committed: the cmdlet reference, one Markdown page per command under
`docs/reference/<module>/`, so it reads on GitHub as well as on the documentation site, as PowerShell module
repositories commonly keep their help. The build writes it (HelpGen's `ZabbixBuildHelpTask`), each page only when its
text changes, and deletes the page of a command that is gone. The rule that a build leaves the tree clean still holds,
so a change that alters a command's help without committing its regenerated page fails CI. Every other generated file
stays under `obj/` and `bin/`.
