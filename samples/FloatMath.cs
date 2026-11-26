using System;

// Float and double math, which the Pico does in software: the same results as .NET, bit for bit, on the PC and the chip.
class Program
{
    static float Logit(float weight0, float weight1, float feature0, float feature1)
    {
        return (weight0 * feature0) + (weight1 * feature1);
    }

    static float SumFloats(int count, float step)
    {
        float sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += step;
        }

        return sum;
    }

    static double SumDoubles(int count, double step)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += step;
        }

        return sum;
    }

    static double Hypotenuse(double a, double b) => Math.Sqrt(a * a + b * b);

    static double Average(float a, double b) => (a + b) / 2;

    static double Divide(double a, double b) => a / b;

    static void Main()
    {
        // a logit from two weights and two features, each in a variable of its own
        float weight0 = 0.5f;
        float weight1 = -0.25f;
        float feature0 = 1.0f;
        float feature1 = 2.0f;
        Console.WriteLine(Logit(weight0, weight1, feature0, feature1));
        Console.WriteLine(Logit(0.75f, 1.5f, -2.0f, 0.1f));

        // 0.1 a thousand times: every float sum rounds to 24 bits, every double sum to 53
        Console.WriteLine(SumFloats(1000, 0.1f));
        Console.WriteLine(SumDoubles(1000, 0.1));

        // square roots, and a float and a double together (the float is widened first)
        Console.WriteLine(Hypotenuse(3, 4));
        Console.WriteLine(Math.Sqrt(2));
        Console.WriteLine(MathF.Sqrt(2));
        Console.WriteLine(Average(0.1f, 0.1));
        Console.WriteLine(Math.Abs(-2.5) + Math.Min(1.5, -0.5) + MathF.Max(0.25f, 0.125f));

        // division by zero gives the infinities and NaN; NaN is unordered, so every comparison with it is false
        double infinity = Divide(1, 0);
        double nan = Divide(0, 0);
        Console.WriteLine(infinity);
        Console.WriteLine(Divide(-1, 0));
        Console.WriteLine(nan);
        Console.WriteLine(nan == nan);
        Console.WriteLine(nan != nan);
        Console.WriteLine(nan < 1);
        Console.WriteLine(!(nan >= 1));
        Console.WriteLine(Math.Max(nan, 1));
        Console.WriteLine(-Divide(0, 1));

        // to an integer: toward zero; beyond the type's range, its smallest or largest value; NaN becomes 0
        Console.WriteLine((int)Divide(-7, 2));
        Console.WriteLine((int)Divide(1e10, 1));
        Console.WriteLine((long)infinity);
        Console.WriteLine((uint)Divide(-1, 1));
        Console.WriteLine((byte)Divide(300.5, 1));
        Console.WriteLine((int)nan);
        Console.WriteLine((ulong)Divide(1e19, 1));
        Console.WriteLine(Divide(7.5, 1) % 2);
    }
}
