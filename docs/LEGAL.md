<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# License, trademark, and warranty

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
Provided "as is", without warranty of any kind: use at your own risk.

## License

This project is licensed under the **[Mozilla Public License Version 2.0 (MPL-2.0)](../LICENSE)**.

In plain English:

- You may **use, modify, distribute, and embed** this software in larger commercial or proprietary products, under the one condition below.
- If you **modify any of the source files** in this repository and distribute those modified files (in source or compiled form), your modified versions of those files must be made available under MPL-2.0. Running the software as a service is not distribution and triggers nothing.
- Files you add yourself, or proprietary code you link against MPL-2.0 files, are **not** required to be open source.

This is the same license used by Mozilla Firefox, LibreOffice, and HashiCorp Terraform/Consul/Vault (until 2023). It is intended to keep improvements to *this codebase* in the open while letting Zabbix, Zabbix partners, and anyone else freely embed the SDK in their commercial products.

Contributions are welcomed under the same license. See [CONTRIBUTING.md](../CONTRIBUTING.md) for the contribution process, and the [Code of Conduct](../CODE_OF_CONDUCT.md) for community expectations.

**Security:** please report vulnerabilities privately — see [SECURITY.md](../SECURITY.md). Do not open a public issue for a security problem.

## Trademark notice

This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.

This is an independent, community-maintained project. References to "Zabbix" in this project, its package names,
namespaces, and source code are made under nominative fair use to identify the software with which this client is
designed to interoperate. All rights to the Zabbix name and logo remain with their owner. The Zabbix logo appears in the
README unaltered and standalone, as the policy allows for non-commercial use, and never on a package or module; the
package icon is the project's own, not the Zabbix logo. The MPL-2.0 license under which this project is distributed grants no rights in
any trademarks; see Section 2.3 of [LICENSE](../LICENSE). Zabbix's trademark policy is at
<https://www.zabbix.com/trademark>, and [docs/design/decisions.md](design/decisions.md#zabbixs-trademark-and-licences)
records how the project follows it. [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) credits the Zabbix sources the API
model is extracted from and reproduces the licences of the components the PowerShell modules redistribute.

## Disclaimer of warranty

> [!CAUTION]
> **No warranty.** This software is provided "AS IS", without warranty of any kind, either expressed, implied, or statutory. The author **cannot be held liable** for any damage that may occur while using this software.

This is a **community-supported, single-developer project**. There is no service-level agreement, no guaranteed response time, no obligation to fix bugs, and no liability for any operational impact resulting from the use of this software. Issues and pull requests are welcomed and reviewed on a best-effort basis.

The author cannot be held liable for any damage that may occur while using this software, including but not limited to:

- Corrupted, deleted, or unintentionally modified Zabbix configuration (hosts, templates, items, triggers, dashboards, ACLs).
- Lost or duplicated monitoring data, history, trends, or audit log records.
- Locked-out user accounts, revoked tokens, or unintended permission changes.
- Accidental destructive operations against monitoring infrastructure (e.g., mass deletes, broken pipelines, misconfigured maintenance windows that suppress production alerts).
- Exposure of API tokens, passwords, or TLS keys handled by the SDK or your scripts that use it.
- Any downstream consequence — incident response failure, missed pages, financial loss — resulting from defects in this software.

**You are responsible for:**

- Testing all destructive or write operations (`Remove-*`, `Clear-*`, `Set-*`, mass updates, configuration imports, ACL changes) in a non-production environment first.
- Validating the SDK's behavior against your specific Zabbix server version, configuration, and integrations before relying on it in production.
- Securing API tokens, credentials, and TLS keys handled by the SDK or by code that uses it.
- Reviewing any changes to monitoring configuration before applying them to production.
- Backing up your Zabbix configuration database before running large-scale or experimental operations.

### Legal terms (MPL-2.0 §6 and §7)

The following text is reproduced **verbatim** from Sections 6 and 7 of the [Mozilla Public License Version 2.0](../LICENSE), the license under which this software is distributed:

> **6. Disclaimer of warranty**
>
> Covered Software is provided under this License on an "as is" basis, without warranty of any kind, either expressed, implied, or statutory, including, without limitation, warranties that the Covered Software is free of defects, merchantable, fit for a particular purpose or non-infringing. The entire risk as to the quality and performance of the Covered Software is with You. Should any Covered Software prove defective in any respect, You (not any Contributor) assume the cost of any necessary servicing, repair, or correction. This disclaimer of warranty constitutes an essential part of this License. No use of any Covered Software is authorized under this License except under this disclaimer.
>
> **7. Limitation of Liability**
>
> Under no circumstances and under no legal theory, whether tort (including negligence), contract, or otherwise, shall any Contributor, or anyone who distributes Covered Software as permitted above, be liable to You for any direct, indirect, special, incidental, or consequential damages of any character including, without limitation, damages for lost profits, loss of goodwill, work stoppage, computer failure or malfunction, or any and all other commercial damages or losses, even if such party shall have been informed of the possibility of such damages. This limitation of liability shall not apply to liability for death or personal injury resulting from such party's negligence to the extent applicable law prohibits such limitation. Some jurisdictions do not allow the exclusion or limitation of incidental or consequential damages, so this exclusion and limitation may not apply to You.

The [LICENSE](../LICENSE) file is the legally authoritative source for these terms; the plain-English summaries above are provided for convenience only.
