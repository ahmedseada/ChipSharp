using System;

// The program ChipSharp was first asked to run: a logit, the weighted sum of a model's features, from two float arrays.
// The arrays live on the stack of Main, which makes them, and Logit reads them.
class Program
{
    static float Logit(float[] weights, float[] features)
    {
        return (weights[0] * features[0]) + (weights[1] * features[1]);
    }

    static void Main()
    {
        float[] weights = { 0.5f, -0.25f };
        float[] features = { 1.0f, 2.0f };

        float result = Logit(weights, features);
        Console.WriteLine(result);

        // other features, the same weights
        features[0] = 3.0f;
        features[1] = 0.5f;
        Console.WriteLine(Logit(weights, features));
    }
}
