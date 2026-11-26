using System;

// A ball thrown up and forward, stepped through time the way a game or a control loop does it: structs of floats
// (a vector, and a body made of two vectors) with operators, a ref parameter that moves the body one step, and
// MathF. The numbers match .NET bit for bit, on the PC and on the Pico, which does its float math in software.
class Program
{
    const float TimeStep = 0.05f;   // seconds

    static void Main()
    {
        var ball = new Body { Position = new Vector(0, 0), Velocity = new Vector(3, 4) };
        var gravity = new Vector(0, -9.81f);
        float highest = 0;
        int steps = 0;
        do
        {
            Step(ref ball, gravity, TimeStep);
            highest = MathF.Max(highest, ball.Position.Y);
            steps++;
        }
        while (ball.Position.Y > 0);

        Console.WriteLine(steps);
        Console.WriteLine(steps * TimeStep);       // seconds in the air
        Console.WriteLine(ball.Position.X);        // how far it went, in metres
        Console.WriteLine(highest);                // how high it got
        Console.WriteLine(ball.Velocity.Length);   // how fast it lands
    }

    // Semi-implicit Euler: the velocity first, then the position with the new velocity.
    static void Step(ref Body body, Vector acceleration, float seconds)
    {
        body.Velocity = body.Velocity + acceleration * seconds;
        body.Position = body.Position + body.Velocity * seconds;
    }
}

struct Vector(float x, float y)
{
    public float X = x;
    public float Y = y;

    public readonly float Length => MathF.Sqrt(X * X + Y * Y);

    public static Vector operator +(Vector a, Vector b) => new Vector(a.X + b.X, a.Y + b.Y);

    public static Vector operator *(Vector v, float k) => new Vector(v.X * k, v.Y * k);
}

struct Body
{
    public Vector Position;
    public Vector Velocity;
}
