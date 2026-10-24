using System;
using System.Collections;
using System.Collections.Generic;

namespace IpScanner.ViewModels;

/// <summary>Fixed-capacity FIFO buffer: appending past capacity overwrites the oldest
/// element in O(1) instead of shifting every remaining element like List.RemoveAt(0).
/// Capacity may change between calls to <see cref="Add"/> (e.g. a resized graph) - the
/// backing array only grows when actually needed, and shrinks logically by dropping the
/// oldest entries.</summary>
internal sealed class RingBuffer<T> : IReadOnlyList<T>
{
    private T[] _items = Array.Empty<T>();
    private int _start;

    public int Count { get; private set; }

    public void Add(T item, int capacity)
    {
        capacity = Math.Max(1, capacity);
        if (_items.Length < capacity) Grow(capacity);

        int end = (_start + Count) % _items.Length;
        _items[end] = item;
        if (Count < _items.Length) Count++;
        else _start = (_start + 1) % _items.Length;

        while (Count > capacity) { _start = (_start + 1) % _items.Length; Count--; }
    }

    private void Grow(int capacity)
    {
        var next = new T[capacity];
        for (int i = 0; i < Count; i++) next[i] = this[i];
        _items = next;
        _start = 0;
    }

    public T this[int index] => _items[(_start + index) % _items.Length];

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
