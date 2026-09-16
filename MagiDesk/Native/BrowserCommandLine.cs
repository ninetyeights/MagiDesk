using System.Runtime.InteropServices;

namespace MagiDesk.Native;

internal static class BrowserCommandLine
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static string[] Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Contains('\0') || text.Length > 16000)
            throw new ArgumentException("参数过长或含有无效字符。");
        var ptr = CommandLineToArgvW("browser.exe " + text, out int count);
        if (ptr == IntPtr.Zero) throw new ArgumentException("无法解析启动参数。");
        try
        {
            var args = new string[count - 1];
            for (int i = 1; i < count; i++)
            {
                string arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size))!;
                string name = arg.Split('=', 2)[0].TrimStart('-', '/');
                if (arg == "--" || name.Equals("profile-directory", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("user-data-dir", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("不能设置 profile-directory、user-data-dir 或单独的 --，以免覆盖账号选择。");
                args[i - 1] = arg;
            }
            return args;
        }
        finally { LocalFree(ptr); }
    }
}
