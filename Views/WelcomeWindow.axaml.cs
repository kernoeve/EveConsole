using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class WelcomeWindow : Window
{
    // The heading is the title's sentence with the app's name in two colours, wherever the language
    // puts the name: "Welcome to EVE Console", 「EVE Consoleへようこそ」. A sentence that ended in
    // "EVE " with "Console" drawn after it could not be translated into a language that puts the
    // name first.
    private const string AppName = "EVE Console";
    private static readonly string Heading = ShellText.TitleWelcomeToEveConsole;
    private static readonly int NameAt = Heading.IndexOf(AppName, StringComparison.Ordinal);

    public static string HeadingBefore => NameAt < 0 ? Heading + " " : Heading[..NameAt];
    public static string HeadingAfter  => NameAt < 0 ? "" : Heading[(NameAt + AppName.Length)..];

    public WelcomeWindow() => InitializeComponent();

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
