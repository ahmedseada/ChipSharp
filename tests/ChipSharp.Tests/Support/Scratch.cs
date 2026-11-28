namespace ChipSharp.Tests.Support;

/// <summary>
/// Deletes a test's scratch folder when the test is done. On Windows a program that has just stopped can keep its files
/// open for a moment: one that died of an unhandled exception, as the programs that stop where .NET throws do, is held by
/// Windows Error Reporting. So a delete that fails is tried again for a few seconds, and a folder still in use after
/// that is left in the temp folder rather than failing a test that passed.
/// </summary>
public static class Scratch
{
    private const int Attempts = 10;

    public static void Delete(string folder)
    {
        for (var attempt = 1; Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts)
                {
                    return;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }
}
