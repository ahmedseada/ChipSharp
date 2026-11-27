// ChipSharp runtime for Windows x64 programs: the clock (DateTime.Now, UtcNow, Today, ChipSharp.Hardware's
// Clock.Start) and the uptime (Stopwatch, Environment.TickCount), what ChipSharp.Library's Runtime.Clock... functions
// are on a PC.
//
// Freestanding: no headers and no C runtime; the target's "link.imports" lists the kernel32 functions.
//
//   ChipSharp_Runtime_ClockUtcTicks      DateTime.UtcNow: Windows' clock (GetSystemTimePreciseAsFileTime, as .NET reads
//                                        it), in ticks of 100 ns since 1 January 0001
//   ChipSharp_Runtime_ClockLocalTicks    DateTime.Now: that, moved by the time zone's offset now
//   ChipSharp_Runtime_LocalUtcOffset     the time zone's offset now (GetTimeZoneInformation: its bias, and the daylight
//                                        or standard one in force), for the z and K formats
//   ChipSharp_Runtime_ClockStart         Clock.Start: from then on the clock is a software clock, as on a chip, that
//                                        counts from the date given with the performance counter, with no time zone
//                                        (Now is UtcNow, the offset 0): the same program shows the same time here
//   ChipSharp_Runtime_ClockTimestamp,    Stopwatch.GetTimestamp and Frequency: the performance counter
//   ChipSharp_Runtime_ClockFrequency     (QueryPerformanceCounter and QueryPerformanceFrequency, as .NET's)
//   ChipSharp_Runtime_ClockElapsedTicks  Stopwatch.GetElapsedTime's ticks, as .NET computes them
//   ChipSharp_Runtime_ClockTickCount64   Environment.TickCount64: milliseconds since Windows started (GetTickCount64)

typedef int BOOL;
typedef unsigned long DWORD;
typedef long LONG;

typedef struct
{
    DWORD dwLowDateTime;
    DWORD dwHighDateTime;
} FILETIME;

typedef struct
{
    unsigned short wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
} SYSTEMTIME;

typedef struct
{
    LONG Bias;
    unsigned short StandardName[32];
    SYSTEMTIME StandardDate;
    LONG StandardBias;
    unsigned short DaylightName[32];
    SYSTEMTIME DaylightDate;
    LONG DaylightBias;
} TIME_ZONE_INFORMATION;

__declspec(dllimport) void __stdcall GetSystemTimePreciseAsFileTime(FILETIME *time);
__declspec(dllimport) DWORD __stdcall GetTimeZoneInformation(TIME_ZONE_INFORMATION *zone);
__declspec(dllimport) BOOL __stdcall QueryPerformanceCounter(long long *count);
__declspec(dllimport) BOOL __stdcall QueryPerformanceFrequency(long long *frequency);
__declspec(dllimport) unsigned long long __stdcall GetTickCount64(void);

#define TIME_ZONE_ID_STANDARD 1u
#define TIME_ZONE_ID_DAYLIGHT 2u

// The ticks from 1 January 0001 to 1 January 1601, where a FILETIME counts from.
#define FILE_TIME_OFFSET 504911232000000000LL
#define TICKS_PER_SECOND 10000000LL
#define TICKS_PER_MINUTE 600000000LL

// The software clock, once Clock.Start has set it: the date's ticks, and the performance counter then.
static int clock_started;
static long long clock_ticks;
static long long clock_count;

static long long system_ticks(void)
{
    FILETIME time;
    GetSystemTimePreciseAsFileTime(&time);
    return (long long)(((unsigned long long)time.dwHighDateTime << 32) | time.dwLowDateTime) + FILE_TIME_OFFSET;
}

long long ChipSharp_Runtime_ClockTimestamp(void)
{
    long long count = 0;
    QueryPerformanceCounter(&count);
    return count;
}

long long ChipSharp_Runtime_ClockFrequency(void)
{
    long long frequency = 0;
    QueryPerformanceFrequency(&frequency);
    return frequency;
}

// .NET's (long)((end - start) * (10,000,000.0 / Frequency)): whole numbers when the frequency divides 10,000,000 (the
// 10 MHz of today's Windows), which gives the same exactly; else the double, towards zero, saturated as .NET's is.
long long ChipSharp_Runtime_ClockElapsedTicks(long long start, long long end)
{
    long long frequency = ChipSharp_Runtime_ClockFrequency();
    long long difference = end - start;
    if (frequency > 0 && TICKS_PER_SECOND % frequency == 0)
    {
        return difference * (TICKS_PER_SECOND / frequency);
    }

    double ticks = (double)difference * ((double)TICKS_PER_SECOND / (double)frequency);
    if (ticks != ticks)
    {
        return 0;
    }

    if (ticks >= 9223372036854775807.0)
    {
        return 9223372036854775807LL;
    }

    if (ticks <= -9223372036854775808.0)
    {
        return -9223372036854775807LL - 1;
    }

    return (long long)ticks;
}

long long ChipSharp_Runtime_ClockTickCount64(void)
{
    return (long long)GetTickCount64();
}

long long ChipSharp_Runtime_LocalUtcOffset(void)
{
    if (clock_started)
    {
        return 0;
    }

    TIME_ZONE_INFORMATION zone;
    DWORD id = GetTimeZoneInformation(&zone);
    LONG bias = zone.Bias;
    if (id == TIME_ZONE_ID_DAYLIGHT)
    {
        bias += zone.DaylightBias;
    }
    else if (id == TIME_ZONE_ID_STANDARD)
    {
        bias += zone.StandardBias;
    }

    // the bias is UTC minus local time, in minutes
    return -(long long)bias * TICKS_PER_MINUTE;
}

long long ChipSharp_Runtime_ClockUtcTicks(void)
{
    if (clock_started)
    {
        return clock_ticks + ChipSharp_Runtime_ClockElapsedTicks(clock_count, ChipSharp_Runtime_ClockTimestamp());
    }

    return system_ticks();
}

long long ChipSharp_Runtime_ClockLocalTicks(void)
{
    return ChipSharp_Runtime_ClockUtcTicks() + ChipSharp_Runtime_LocalUtcOffset();
}

void ChipSharp_Runtime_ClockStart(long long ticks)
{
    clock_ticks = ticks;
    clock_count = ChipSharp_Runtime_ClockTimestamp();
    clock_started = 1;
}
