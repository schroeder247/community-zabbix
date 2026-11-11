// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>What an emitter produced from a description: the files, and what it refused to emit. Equal by value, so the
/// incremental pipeline caches it.</summary>
/// <param name="Files">The files.</param>
/// <param name="Problems">What it left out, each with its diagnostic.</param>
internal sealed record EmitResult(EquatableArray<GeneratedSource> Files, EquatableArray<EmitProblem> Problems);
