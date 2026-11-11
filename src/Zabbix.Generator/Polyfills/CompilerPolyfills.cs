// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
// The generator targets netstandard2.0, the Roslyn convention for an analyzer (it loads in every compiler host); the
// compiler features it is written with need these marker types, which netstandard2.0 does not have. Defined here,
// internal, never a third-party polyfill package (the compiler binds to them by name).
#pragma warning disable CA1019, CA1813, IDE0130 // the BCL's own shapes, in the BCL's own namespaces

namespace System.Runtime.CompilerServices
{
    /// <summary>The marker <c>init</c> accessors and records need.</summary>
    internal static class IsExternalInit;

    /// <summary>The marker that lets a collection expression build a type through a static factory.</summary>
    /// <param name="builderType">The type that holds the factory.</param>
    /// <param name="methodName">The factory, taking a <see cref="ReadOnlySpan{T}"/>.</param>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
    internal sealed class CollectionBuilderAttribute(Type builderType, string methodName) : Attribute
    {
        /// <summary>The type that holds the factory.</summary>
        public Type BuilderType { get; } = builderType;

        /// <summary>The factory's name.</summary>
        public string MethodName { get; } = methodName;
    }
}

namespace System
{
    /// <summary>An index from the start or the end of a sequence: the BCL type <c>^1</c> and ranges compile to.</summary>
    internal readonly struct Index
    {
        private readonly int _value;

        /// <summary>An index <paramref name="value"/> from the start, or from the end when <paramref name="fromEnd"/>.</summary>
        /// <param name="value">The distance.</param>
        /// <param name="fromEnd">Whether it counts from the end.</param>
        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "an index is not negative");
            _value = fromEnd ? ~value : value;
        }

        /// <summary>The first element.</summary>
        public static Index Start => new(0);

        /// <summary>One past the last element.</summary>
        public static Index End => new(0, fromEnd: true);

        /// <summary>The distance.</summary>
        public int Value => _value < 0 ? ~_value : _value;

        /// <summary>Whether it counts from the end.</summary>
        public bool IsFromEnd => _value < 0;

        /// <summary>An index from the start.</summary>
        /// <param name="value">The distance.</param>
        public static Index FromStart(int value) => new(value);

        /// <summary>An index from the end.</summary>
        /// <param name="value">The distance.</param>
        public static Index FromEnd(int value) => new(value, fromEnd: true);

        /// <summary>The offset in a sequence of <paramref name="length"/>.</summary>
        /// <param name="length">The sequence's length.</param>
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;

        /// <summary>An index from the start.</summary>
        /// <param name="value">The distance.</param>
        public static implicit operator Index(int value) => FromStart(value);
    }

    /// <summary>A range of a sequence: the BCL type <c>a..b</c> compiles to.</summary>
    /// <param name="start">The inclusive start.</param>
    /// <param name="end">The exclusive end.</param>
    internal readonly struct Range(Index start, Index end)
    {
        /// <summary>The inclusive start.</summary>
        public Index Start { get; } = start;

        /// <summary>The exclusive end.</summary>
        public Index End { get; } = end;

        /// <summary>From <paramref name="start"/> to the end.</summary>
        /// <param name="start">The inclusive start.</param>
        public static Range StartAt(Index start) => new(start, Index.End);

        /// <summary>From the start to <paramref name="end"/>.</summary>
        /// <param name="end">The exclusive end.</param>
        public static Range EndAt(Index end) => new(Index.Start, end);

        /// <summary>The whole sequence.</summary>
        public static Range All => new(Index.Start, Index.End);

        /// <summary>The offset and length in a sequence of <paramref name="length"/>.</summary>
        /// <param name="length">The sequence's length.</param>
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end) throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }
    }
}

namespace System.Collections.Generic
{
    /// <summary>The dictionary lookup netstandard2.0 lacks.</summary>
    internal static class DictionaryPolyfills
    {
        /// <summary>The value under <paramref name="key"/>, or the default.</summary>
        /// <param name="dictionary">The dictionary.</param>
        /// <param name="key">The key.</param>
        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key) where TKey : notnull
            => dictionary.TryGetValue(key, out TValue? value) ? value : default;
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>The nullable-flow attribute of the BCL: the argument is not null when the method returns <see cref="ReturnValue"/>.</summary>
    /// <param name="returnValue">The return value that guarantees a non-null argument.</param>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute(bool returnValue) : Attribute
    {
        /// <summary>The return value that guarantees a non-null argument.</summary>
        public bool ReturnValue { get; } = returnValue;
    }
}
