namespace COMPEL;

/// <summary>
///     The COMPEL start-up banner, rendered in the "Slant Relief" FIGfont.
/// </summary>
internal static class Banner
{
    public static void Write()
    {
        Console.WriteLine();
        Console.WriteLine(Art);
        Console.WriteLine();
    }

    /// <summary>
    ///     Appends a session header to the log file: the banner at the very top of a fresh file, and a blank-line-separated session marker before each run's entries, so successive sessions read as distinct blocks.
    /// </summary>
    public static void WriteToLogFile(string logFilePath)
    {
        try
        {
            bool fileHasContent = File.Exists(logFilePath) && new FileInfo(logFilePath).Length > 0;

            StringBuilder header = new ();

            if (fileHasContent)
                // A Blank Line Separates This Session's Block From The Previous One.
                header.AppendLine();

            else
            {
                // A Fresh Log File Opens With The Banner At The Very Top.
                header.AppendLine(Art);
                header.AppendLine();
            }

            header.AppendLine($"▝▚▞▚▞▚▞▚▖ COMPEL Session Started At {DateTime.Now:O} ▗▞▚▞▚▞▚▞▘");

            File.AppendAllText(logFilePath, header.ToString());
        }

        catch
        {
            // Best-Effort: The Session Header Is Cosmetic, So A Failure To Write It Must Not Prevent Start-Up. A Genuinely Unwritable Log Location Surfaces When The Logger Itself Opens The File.
        }
    }

    private const string Art = @"________/\\\\\\\\\_______/\\\\\_______/\\\\____________/\\\\__/\\\\\\\\\\\\\____/\\\\\\\\\\\\\\\__/\\\_____________
 _____/\\\////////______/\\\///\\\____\/\\\\\\________/\\\\\\_\/\\\/////////\\\_\/\\\///////////__\/\\\_____________
  ___/\\\/_____________/\\\/__\///\\\__\/\\\//\\\____/\\\//\\\_\/\\\_______\/\\\_\/\\\_____________\/\\\_____________
   __/\\\______________/\\\______\//\\\_\/\\\\///\\\/\\\/_\/\\\_\/\\\\\\\\\\\\\/__\/\\\\\\\\\\\_____\/\\\_____________
    _\/\\\_____________\/\\\_______\/\\\_\/\\\__\///\\\/___\/\\\_\/\\\/////////____\/\\\///////______\/\\\_____________
     _\//\\\____________\//\\\______/\\\__\/\\\____\///_____\/\\\_\/\\\_____________\/\\\_____________\/\\\_____________
      __\///\\\___________\///\\\__/\\\____\/\\\_____________\/\\\_\/\\\_____________\/\\\_____________\/\\\_____________
       ____\////\\\\\\\\\____\///\\\\\/_____\/\\\_____________\/\\\_\/\\\_____________\/\\\\\\\\\\\\\\\_\/\\\\\\\\\\\\\\\_
        _______\/////////_______\/////_______\///______________\///__\///______________\///////////////__\///////////////__";
}
