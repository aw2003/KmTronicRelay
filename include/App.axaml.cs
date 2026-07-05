// Avalonia application shell — loads theme and opens MainWindow.
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace KmTronicRelay;

/**
 * @brief Avalonia Application subclass; loads XAML resources and creates MainWindow.
 */
public partial class App : Application {
    /**
     * @brief Loads Avalonia XAML application resources.
     */
    public override void Initialize() {AvaloniaXamlLoader.Load(this);}

    /**
     * @brief Creates and assigns MainWindow when the desktop framework is ready.
     */
    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {desktop.MainWindow = new MainWindow();}
        base.OnFrameworkInitializationCompleted();
    }
}
