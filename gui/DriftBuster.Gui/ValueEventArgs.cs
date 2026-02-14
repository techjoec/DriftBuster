using System;

namespace DriftBuster.Gui;

public sealed class ValueEventArgs<T> : EventArgs
{
    public ValueEventArgs(T value)
    {
        Value = value;
    }

    public T Value { get; }
}
