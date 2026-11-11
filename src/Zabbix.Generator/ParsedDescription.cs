// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>
/// The pipeline's first cached step: a description file read once (<see cref="ApiDescription.Read(string, List{EmitProblem})"/>),
/// with what reading it refused. Equal when the path, the part and the text are: the description is a pure function of
/// the text, so comparing the text (one ordinal compare) stands for comparing the model, and an unchanged file skips
/// every step after this one.
/// </summary>
/// <param name="Path">The file's path, for the diagnostics.</param>
/// <param name="Part">What is emitted from it (<c>Models</c>, <c>Clients</c>, <c>Cmdlets</c>).</param>
/// <param name="Text">The file's text, for the diagnostics' lines.</param>
/// <param name="Api">The description, or <see langword="null"/> when it could not be read.</param>
/// <param name="Problems">What reading it refused.</param>
internal sealed record ParsedDescription(string Path, string Part, string Text, ApiDescription? Api, EquatableArray<EmitProblem> Problems)
{
    /// <inheritdoc/>
    public bool Equals(ParsedDescription? other)
        => other is not null && string.Equals(Path, other.Path, StringComparison.Ordinal) && string.Equals(Part, other.Part, StringComparison.Ordinal)
            && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Path) ^ Text.Length;
}
