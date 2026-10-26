using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Api.ToMcp.Generator.Models
{
    /// <summary>
    /// An immutable array that equals another holding equal items in the same order. Roslyn reuses a pipeline step's
    /// output from the run before when its input equals that run's, and an <see cref="ImmutableArray{T}"/> equals only
    /// itself, so a model holding one would never let the steps after it be reused. The default value is empty.
    /// </summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    {
        public static readonly EquatableArray<T> Empty = new EquatableArray<T>(ImmutableArray<T>.Empty);

        private readonly ImmutableArray<T> _items;

        public EquatableArray(ImmutableArray<T> items)
        {
            _items = items;
        }

        public int Length => AsImmutableArray().Length;

        public bool IsEmpty => Length == 0;

        int IReadOnlyCollection<T>.Count => Length;

        public T this[int index] => AsImmutableArray()[index];

        public ImmutableArray<T> AsImmutableArray()
        {
            return _items.IsDefault ? ImmutableArray<T>.Empty : _items;
        }

        public bool Contains(T item)
        {
            return AsImmutableArray().Contains(item);
        }

        public bool Equals(EquatableArray<T> other)
        {
            var items = AsImmutableArray();
            var otherItems = other.AsImmutableArray();
            if (items.Length != otherItems.Length)
                return false;

            for (var i = 0; i < items.Length; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(items[i], otherItems[i]))
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is EquatableArray<T> other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                foreach (var item in AsImmutableArray())
                    hash = (hash * 31) + (item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item));

                return hash;
            }
        }

        public ImmutableArray<T>.Enumerator GetEnumerator()
        {
            return AsImmutableArray().GetEnumerator();
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)AsImmutableArray()).GetEnumerator();
        }

        public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right)
        {
            return !left.Equals(right);
        }

        public static implicit operator EquatableArray<T>(ImmutableArray<T> items)
        {
            return new EquatableArray<T>(items);
        }
    }
}
