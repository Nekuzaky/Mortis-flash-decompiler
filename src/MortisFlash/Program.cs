using Avalonia;

namespace MortisFlash;

sealed class Program
{
    /// <summary>File passed on the command line (a .swf dropped onto the executable, or a file association).</summary>
    public static string? StartupFile { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // English UI: numbers and sizes are formatted the English way whatever the system language
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        StartupFile = args.FirstOrDefault(File.Exists);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
