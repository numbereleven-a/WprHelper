using System.Windows;

namespace WprHelper.App;

public partial class ProfileNameDialog : Window
{
    public ProfileNameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public string ProfileName => NameBox.Text.Trim();

    private void AcceptClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileName)) return;
        DialogResult = true;
    }
}
