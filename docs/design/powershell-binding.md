<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# PowerShell binding: the native seams for a typed SDK module

The `Community.Zabbix` PowerShell module wraps a typed SDK in which every request and parameters type is a C# `record` with
`init` or settable properties and System.Text.Json source-generated metadata (wire-name remap, converters, `IReadOnlyList<T>`
nested lists, an `AdditionalFields` extension bag), about 700 records in all. A shell user writes hashtables,
`[pscustomobject]`s and arrays of them. This document surveys the seams PowerShell itself offers for turning one into
the other and back, with a verdict per seam, and closes with [what the module does](#what-the-module-does).

PowerShell SDK citations are PowerShell 7.6 (`view=powershellsdk-7.6.0` / `view=powershell-7.6`). Claims confirmed by
running code on pwsh 7.6 are marked **measured**; claims not backed by a Learn page or a measurement are marked
**unverified**. Where this document disagrees with the PowerShell cmdlet development guidelines or the shape of the
PowerShell team's own cmdlets, the guidelines win and this document is corrected ([the precedence
rule](decisions.md#precedence)).

---

## 1. `PSTypeConverter` / `Update-TypeData -TypeConverter` / `TypeConverterAttribute`

**APIs**
- `PSTypeConverter` (abstract, `System.Management.Automation`): `CanConvertFrom`/`ConvertFrom`/`CanConvertTo`/
  `ConvertTo`, all taking `(object value, Type type, IFormatProvider, bool ignoreErrors)`. It is associated with a
  target type by `Update-TypeData -TypeName <FullName> -TypeConverter <Type>`, by a `<TypeConverter>` entry in a
  `types.ps1xml` file, or by `[TypeConverter(typeof(MyConverter))]` on the target type.
  https://learn.microsoft.com/dotnet/api/system.management.automation.pstypeconverter?view=powershellsdk-7.6.0
- *"Unlike System.ComponentModel.TypeConverter, PSTypeConverter can be applied to a family of types (like all types
  derived from System.Enum)."* The family behaviour is the engine's own; for user types the association is one
  registration per concrete type. There is no wildcard `-TypeName`.
  https://learn.microsoft.com/powershell/scripting/developer/ets/typeconverters?view=powershell-7.6
- `LanguagePrimitives.ConvertTo(object, Type[, IFormatProvider])`: language-standard conversions first (they cannot be
  overridden; they include `IDictionary` → `Hashtable` and enumerable → array), then `TypeConverter` and
  `PSTypeConverter`, then `Parse` methods, single-argument constructors, cast operators and `IConvertible`.
  https://learn.microsoft.com/dotnet/api/system.management.automation.languageprimitives.convertto?view=powershellsdk-7.6.0

**Measured behaviour**

1. A hashtable binds to a record with `init`-only properties **with no converter at all**, for scalar properties:
   `[MyRecord]@{...}`, `LanguagePrimitives.ConvertTo($hashtable, [MyRecord])` and a `[MyRecord]$Param` parameter all
   succeed. The engine recurses into a nested collection property typed `T[]` or `List<T>` (an `object[]` of
   hashtables becomes an array of records). **measured**
2. It **fails on an `IReadOnlyList<T>` property** with `PSInvalidCastException: Cannot convert the "System.Object[]"
   value ... to type "System.Collections.Generic.IReadOnlyList`1[...]"`. The engine does not try to build a `List<T>`
   for an interface-typed collection. `IReadOnlyList<T>` is the shape the SDK's generator emits for every nested list,
   so the native path covers none of the SDK's nested lists. **measured**
3. A `PSTypeConverter` registered **on the nested element type** does not help: the binder fails at the collection
   before it asks for any element conversion. **measured**
4. A `PSTypeConverter` registered **on the outer type** does work, for casts and for parameter binding, because the
   converter's own `ConvertFrom` then owns the whole object graph and can build the `IReadOnlyList<T>` itself.
   **measured**
5. One shared converter class works for every record type. A record with no positional parameter list has a public
   parameterless constructor, so construction needs no special case. Two traps: a singleton hashtable given for a
   collection property is itself `IEnumerable` and is misread as a sequence of `DictionaryEntry` unless the converter
   tests for `IDictionary` first; and `LanguagePrimitives.ConvertTo(pscustomobject, typeof(Hashtable))` throws
   `PSInvalidCastException`, because the `IDictionary` → `Hashtable` conversion fires only for a source that already
   implements `IDictionary`, so a `[pscustomobject]` has to be flattened from its own properties. **measured**

**Verdict: fits, with one adaptation.** `PSTypeConverter` is the native seam for "many shapes for one type", and one
class serves every record: 700 records means 700 registrations of one class, not 700 converters. The converter, not the
engine, must own recursion into `IReadOnlyList<T>`. Changing the generator to emit `List<T>` or `T[]` for PowerShell's
sake would invert the layering and give up the read-only contract.

---

## 2. `IDynamicParameters` + `RuntimeDefinedParameterDictionary`

**APIs**
- `IDynamicParameters`: one method, `object? GetDynamicParameters()`.
  https://learn.microsoft.com/dotnet/api/system.management.automation.idynamicparameters?view=powershellsdk-7.6.0
- Timing, from Learn: *"This method will be called after all formal (command-line) parameters are set, but before
  `BeginProcessing()` is called and before any incoming pipeline objects are read. Therefore, parameters which allow
  input from the pipeline may not be set at the time this method is called, even if the parameters are mandatory."*
  https://learn.microsoft.com/dotnet/api/system.management.automation.idynamicparameters.getdynamicparameters?view=powershellsdk-7.6.0
- `RuntimeDefinedParameterDictionary : Dictionary<string, RuntimeDefinedParameter>` is the usual return type.
  https://learn.microsoft.com/dotnet/api/system.management.automation.runtimedefinedparameterdictionary?view=powershellsdk-7.6.0
- The canonical example is "a parameter that appears depending on another parameter's value", which is the shape of a
  `-Type`-dependent `details` block.
  https://learn.microsoft.com/powershell/scripting/developer/cmdlet/cmdlet-dynamic-parameters?view=powershell-7.6

**Consequences**
- The dynamic set can depend only on parameters bound from the command line, never on pipeline input (documented
  timing).
- Tab completion of a dynamic parameter after `-Type Http` works in practice, because the formal parameters are bound
  first. **unverified** here.
- `Get-Help` cannot show a dynamic parameter unless it is asked in a context where the revealing parameter's value is
  known, so discoverability and help quality drop relative to a static parameter set. **unverified**, and not
  contradicted by Learn.
- `GetDynamicParameters()` runs once per invocation, not per pipeline item; its cost is negligible next to an API call.

**Verdict: partial fit.** It is the sanctioned tool for "parameters vary by `-Type`", but it cannot follow pipeline
input, it weakens help, and it re-creates per-type parameter metadata that must still come from the records. Use it only
for narrow, high-value cases; first check whether a record-typed parameter, bound from a hashtable through §1's
converter, already is the `details` input.

---

## 3. `PSPropertyAdapter`: `AdditionalFields` as first-class properties

**APIs**
- `PSPropertyAdapter` (abstract, public): *"User-defined property adapter... used to expose a simplified version of the
  type adapter API."* Members include `GetProperty(object, string)`, `GetProperties(object)`,
  `GetPropertyTypeName(PSAdaptedProperty)`, `GetTypeNameHierarchy(object)`.
  https://learn.microsoft.com/dotnet/api/system.management.automation.pspropertyadapter?view=powershellsdk-7.6.0
- Precedent: `Microsoft.PowerShell.Cim.CimInstanceAdapter : PSPropertyAdapter` is how `Get-CimInstance` exposes a CIM
  instance's dynamic properties as `$ci.SomeProperty` without static .NET properties.
- Registration: `Update-TypeData -TypeAdapter <Type>` or a `<TypeAdapter>` entry in `types.ps1xml`.
  https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/update-typedata?view=powershell-7.6

**Verdict: fits.** One adapter, registered for every read record with an `AdditionalFields` bag, surfaces each key of
the bag as an adapted property, so `$result.newfield` works for a field the model does not carry. The adapter must
return the record's CLR properties first and the bag's keys after them, or `Get-Member` and `Format-List` lose the
modelled properties.

One measured load-order fact: a `<TypeAdapter>` resolves its type name against already-loaded assemblies when the types
file is processed (a `<TypeConverter>` resolves lazily). The manifest's `RequiredAssemblies` loads before
`TypesToProcess`, so the assembly that holds the adapted record types must be listed there; without it every adapter
registration fails on a clean `Import-Module` with "The adapted type … is not valid." **measured**

---

## 4. ETS type data: `AliasProperty`, `ScriptProperty`, `DefaultDisplayPropertySet`, views

**APIs**
- `AliasProperty` (a new name for an existing property) and `ScriptProperty` (computed, `$this` in scope), declared in
  `types.ps1xml` or by `Update-TypeData -MemberType`.
  https://learn.microsoft.com/powershell/scripting/developer/cmdlet/extending-properties-for-objects?view=powershell-7.6
  https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_types.ps1xml?view=powershell-7.6
- `-DefaultDisplayPropertySet` on `Update-TypeData`, and table/list/wide views in a `format.ps1xml`, shape default
  output.
  https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/update-typedata?view=powershell-7.6
- A `types.ps1xml` listed in the manifest's `TypesToProcess` is the packaging that "just works" on `Import-Module`.
- Security: *"Restricted endpoints, such as JEA, must not expose the `Update-TypeData` command... ScriptProperty members
  might be evaluated in FullLanguage mode, even when the session is configured to use a more restrictive language
  mode."* A module that may run in a constrained endpoint should not ship `ScriptProperty` members.

**Verdict: fits, for output.** Type data augments objects that already exist; it does not construct records from
hashtables (that is §1's job). Generated views replace hand-written ones.

---

## 5. `ArgumentTransformationAttribute` versus `PSTypeConverter`

**APIs**
- `ArgumentTransformationAttribute` (abstract): override `Transform(EngineIntrinsics, object)`. *"The transformation
  might change the object, convert the type, or even load a file or AD object based on the name... If multiple
  transformations are defined on a parameter, they will be invoked in series."*
  https://learn.microsoft.com/dotnet/api/system.management.automation.argumenttransformationattribute?view=powershellsdk-7.6.0
- Precedent: DSC's `ArgumentToConfigurationDataTransformationAttribute` turns a file path into a hashtable for
  `-ConfigurationData` and passes anything else through.
  https://learn.microsoft.com/dotnet/api/microsoft.powershell.desiredstateconfiguration.argumenttoconfigurationdatatransformationattribute?view=powershellsdk-7.6.0

**Where they differ.** A transformation attribute is scoped to one declared parameter on one command and runs before
that parameter's validation. A `PSTypeConverter` is scoped to a target type, everywhere the engine converts to it:
casts, and binding on any command that takes the type.

**Verdict: `PSTypeConverter` for record coercion.** Coercion is a property of the type, so it is declared once per type
(by the generator), not once per parameter on every command that takes the type. A transformation attribute stays the
tool for transforms that are genuinely about one parameter, such as a connection given by name or object, or the flat
`@{ env = 'x' }` tag shorthand.

---

## 6. `IValidateSetValuesGenerator`, `ArgumentCompletions`, `IArgumentCompleterFactory`

**APIs**
- `IValidateSetValuesGenerator` (PowerShell 6.0+): `string[] GetValidValues()`, passed to
  `ValidateSetAttribute(Type)`; values are computed at validation and completion time.
  `CachedValidValuesGeneratorBase` adds caching. Microsoft precedent: `ValidateCultureNamesGenerator`.
  https://learn.microsoft.com/dotnet/api/system.management.automation.ivalidatesetvaluesgenerator?view=powershellsdk-7.6.0
  https://learn.microsoft.com/dotnet/api/system.management.automation.cachedvalidvaluesgeneratorbase?view=powershellsdk-7.6.0
- `ArgumentCompletionsAttribute`: static completion, explicitly not validated.
  https://learn.microsoft.com/dotnet/api/system.management.automation.argumentcompletionsattribute?view=powershellsdk-7.6.0
- `IArgumentCompleterFactory` + `ArgumentCompleterFactoryAttribute` (7.2+): one parameterized completer class serving
  many parameters.
  https://learn.microsoft.com/dotnet/api/system.management.automation.iargumentcompleterfactory?view=powershellsdk-7.6.0

**Verdict, by use case**
- Wire enums (item type, interface type): a generated `enum` parameter; PowerShell validates and completes enum
  parameters natively.
- Names that exist only on the connected server (hosts, groups, templates): `IValidateSetValuesGenerator` (with
  `CachedValidValuesGeneratorBase`) fits when the set must be enforced; a completer factory fits when the value is only
  suggested. The module takes the second: `ZabbixCompleterAttribute<TEntity>`, an `ArgumentCompleterFactoryAttribute`,
  completes names against the connected server, and the server itself refuses a name that does not exist.
- Suggested but open values: `ArgumentCompletions` or a completer factory.

---

## 7. `-Filter`: a scriptblock or a hashtable

**APIs**
- `ScriptBlock.Ast` exposes a parsed `ScriptBlockAst`; the AST is public and walkable.
  https://learn.microsoft.com/dotnet/api/system.management.automation.language.scriptblockast.getscriptblock?view=powershellsdk-7.6.0
- `Where-Object { $_.Prop -eq 'x' }` is the PowerShell mental model of a filter.
  https://learn.microsoft.com/powershell/module/microsoft.powershell.core/where-object?view=powershell-7.6
- Precedent for a friendly server-side filter: the ActiveDirectory module's `-Filter <String>` ("the PowerShell
  Expression Language", parsed by the module) beside the raw `-LDAPFilter`.
  https://learn.microsoft.com/powershell/module/activedirectory/get-aduser?view=windowsserver2025-ps

**Comparison.** A hashtable maps one to one onto Zabbix's `filter`/`search` objects, which are themselves key →
value-or-array maps; it needs no parsing and loses nothing. A scriptblock reads better but must be walked, a recognized
subset (`$_.Prop -eq/-like/-in value`, `-and`) translated, and everything else refused, because a silent fall-back to
client-side filtering defeats server-side selection at scale. Zabbix's filter language is key → value matching, not a
boolean language, so most of what a scriptblock can say has no wire form.

**Verdict: keep the hashtable.** A scriptblock compiler is rejected. If one is ever wanted, it should recognize only the
subset above and fail loudly on anything else.

---

## 8. PowerShell's JSON and `PSObject` → `Hashtable`

- `ConvertTo-Json` is implemented on Newtonsoft Json.NET, a different JSON stack from the SDK's source-generated
  System.Text.Json contract. It writes C# property names, ignores `JsonPropertyName`, cannot see the records'
  converters (flags, ids, `ZabbixOutput<TRecord>`, `ZabbixInterval`), and `ConvertFrom-Json` turns an ISO-looking string into a `DateTime` the
  server never sent. A typed record nested in a hashtable and serialized through it is written with the wrong names
  (measured: `CreateMissing` instead of `createMissing`, which the wire contract then drops).
  https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/convertto-json?view=powershell-7.6
- `Microsoft.PowerShell.Commands.JsonObject.ConvertToJson(object, ref ConvertToJsonContext)` is public, and so is the
  nested `ConvertToJsonContext` struct (`IsNestedPublic` is `True` on pwsh 7.6; `IsPublic` is always `False` for a
  nested type, which is easy to misread). It is callable, but it is the same Newtonsoft stack. **measured**
- There is no built-in "flatten any `PSObject` into a `Hashtable`" (§1, item 5); a module flattens
  `$obj.PSObject.Properties` itself.

**Verdict: PowerShell's JSON must never produce wire JSON.** The wire is the SDK's System.Text.Json contract, reached
from the module's C# code. `ConvertTo-Json` is for display only.

---

## What the module does

The module implements the verdicts above:

- **Input.** One `ZabbixRecordConverter : PSTypeConverter` is the `<TypeConverter>` of every request, parameters and
  nested-request record, registered by a generated `Community.Zabbix.types.ps1xml` (emitted by `Zabbix.Generator`'s
  `ZabbixTypesEmitter`) that the manifest loads through `TypesToProcess`. The records are `Zabbix.Api`'s generated
  output, so the registration lives in the module's types file, not in a `[TypeConverter]` attribute on the records.
  PowerShell's own parameter binder calls the converter for `-Filter`, a record-typed parameter, a nested record list
  and a `[Community.Zabbix.ZabbixHostCreateRequest]@{…}` cast; no cmdlet calls a binder. `-Filter` is the entity's typed
  `*GetParameters` record, to which a hashtable binds through the converter.
- **Inside the converter.** `ZabbixJsonBinder` remaps keys to wire names recursively, by C# name or `JsonPropertyName`,
  refuses a key the contract does not know with `ArgumentException`, and deserializes once through the SDK's JSON
  contract (`ZabbixJson.Options`, its source-generated context), which builds every `IReadOnlyList<T>` itself. Loose
  shell values (hashtable, `[ordered]`, `[pscustomobject]`, arrays of them, at any depth) are walked into
  System.Text.Json by `ZabbixJsonShape`; a typed record nested in a hashtable is written through its own `JsonTypeInfo`.
  PowerShell's `JsonObject` is not used.
- **Per-parameter shapes.** The flat tag hashtable (`@{ env = 'x'; role = 'y' }`, one tag per key) is pre-shaped by a
  transformation attribute and then bound by the converter. `-AdditionalProperty` merges last, over the named parameters,
  because a merge onto a built request is not a value conversion; two `-AdditionalProperty` keys that resolve to one wire field
  with different values are refused.
- **Untyped JSON fields.** A field whose shape Zabbix does not fix (schema kind `json`: `New-ZbxModule -Config`,
  `New-ZbxHttpTestStep -Posts`) is a `JsonElement` parameter, to which PowerShell has
  no conversion. The generator puts `ZabbixJsonAttribute`, a transformation, on every such parameter: a hashtable, an
  `[ordered]` dictionary, a `[pscustomobject]` or an array of them is walked by `ZabbixJsonShape`, a string is read as
  JSON text, a `JsonElement` passes through. A transformation, not a `PSTypeConverter` for `JsonElement`: the target is
  a BCL type, and a converter registered for it would change every module's conversions to it in the session.
- **Server addresses.** Every `-Server` (and `Connect-ZbxServer -Trapper`) is `DnsEndPoint[]`, the nodes of one
  HA cluster tried in order: `ZabbixEndPointAttribute` reads each string through `ZabbixEndPoint.Parse`, so `host`,
  `host:port`, an IPv6 address and `[ipv6]:port` need no `-Port`. `Start-ZbxActiveAgent -Server` is the `ServerActive`
  text: one string is that grammar (`'a;b'` fails over, `'a,b'` sends to two destinations), and a list is one HA
  cluster, as everywhere else (`ZabbixServerActiveAttribute`). `Get-ZbxAgentItem -Agent` is an agent's `DnsEndPoint`,
  whose port is 10050 when the text names none.
- **Parameter names and aliases.** A generated parameter is named by the words of its wire field
  (`schema/words.json`); no other spelling is kept as an alias, because an alias is permanent API once released. An alias stays only where it binds what a name cannot:
  `-Id` on every `-InputObject`; a piped object's own id property on a reference filter or an owner (`-HostId` on
  `Get-ZbxItem -Host`, `-TemplateId` on a `New-` that creates on a host or template), which is how
  `Get-ZbxHost | Get-ZbxItem` binds by property name; the spelling of a name that would hide a `PSCmdlet` member
  (`-Host` for `-HostName`, `-HostReference`; the property's name is `cmdlets.objects.<object>.unhide` in
  `schema/overlay.json`, else the name followed by `Value`); the one name host-like objects share for a field (`-Name`,
  `-DisplayName`, for the objects `cmdlets.objects.<object>.names` lists); and PowerShell's own path and computer aliases (`-PSPath`, `-LP`, `-ComputerName`).
  `ModuleShapeTests.Every_parameter_alias_binds_what_a_name_cannot` holds the rule.
  Above the words, the cmdlet guidelines decide the name ([Strongly Encouraged Development
  Guidelines](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/strongly-encouraged-development-guidelines),
  SD03 and SC01; [standard parameter names](https://learn.microsoft.com/powershell/scripting/developer/cmdlet/standard-cmdlet-parameter-names-and-types)):
  where a standard name fits (`-Name`, `-Id`, `-InputObject`, `-PassThru`, `-Force`, `-Credential`, `-Path` and
  `-LiteralPath`, `-ComputerName`), the parameter takes the standard name and a more specific name is its alias;
  parameter names are singular, also for arrays; one concept takes one name and one type on every command of the three
  modules; and a file is `-Path` (wildcards, alias `-PSPath`) with a separate `-LiteralPath`, at position 0 on an
  `Import-` command as on `Import-Csv`. A machine to reach keeps its Zabbix name with `-ComputerName` as its alias
  (`Get-ZbxAgentItem -Agent`), as the PowerShell team's own `Test-Connection -TargetName` keeps `-ComputerName` as an
  alias. The
  commands that do not meet these rules yet are listed under [the documented
  divergences](decisions.md#documented-divergences-to-be-closed).
- **Output.** One `ZabbixAdditionalFieldsAdapter : PSPropertyAdapter` is the `<TypeAdapter>` of every read record, so
  an unmodelled wire field reads as `$obj.<field>`; it returns the CLR properties first, then the bag's keys. The
  manifest lists `Community.Zabbix.Api.dll` in `RequiredAssemblies` so the adapter registrations resolve. A generated
  `Community.Zabbix.format.ps1xml` gives every read record a table, list and wide view; the one `ScriptProperty`
  member is a user macro's bare `Name`.
- **Not used.** Hand-written coercion, `LanguagePrimitives.ConvertTo` as the binder, PowerShell's JSON as the wire
  contract, `ArgumentTransformationAttribute` as the general seam, module-wide `IDynamicParameters`, and a scriptblock
  `-Filter`.
