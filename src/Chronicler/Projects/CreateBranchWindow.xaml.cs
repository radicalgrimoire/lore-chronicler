using System.Windows;

namespace Chronicler.Projects;

public partial class CreateBranchWindow : Window
{
    public string BranchName => BranchNameInput.Text.Trim();

    public CreateBranchWindow()
    {
        InitializeComponent();
        BranchNameInput.Focus();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BranchName))
        {
            ErrorText.Text = "Enter a branch name.";
            return;
        }
        DialogResult = true;
    }
}
