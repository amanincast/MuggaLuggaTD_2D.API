namespace MuggaLuggaTD_2D.API.Tests.TestSupport;

/// <summary>
/// A clock a test can move. A siege runs for up to twenty hours of real time; the only honest way to
/// test its windows is to own the time it is measured against.
/// </summary>
public class FakeClock : TimeProvider
{
    private DateTimeOffset _now;

    public FakeClock(DateTime? start = null)
    {
        _now = new DateTimeOffset(start ?? DateTime.UtcNow, TimeSpan.Zero);
    }

    public DateTime UtcNow => _now.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
