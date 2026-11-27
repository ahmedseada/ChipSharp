namespace ChipSharp.Library;

/// <summary>
/// <see cref="System.DayOfWeek"/>, with .NET's values: a program's DayOfWeek is this enum, printed by its name
/// (<c>Thursday</c>) as .NET prints it.
/// </summary>
[Substitute(typeof(System.DayOfWeek))]
internal enum DayOfWeek
{
    Sunday = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 3,
    Thursday = 4,
    Friday = 5,
    Saturday = 6,
}

/// <summary>
/// <see cref="System.DateTimeKind"/>, with .NET's values: whether a <see cref="DateTime"/> is a local time, a UTC time
/// or neither. On a chip the local time is UTC (see <see cref="DateTime.Now"/>).
/// </summary>
[Substitute(typeof(System.DateTimeKind))]
internal enum DateTimeKind
{
    Unspecified = 0,
    Utc = 1,
    Local = 2,
}

