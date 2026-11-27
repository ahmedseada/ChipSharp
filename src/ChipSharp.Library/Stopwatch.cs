namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's <see cref="System.Diagnostics.Stopwatch"/>, .NET 10's own (System.Private.CoreLib's Stopwatch.cs) over
/// the target's counter of uptime: on a chip its timer's microseconds (<see cref="Frequency"/> 1,000,000), on a PC the
/// performance counter, as .NET's. The static members need no heap: <c>long start = Stopwatch.GetTimestamp();</c> ...
/// <c>TimeSpan pulse = Stopwatch.GetElapsedTime(start);</c>. A Stopwatch object (<c>Stopwatch.StartNew()</c>,
/// <c>Elapsed</c>, <c>Restart()</c>) is a class: it needs a heap. Only a target whose file says <c>"clock": true</c>
/// has the counter.
/// </summary>
/// <remarks>
/// .NET turns timestamps into ticks of 100 ns with a double (10,000,000 / Frequency); ChipSharp with whole numbers where
/// the frequency divides 10,000,000, as a chip's and today's PCs' do, which gives the same exactly (see
/// <see cref="Runtime.ClockElapsedTicks"/>). ToString is not here.
/// </remarks>
[Substitute(typeof(System.Diagnostics.Stopwatch))]
internal class Stopwatch
{
    public static readonly long Frequency = Runtime.ClockFrequency();

    public static readonly bool IsHighResolution = true;

    private long _elapsed;
    private long _startTimeStamp;
    private bool _isRunning;

    public Stopwatch()
    {
    }

    public bool IsRunning => _isRunning;

    public System.TimeSpan Elapsed => new(ElapsedTimeSpanTicks);

    public long ElapsedMilliseconds => ElapsedTimeSpanTicks / System.TimeSpan.TicksPerMillisecond;

    public long ElapsedTicks
    {
        get
        {
            long timeElapsed = _elapsed;
            if (_isRunning)
            {
                timeElapsed += GetTimestamp() - _startTimeStamp;
            }

            return timeElapsed;
        }
    }

    private long ElapsedTimeSpanTicks => Runtime.ClockElapsedTicks(0, ElapsedTicks);

    public static Stopwatch StartNew()
    {
        Stopwatch s = new();
        s.Start();
        return s;
    }

    public static long GetTimestamp() => Runtime.ClockTimestamp();

    public static System.TimeSpan GetElapsedTime(long startingTimestamp) =>
        GetElapsedTime(startingTimestamp, GetTimestamp());

    public static System.TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        new(Runtime.ClockElapsedTicks(startingTimestamp, endingTimestamp));

    public void Start()
    {
        // a Stopwatch that runs goes on running
        if (!_isRunning)
        {
            _startTimeStamp = GetTimestamp();
            _isRunning = true;
        }
    }

    public void Stop()
    {
        if (_isRunning)
        {
            _elapsed += GetTimestamp() - _startTimeStamp;
            _isRunning = false;
        }
    }

    public void Reset()
    {
        _elapsed = 0;
        _startTimeStamp = 0;
        _isRunning = false;
    }

    public void Restart()
    {
        _elapsed = 0;
        _startTimeStamp = GetTimestamp();
        _isRunning = true;
    }
}
