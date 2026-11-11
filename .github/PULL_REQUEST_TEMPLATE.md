<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
## Summary

<!-- What changes and why. Link the issue it closes. -->

## Checklist

- [ ] `dotnet build Zabbix.slnx -warnaserror`, `dotnet format Zabbix.slnx --verify-no-changes` and `dotnet test --solution Zabbix.slnx` pass.
- [ ] A behaviour change has a test, and a user-visible change has a line under `## [Unreleased]` in CHANGELOG.md.
- [ ] No secrets, tokens or personal data in code, tests or docs.
