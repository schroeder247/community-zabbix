; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
ZBX0001 | Zabbix.Generator | Error | The API description could not be read
ZBX0002 | Zabbix.Generator | Error | A kind the generator does not know
ZBX0003 | Zabbix.Generator | Error | A kind names a record or enum the description does not declare
ZBX0004 | Zabbix.Generator | Error | Two fields of a record would get one C# name
ZBX0005 | Zabbix.Generator | Error | Two methods of a client would get one C# name
ZBX0006 | Zabbix.Generator | Warning | A cmdlet, parameter or nested-record builder the cmdlet generator could not emit
ZBX0008 | Zabbix.Generator | Error | An entry of the description is malformed or declared twice
