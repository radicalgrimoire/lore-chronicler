using System.ComponentModel;
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
}
