using System.Windows;

namespace LocalTranslator.App;

public partial class StyledConfirmDialog : Window
{
    public StyledConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
