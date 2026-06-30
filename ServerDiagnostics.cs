namespace SurvivalcraftServer;

internal static class ServerDiagnostics
{
    public static bool DebugEnabled { get; set; }

    public static void Debug(string message)
    {
        if (DebugEnabled)
        {
            Console.WriteLine(message);
        }
    }
}
