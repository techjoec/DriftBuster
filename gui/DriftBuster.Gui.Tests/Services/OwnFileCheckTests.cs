using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Services;

/// <summary>The check before the main window opens: the first own file that cannot be read stops the start.</summary>
public sealed class OwnFileCheckTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-own-files-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void Every_file_reading_passes()
    {
        OwnFileCheck.FirstFailure(() => new SessionCacheService(_tmp.FullName).Read(), () => new DiffPlannerMruStore(_tmp.FullName).Read())
            .Should().BeNull();
    }

    [Fact]
    public void The_first_data_or_io_failure_is_the_reason()
    {
        var session = new SessionCacheService(_tmp.FullName);
        File.WriteAllText(session.CachePath, "[]");
        var later = false;

        var failure = OwnFileCheck.FirstFailure(
            () => new DiffPlannerMruStore(_tmp.FullName).Read(),
            session.Read,
            () => later = true);

        failure.Should().StartWith(session.CachePath + ": $");
        later.Should().BeFalse();
        OwnFileCheck.FirstFailure(() => throw new UnauthorizedAccessException("denied")).Should().Be("denied");
        FluentActions.Invoking(() => OwnFileCheck.FirstFailure(() => throw new InvalidOperationException("bug")))
            .Should().Throw<InvalidOperationException>("only data and I/O failures are about the files");
    }
}
