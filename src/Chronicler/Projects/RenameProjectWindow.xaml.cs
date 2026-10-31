using System.Windows;

namespace Chronicler.Projects;

public partial class RenameProjectWindow : Window
{
    public string ProjectName => NameInput.Text.Trim();

    public RenameProjectWindow(string name)
    {
        InitializeComponent();
        NameInput.Text = name;
        NameInput.SelectAll();
        NameInput.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            ErrorText.Text = "プロジェクト名を入力してください。";
            return;
        }
        DialogResult = true;
    }
}
