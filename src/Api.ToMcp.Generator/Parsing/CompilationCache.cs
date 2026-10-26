using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// Results computed from a compilation, kept for as long as the compilation lives. Roslyn scans each class in its own
    /// call, so a result two scans share, such as the analysis of a base controller's action that every controller
    /// inheriting it lists as its own, can only be kept beside the compilation. An IDE runs the generator on a new
    /// compilation after every edit, so no result outlives the code it was computed from.
    /// </summary>
    internal sealed class CompilationCache<TKey, TValue>
        where TKey : notnull
    {
        private readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<TKey, TValue>> _results =
            new ConditionalWeakTable<Compilation, ConcurrentDictionary<TKey, TValue>>();

        private readonly ConditionalWeakTable<Compilation, ConcurrentDictionary<TKey, TValue>>.CreateValueCallback _createResults;

        public CompilationCache(IEqualityComparer<TKey> keyComparer)
        {
            _createResults = _ => new ConcurrentDictionary<TKey, TValue>(keyComparer);
        }

        /// <summary>
        /// The result for the key in the compilation, computed the first time it is asked for. Two scans asking at once may
        /// both compute it, which gives the same result.
        /// </summary>
        public TValue GetOrAdd(Compilation compilation, TKey key, Func<TKey, TValue> compute)
        {
            return _results.GetValue(compilation, _createResults).GetOrAdd(key, compute);
        }
    }
}
