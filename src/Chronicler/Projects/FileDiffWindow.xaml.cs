using System.IO;
using System.Text;
using System.Windows;
using LoreVcs;

namespace Chronicler.Projects;

public partial class FileDiffWindow : Window
{
    private readonly Func<Task<FileDiff>> readDiff;
    private bool closed;
    private bool loading;

    public FileDiffWindow(RevisionChange change, Func<Task<FileDiff>> readDiff)
    {
        this.readDiff = readDiff;
        InitializeComponent();
        FilePathText.Text = change.DisplayPath;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (closed || loading) return;
        loading = true;
        RetryButton.IsEnabled = false;
        DiffText.Clear();
        DiffStatus.Text = "Loading file diff (connecting to the server if needed)…";
        try
        {
            var diff = await readDiff();
            if (closed) return;
            if (!diff.IsText)
            {
                DiffStatus.Text = diff.Notice ?? "This file cannot be compared.";
                return;
            }
            DiffText.Text = TextDiffFormatter.Format(diff);
            DiffStatus.Text = diff.Notice ?? "Showing text differences.";
        }
        catch (LoreError ex)
        {
            if (!closed) DiffStatus.Text = $"Could not retrieve file diff (Lore error {ex.ReturnCode}).\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (!closed) DiffStatus.Text = $"Could not retrieve file diff: {ex.Message}";
        }
        finally
        {
            loading = false;
            if (!closed) RetryButton.IsEnabled = true;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closed(object? sender, EventArgs e) => closed = true;
}

public static class TextDiffFormatter
{
    public static string Format(FileDiff diff)
    {
        var before = SplitLines(diff.Before);
        var after = SplitLines(diff.After);
        var common = LongestCommonSubsequence(before, after);
        var builder = new StringBuilder($"--- {diff.BeforeLabel}\n+++ {diff.AfterLabel}\n");
        var beforeIndex = 0;
        var afterIndex = 0;
        foreach (var (beforeMatch, afterMatch) in common)
        {
            while (beforeIndex < beforeMatch) builder.Append('-').Append(before[beforeIndex++]).Append('\n');
            while (afterIndex < afterMatch) builder.Append('+').Append(after[afterIndex++]).Append('\n');
            builder.Append(' ').Append(before[beforeIndex++]).Append('\n');
            afterIndex++;
        }
        while (beforeIndex < before.Length) builder.Append('-').Append(before[beforeIndex++]).Append('\n');
        while (afterIndex < after.Length) builder.Append('+').Append(after[afterIndex++]).Append('\n');
        return builder.ToString();
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        return normalized.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static IReadOnlyList<(int Before, int After)> LongestCommonSubsequence(string[] before, string[] after)
    {
        var lengths = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
            for (var j = after.Length - 1; j >= 0; j--)
                lengths[i, j] = before[i] == after[j] ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var matches = new List<(int, int)>();
        for (int i = 0, j = 0; i < before.Length && j < after.Length;)
        {
            if (before[i] == after[j])
            {
                matches.Add((i++, j++));
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1]) i++;
            else j++;
        }
        return matches;
    }
}
