// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Binds a web scenario's <c>-Step</c> to the step records of the request it goes into: <typeparamref name="TStep"/>
/// objects pass through, and a hashtable or <c>[pscustomobject]</c> (what <c>New-ZbxHttpTestStep</c> writes, or one
/// written by hand) is built through the step's own JSON contract, its keys matched by property name or by the API's
/// field name (<see cref="ZabbixJsonBinder"/>). <c>httptest.create</c> and <c>httptest.update</c> take different steps
/// (an update's step may name <c>httpstepid</c> and need not repeat <c>name</c>, <c>no</c> or <c>url</c>; a create's
/// may not name it and must: <c>CHttpTest.php</c> 8.0:421), so the parameter names its record.
/// </summary>
/// <remarks>
/// With <see cref="NumberByPosition"/>, a step that names no number (<c>no</c>) is numbered by its position, 1 for the
/// first: <c>httptest.create</c> requires every step's number (<c>CHttpTest.php</c> 7.0:304, 8.0:394). The caller's
/// object is copied, never changed. A step that does not shape a <typeparamref name="TStep"/> is a binding error.
/// </remarks>
/// <typeparam name="TStep">The request's step record: <c>ZabbixHttpTestStepRequest</c> or <c>ZabbixHttpTestUpdateStepRequest</c>.</typeparam>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixHttpTestStepsAttribute<TStep> : ArgumentTransformationAttribute
    where TStep : notnull
{
    /// <summary>Whether a step that names no number is numbered by its position.</summary>
    public bool NumberByPosition { get; set; }

    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = ZabbixJsonBinder.Unwrap(inputData);
        if (input is null) return null;
        IEnumerable<object?> items = input is IEnumerable many and not string and not IDictionary ? many.Cast<object?>() : [input];
        JsonTypeInfo<TStep> contract = ZabbixContracts.Of<TStep>();
        var steps = new List<TStep>();
        foreach (object? item in items)
        {
            object? step = NumberByPosition ? Numbered(item, steps.Count + 1) : item;
            try
            {
                steps.Add(ZabbixJsonBinder.Bind(step, contract));
            }
            catch (Exception failure) when (failure is ArgumentException or JsonException or NotSupportedException or InvalidOperationException)
            {
                throw new ArgumentTransformationMetadataException(string.Create(CultureInfo.InvariantCulture,
                    $"'{item}' is not a web scenario step: give a {typeof(TStep).Name}, New-ZbxHttpTestStep's output or a hashtable of its fields."), failure);
            }
        }
        return steps.ToArray();
    }

    // The step numbered `no` when it is a hashtable or a custom object that names no `no`; anything else as given.
    private static object? Numbered(object? item, int no)
    {
        object? value = item is PSObject { BaseObject: not PSCustomObject } wrapped ? wrapped.BaseObject : item;
        switch (value)
        {
            case IDictionary table when !table.Keys.OfType<string>().Any(key => key.Equals("no", StringComparison.OrdinalIgnoreCase)):
                return new Hashtable(table, StringComparer.OrdinalIgnoreCase) { ["no"] = no };
            case PSObject custom when custom.Properties["no"] is null:
                PSObject numbered = custom.Copy();
                numbered.Properties.Add(new PSNoteProperty("no", no));
                return numbered;
            default:
                return item;
        }
    }
}
