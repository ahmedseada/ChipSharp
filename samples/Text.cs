using System;

// Text: strings built and kept on the heap, literals in flash. A thermostat takes commands as text, the way they come
// over a serial line: each line is trimmed, split into a word and a number, picked with a switch on strings and parsed
// with int.Parse. Readings are kept as text in a log, enum names describe the modes ([Flags] combined too), chars are
// counted and changed, and a lot of short-lived strings make the Pico's garbage collector run again and again while the
// log stays.

enum Mode
{
    Off,
    Heat,
    Cool,
}

[Flags]
enum Alarm
{
    None = 0,
    TooHot = 1,
    TooCold = 2,
    Sensor = 4,
}

struct Reading
{
    public string Place;
    public int Tenths;
}

class Log
{
    private readonly string[] lines = new string[4];
    private int count;

    public void Add(string line) => lines[count++ % lines.Length] = line;

    public override string ToString() => string.Join(" | ", lines);
}

class Program
{
    static Mode mode = Mode.Off;
    static int target = 200;
    static readonly Log log = new Log();

    static string Degrees(int tenths) => $"{tenths / 10}.{(tenths < 0 ? -tenths : tenths) % 10} C";

    static string Run(string line)
    {
        string command = line.Trim();
        int space = command.IndexOf(' ');
        string word = (space < 0 ? command : command.Substring(0, space)).ToLowerInvariant();
        string argument = space < 0 ? "" : command.Substring(space + 1).Trim();
        switch (word)
        {
            case "heat":
                mode = Mode.Heat;
                break;
            case "cool":
                mode = Mode.Cool;
                break;
            case "off":
                mode = Mode.Off;
                break;
            case "set":
                if (!int.TryParse(argument, out var degrees))
                {
                    return $"'{argument}' is no number";
                }

                target = degrees * 10;
                break;
            default:
                return "unknown command: " + word;
        }

        return $"{mode} to {Degrees(target)}";
    }

    static Alarm Check(Reading reading)
    {
        var alarm = Alarm.None;
        if (reading.Tenths > target + 30)
        {
            alarm |= Alarm.TooHot;
        }

        if (reading.Tenths < target - 30)
        {
            alarm |= Alarm.TooCold;
        }

        if (reading.Place.Length == 0)
        {
            alarm |= Alarm.Sensor;
        }

        return alarm;
    }

    static void Main()
    {
        foreach (var line in new[] { "  HEAT  ", "set 21", "set x", "cool", "set 18", "jump" })
        {
            Console.WriteLine(Run(line));
        }

        Reading[] readings =
        {
            new Reading { Place = "kitchen", Tenths = 192 },
            new Reading { Place = "attic", Tenths = 245 },
            new Reading { Place = "", Tenths = 120 },
        };
        foreach (var reading in readings)
        {
            var alarm = Check(reading);
            string text = $"{reading.Place,-8}|{Degrees(reading.Tenths),7}|{alarm}";
            log.Add(text);
            Console.WriteLine(text);
        }

        Console.WriteLine(log);
        Console.WriteLine(Alarm.TooHot | Alarm.Sensor);

        string name = "Thermostat";
        int upper = 0;
        var letters = name.ToCharArray();
        for (int i = 0; i < letters.Length; i++)
        {
            if (char.IsUpper(letters[i]))
            {
                upper++;
            }

            letters[i] = i % 2 == 0 ? char.ToUpper(letters[i]) : char.ToLower(letters[i]);
        }

        Console.WriteLine(new string(letters) + " has " + upper + " capital, " + name.Length + " letters, ends with '" +
            name[^1] + "'");
        Console.WriteLine(name.Replace("stat", "meter").PadLeft(14, '.') + " " + name.Contains("mo") + " " +
            name.StartsWith("Thermo"));

        // short-lived strings: on the Pico they fill the heap many times over
        long total = 0;
        string last = "";
        for (int i = 0; i < 3000; i++)
        {
            last = $"reading {i}: {Degrees(150 + i % 100)}";
            total += last.Length;
        }

        Console.WriteLine(total + " characters, the last: " + last);
        Console.WriteLine(log.ToString().Split('|').Length + " parts in the log, still there");
    }
}
