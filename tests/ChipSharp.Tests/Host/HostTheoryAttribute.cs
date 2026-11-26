namespace ChipSharp.Tests.Host;

/// <summary>Like <see cref="HostFactAttribute"/>, for a test that runs once per case: skipped where PC programs cannot be built and run.</summary>
public sealed class HostTheoryAttribute : TheoryAttribute
{
    public HostTheoryAttribute()
    {
        if (new HostFactAttribute().Skip is { } reason)
        {
            Skip = reason;
        }
    }
}
