namespace DriftBuster.Gui.ViewModels
{
    public sealed class FilterOption<T>
    {
        public FilterOption(T value, string display)
        {
            Value = value;
            Display = display;
        }

        public T Value { get; }

        public string Display { get; }

        public override string ToString() => Display;
    }
}
