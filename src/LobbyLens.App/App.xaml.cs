using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LobbyLens.App;

public partial class App : Application
{
    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), $"{DateTime.Now:O} {e.Exception}\n\n");
        }
        catch (IOException) { }
        MessageBox.Show("Ошибка: " + e.Exception.Message + "\n\nПодробности в %APPDATA%\\LobbyLens\\error.log", "LobbyLens",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
