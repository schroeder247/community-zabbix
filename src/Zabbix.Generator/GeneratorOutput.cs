// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>The pipeline's second cached step: the files to add and the diagnostics to report, all values.</summary>
/// <param name="Files">The files.</param>
/// <param name="Diagnostics">The diagnostics, located.</param>
internal sealed record GeneratorOutput(EquatableArray<GeneratedSource> Files, EquatableArray<LocatedDiagnostic> Diagnostics);
