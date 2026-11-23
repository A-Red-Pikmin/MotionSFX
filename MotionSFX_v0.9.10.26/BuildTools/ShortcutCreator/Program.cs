using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MotionSFX.BuildTools;

internal static class Program
{
    [STAThread]
    [SupportedOSPlatform("windows")]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Desktop shortcut creation is supported on Windows only.");
            return 1;
        }

        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("Usage: ShortcutCreator <path-to-MotionSFX.exe>");
            return 2;
        }

        string executablePath = Path.GetFullPath(args[0]);
        if (!File.Exists(executablePath))
        {
            Console.Error.WriteLine($"The compiled application was not found: {executablePath}");
            return 3;
        }

        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopPath))
        {
            Console.Error.WriteLine("Windows did not return a Desktop folder path.");
            return 4;
        }

        Directory.CreateDirectory(desktopPath);
        string shortcutPath = Path.Combine(desktopPath, "MotionSFX.lnk");
        object? shellLinkObject = null;

        try
        {
            shellLinkObject = new ShellLink();
            var shellLink = (IShellLinkW)shellLinkObject;
            var persistFile = (IPersistFile)shellLinkObject;

            Check(shellLink.SetPath(executablePath), "setting the shortcut target");
            Check(shellLink.SetWorkingDirectory(Path.GetDirectoryName(executablePath)!), "setting the working directory");
            Check(shellLink.SetDescription("MotionSFX - Motion-triggered sound effects"), "setting the shortcut description");
            Check(shellLink.SetIconLocation(executablePath, 0), "setting the shortcut icon");
            Check(shellLink.SetShowCmd(1), "setting the shortcut window mode");
            Check(persistFile.Save(shortcutPath, true), "saving the desktop shortcut");

            Console.WriteLine($"Desktop shortcut created: {shortcutPath}");
            Console.WriteLine($"Target: {executablePath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not create the desktop shortcut: {ex.Message}");
            return 5;
        }
        finally
        {
            if (shellLinkObject is not null && Marshal.IsComObject(shellLinkObject))
                Marshal.FinalReleaseComObject(shellLinkObject);
        }
    }

    private static void Check(int hresult, string operation)
    {
        if (hresult < 0)
            throw new COMException($"Windows failed while {operation}.", hresult);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath(IntPtr file, int maxPath, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(IntPtr itemIdList);
        [PreserveSig] int SetIDList(IntPtr itemIdList);
        [PreserveSig] int GetDescription(IntPtr name, int maxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory(IntPtr directory, int maxPath);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments(IntPtr arguments, int maxPath);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int showCommand);
        [PreserveSig] int SetShowCmd(int showCommand);
        [PreserveSig] int GetIconLocation(IntPtr iconPath, int maxPath, out int iconIndex);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        [PreserveSig] int GetCurFile(out IntPtr fileName);
    }
}
