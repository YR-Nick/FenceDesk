using System.IO;
using System.Runtime.InteropServices;

namespace FenceDesk.Services;

public enum RecycleBinEmptyResult
{
    Emptied,
    AlreadyEmpty,
}

public static class RecycleBinService
{
    public static RecycleBinEmptyResult Empty()
    {
        var before = QueryCount();
        if (before == 0)
        {
            NotifyRecycleBinChanged();
            return RecycleBinEmptyResult.AlreadyEmpty;
        }

        var result = NativeMethods.SHEmptyRecycleBin(
            IntPtr.Zero,
            null,
            NativeMethods.SHERB_NOCONFIRMATION |
            NativeMethods.SHERB_NOPROGRESSUI |
            NativeMethods.SHERB_NOSOUND);

        var after = QueryCount();
        if (result < 0 && after != 0)
        {
            throw new InvalidOperationException("清空回收站失败，错误代码 0x" + result.ToString("X8"));
        }

        NotifyRecycleBinChanged();
        return after == 0 && before > 0 ? RecycleBinEmptyResult.Emptied : RecycleBinEmptyResult.AlreadyEmpty;
    }

    public static long QueryCount()
    {
        try
        {
            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>(),
            };
            var hr = NativeMethods.SHQueryRecycleBin(null, ref info);
            return hr >= 0 ? info.i64NumItems : -1;
        }
        catch
        {
            return -1;
        }
    }

    public static void NotifyRecycleBinChanged()
    {
        var recyclePath = Path.Combine(
            Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "$Recycle.Bin");
        var pathPtr = Marshal.StringToHGlobalUni(recyclePath);
        try
        {
            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEDIR,
                NativeMethods.SHCNF_PATHW, pathPtr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
        }

        NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED,
            NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }
}
