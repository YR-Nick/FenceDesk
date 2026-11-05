using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace FenceDesk.Services;

/// <summary>
/// 隐藏围栏的文件属性、密码和资源管理器右键菜单支持。
/// 注意：隐藏围栏使用 Windows 的 Hidden 属性，不是文件加密；密码只锁 FenceDesk 的入口。
/// </summary>
public static class HiddenVaultService
{
    private const int PasswordIterations = 120_000;
    private const string ExplorerMenuKey = "FenceDeskAddHidden";

    public static (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPassword(string password, string? hashBase64, string? saltBase64)
    {
        if (string.IsNullOrWhiteSpace(hashBase64) || string.IsNullOrWhiteSpace(saltBase64))
        {
            return true;
        }

        try
        {
            var expected = Convert.FromBase64String(hashBase64);
            var salt = Convert.FromBase64String(saltBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, PasswordIterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetHiddenAttribute(string path, bool hidden, out bool wasHidden, out string? error)
    {
        wasHidden = false;
        error = null;
        try
        {
            var attributes = File.GetAttributes(path);
            wasHidden = attributes.HasFlag(FileAttributes.Hidden);
            if (hidden)
            {
                attributes |= FileAttributes.Hidden;
            }
            else if (!wasHidden)
            {
                attributes &= ~FileAttributes.Hidden;
            }

            File.SetAttributes(path, attributes);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string RequestDirectory
    {
        get
        {
            var directory = Path.Combine(AppPaths.DataDirectory, "hidden-requests");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static void EnqueueRequest(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var file = Path.Combine(RequestDirectory, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(file, Path.GetFullPath(path), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogService.Error("写入隐藏请求失败", ex);
        }
    }

    public static List<string> TakeRequests()
    {
        var result = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(RequestDirectory, "*.txt"))
            {
                try
                {
                    var path = File.ReadAllText(file, Encoding.UTF8).Trim();
                    if (!string.IsNullOrWhiteSpace(path)) result.Add(path);
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    LogService.Error("读取隐藏请求失败: " + file, ex);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("扫描隐藏请求失败", ex);
        }

        return result;
    }

    public static void RegisterExplorerMenu()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var command = "\"" + exe + "\" --add-hidden \"%1\"";
            RegisterUnder(@"Software\Classes\*\shell\" + ExplorerMenuKey, exe, command);
            RegisterUnder(@"Software\Classes\Directory\shell\" + ExplorerMenuKey, exe, command);
        }
        catch (Exception ex)
        {
            LogService.Error("注册资源管理器隐藏菜单失败", ex);
        }
    }

    private static void RegisterUnder(string keyPath, string exe, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (key is null) return;
        key.SetValue("MUIVerb", "添加隐藏功能");
        key.SetValue("Icon", exe);
        using var commandKey = key.CreateSubKey("command");
        commandKey?.SetValue(string.Empty, command);
    }
}
