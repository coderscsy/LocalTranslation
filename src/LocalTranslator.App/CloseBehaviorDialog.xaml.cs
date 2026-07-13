using System.Windows;

namespace LocalTranslator.App;

public partial class CloseBehaviorDialog : Window
{
    public CloseBehaviorDialog() => InitializeComponent();

    public AppCloseAction SelectedAction { get; private set; } = AppCloseAction.Ask;
    public bool RememberChoice => RememberChoiceCheck.IsChecked == true;

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = AppCloseAction.MinimizeToTray;
        DialogResult = true;
    }

    private void ExitApplication_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = AppCloseAction.Exit;
        DialogResult = true;
    }
}
