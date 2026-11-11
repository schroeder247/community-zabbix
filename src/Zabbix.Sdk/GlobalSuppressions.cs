// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;

// Every async member takes an optional CancellationToken last, on every overload (ADR 0013, section 6), so the overloads of
// one name each have an optional parameter (RS0026), and a shorter one has it beside a longer one (RS0027). The analyzers'
// alternative, a second overload without the token for each shape, doubled the surface. DesignConformanceTests holds what
// the two rules guarded: an async member's token is last and optional, and no other parameter of an overload is optional.
[assembly: SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Scope = "module",
    Justification = "ADR 0013, section 6: every async member takes an optional CancellationToken last, on every overload.")]
[assembly: SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Scope = "module",
    Justification = "ADR 0013, section 6: every async member takes an optional CancellationToken last, on every overload.")]
