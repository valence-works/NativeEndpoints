using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace NativeEndpoints.Generator;

/// <summary>An immutable array that compares by content, so it can live in a cached generator model.</summary>
/// <remarks>
/// <see cref="ImmutableArray{T}"/> equality is reference equality over the underlying array, which
/// makes every model containing one compare unequal to its identical predecessor and defeats the
/// incremental pipeline's caching. This wrapper restores structural equality without copying: it
/// holds the same array and implements <see cref="IEquatable{T}"/> element-by-element, which is what
/// record equality and the pipeline's comparers call.
/// </remarks>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> items;

    public EquatableArray(ImmutableArray<T> items) => this.items = items;

    /// <summary>The wrapped array, with the default (uninitialized) struct reading as empty.</summary>
    private ImmutableArray<T> Items => items.IsDefault ? ImmutableArray<T>.Empty : items;

    public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new(items);

    public int Length => Items.Length;

    public bool IsEmpty => Items.IsEmpty;

    public T this[int index] => Items[index];

    int IReadOnlyCollection<T>.Count => Length;

    public bool Equals(EquatableArray<T> other)
    {
        var left = Items;
        var right = other.Items;
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[index], right[index]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            foreach (var item in Items)
                hash = hash * 31 + (item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item));

            return hash;
        }
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    /// <summary>A struct enumerator, so foreach over the wrapper does not allocate.</summary>
    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Items).GetEnumerator();
}
