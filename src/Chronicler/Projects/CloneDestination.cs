using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Chronicler.Projects;

public static class CloneDestination
{
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    public static void Reserve(string path)
    {
        if (!CreateDirectory(path, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static bool ExistsAndIsEmpty(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("clone先はフォルダーではありません。");
        }
        EnsureEmpty(path);
        return true;
    }

    public static void EnsureEmpty(string path)
    {
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new IOException("clone先のフォルダーが空ではありません。空のフォルダーを指定してください。");
        }
    }
}
