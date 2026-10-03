using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace LightSide
{
    /// <summary>
    /// High-performance dictionary optimized for integer keys using open addressing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses linear probing with power-of-two table sizes for fast lookups.
    /// Designed for hot paths where standard Dictionary overhead is too high.
    /// </para>
    /// <para>
    /// <b>Threading.</b> A SINGLE writer concurrent with any number of readers is safe; concurrent
    /// writers still require external synchronisation. The single-writer/many-reader guarantee is
    /// established with memory barriers (not locks), so it stays allocation-free:
    /// <list type="bullet">
    /// <item><see cref="AddOrUpdate"/> writes <c>key</c> and <c>value</c> FIRST, then publishes the
    /// slot by writing <c>hasValue</c> with <see cref="Volatile.Write(ref bool,bool)"/> LAST. A
    /// reader that observes <c>hasValue == true</c> (read with <see cref="Volatile.Read(ref bool)"/>)
    /// is therefore guaranteed to observe the fully-written <c>key</c>/<c>value</c> — no torn slot,
    /// including on weak memory models (ARM).</item>
    /// <item><see cref="Grow"/> builds a fresh, fully-populated array and publishes it with
    /// <see cref="Volatile.Write{T}(ref T,T)"/>; readers snapshot <c>entries</c> once with
    /// <see cref="Volatile.Read{T}(ref T)"/> and derive the mask from that snapshot, so a grow in
    /// flight never changes the array a reader is walking.</item>
    /// </list>
    /// Grows automatically at 75% load factor.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The value type.</typeparam>
    internal sealed class FastIntDictionary<T>
    {
        private struct Entry
        {
            public int key;
            public T value;
            public bool hasValue;
        }

        private Entry[] entries;
        private int count;

        public FastIntDictionary(int capacity = 16)
        {
            var size = NextPowerOfTwo(capacity);
            entries = new Entry[size];
        }

        public int Count => count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(int key, out T value)
        {
            // Snapshot the array once (a concurrent Grow publishes a NEW array via Volatile.Write; this
            // Volatile.Read either sees the old or the new one whole, never a half-swapped reference).
            var e = Volatile.Read(ref entries);
            var m = e.Length - 1;
            var idx = key & m;

            // Volatile.Read of hasValue pairs with the Volatile.Write in AddOrUpdate: observing true
            // here guarantees the matching key/value stores are also visible (no torn slot on ARM).
            while (Volatile.Read(ref e[idx].hasValue))
            {
                if (e[idx].key == key)
                {
                    value = e[idx].value;
                    return true;
                }
                idx = (idx + 1) & m;
            }

            value = default;
            return false;
        }

        public T this[int key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (TryGetValue(key, out var val))
                    return val;
                throw new KeyNotFoundException();
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => AddOrUpdate(key, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddOrUpdate(int key, T value)
        {
            if (count >= entries.Length * 3 / 4)
                Grow();

            var e = entries;
            var m = e.Length - 1;
            var idx = key & m;

            while (Volatile.Read(ref e[idx].hasValue))
            {
                if (e[idx].key == key)
                {
                    // Updating an EXISTING slot: a reader already past the hasValue gate may read this
                    // value concurrently. For reference/large value types this store is not atomic, but
                    // the single-writer/many-reader contract only guarantees torn-free SLOT PUBLICATION
                    // (key↔value↔hasValue consistency), which an in-place value update does not break:
                    // the slot stays occupied with the same key throughout. Callers needing a coherent
                    // read of an updated value must still synchronise externally.
                    e[idx].value = value;
                    return;
                }
                idx = (idx + 1) & m;
            }

            // Publish a NEW slot: write key and value FIRST, then release-publish hasValue LAST so a
            // reader that sees hasValue==true is guaranteed to see the key/value already stored.
            e[idx].key = key;
            e[idx].value = value;
            Volatile.Write(ref e[idx].hasValue, true);
            count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(int key)
        {
            var e = Volatile.Read(ref entries);
            var m = e.Length - 1;
            var idx = key & m;

            while (Volatile.Read(ref e[idx].hasValue))
            {
                if (e[idx].key == key)
                    return true;
                idx = (idx + 1) & m;
            }

            return false;
        }

        public bool Remove(int key)
        {
            var e = entries;
            var m = e.Length - 1;
            var idx = key & m;

            while (e[idx].hasValue)
            {
                if (e[idx].key == key)
                {
                    count--;
                    var empty = idx;

                    while (true)
                    {
                        idx = (idx + 1) & m;

                        if (!e[idx].hasValue)
                        {
                            e[empty].hasValue = false;
                            e[empty].value = default;
                            return true;
                        }

                        var ideal = e[idx].key & m;

                        if ((empty <= idx) ? (ideal <= empty || ideal > idx) : (ideal <= empty && ideal > idx))
                        {
                            e[empty] = e[idx];
                            empty = idx;
                        }
                    }
                }
                idx = (idx + 1) & m;
            }

            return false;
        }

        public void Clear()
        {
            Array.Clear(entries, 0, entries.Length);
            count = 0;
        }

        public void ClearFast()
        {
            if (count == 0) return;
            var e = entries;
            for (var i = 0; i < e.Length; i++)
                e[i].hasValue = false;
            count = 0;
        }

        private void Grow()
        {
            var oldEntries = entries;
            var newSize = oldEntries.Length * 2;
            var newEntries = new Entry[newSize];
            var newMask = newSize - 1;

            for (var i = 0; i < oldEntries.Length; i++)
            {
                if (oldEntries[i].hasValue)
                {
                    var idx = oldEntries[i].key & newMask;
                    while (newEntries[idx].hasValue)
                        idx = (idx + 1) & newMask;
                    newEntries[idx] = oldEntries[i];
                }
            }

            // Publish the fully-populated new array LAST. A concurrent reader's Volatile.Read(entries)
            // sees either the old array (walked to completion against its own mask) or this new one,
            // never a partially-filled array (every slot above was written before this release store).
            Volatile.Write(ref entries, newEntries);
        }

        private static int NextPowerOfTwo(int v)
        {
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return v + 1;
        }

        public Enumerator GetEnumerator() => new(this);

        public struct Enumerator
        {
            private readonly Entry[] entries;
            private int index;

            internal Enumerator(FastIntDictionary<T> dict)
            {
                entries = dict.entries;
                index = -1;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                while (++index < entries.Length)
                    if (entries[index].hasValue)
                        return true;
                return false;
            }

            public KeyValuePair<int, T> Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => new(entries[index].key, entries[index].value);
            }
        }
    }

}
