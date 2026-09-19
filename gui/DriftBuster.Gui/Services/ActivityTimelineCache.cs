namespace DriftBuster.Gui.Services
{
    public sealed record ActivityTimelineCache
    {
        public string? Filter { get; init; }

        public string? LastOpenedHostId { get; init; }
    }
}
