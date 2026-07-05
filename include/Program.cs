// Avalonia entry point — no app logic here, only starts the UI framework.
using Avalonia;

namespace KmTronicRelay;

/**
 * @brief Avalonia application entry point; configures and starts the desktop UI.
 */
internal static class Program {
    /**
     * @brief Application main entry; starts the Avalonia classic desktop lifetime.
     * @param args Command-line arguments passed to the process.
     */
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /**
     * @brief Configures and returns the Avalonia AppBuilder for this application.
     * @retval AppBuilder Configured builder ready to start the UI framework.
     */
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
