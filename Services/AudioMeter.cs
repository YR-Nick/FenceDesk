using System.Runtime.InteropServices;

namespace FenceDesk.Services;

/// <summary>
/// 读取 Windows 默认播放设备的峰值电平，用于音乐浮动岛的节奏动画。
/// 使用 Core Audio 的 IAudioMeterInformation，不注入任何播放器。
/// </summary>
public sealed class AudioMeter : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IAudioMeterInformation? _meter;
    private DateTime _nextRetryUtc;

    public float GetPeak()
    {
        try
        {
            if (_meter is null && DateTime.UtcNow >= _nextRetryUtc)
            {
                Initialize();
            }

            if (_meter is null) return 0f;
            var hr = _meter.GetPeakValue(out var peak);
            if (hr < 0)
            {
                Release();
                _nextRetryUtc = DateTime.UtcNow.AddSeconds(2);
                return 0f;
            }

            return Math.Clamp(peak, 0f, 1f);
        }
        catch
        {
            Release();
            _nextRetryUtc = DateTime.UtcNow.AddSeconds(2);
            return 0f;
        }
    }

    private void Initialize()
    {
        Release();
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var hr = _enumerator.GetDefaultAudioEndpoint(0, 0, out var device);
            if (hr < 0)
            {
                Release();
                return;
            }

            try
            {
                var iid = typeof(IAudioMeterInformation).GUID;
                hr = device.Activate(ref iid, 23, IntPtr.Zero, out var meterObject);
                if (hr < 0 || meterObject is null)
                {
                    Release();
                    return;
                }

                _meter = (IAudioMeterInformation)meterObject;
                LogService.Info("音乐浮动岛：已连接默认音频设备");
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch
        {
            Release();
        }
    }

    private void Release()
    {
        if (_meter is not null)
        {
            try { Marshal.ReleaseComObject(_meter); } catch { }
            _meter = null;
        }
        if (_enumerator is not null)
        {
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
        }
    }

    public void Dispose() => Release();

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int classContext, IntPtr activationParams,
            [MarshalAs(UnmanagedType.Interface)] out object interfacePointer);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out int channelCount);
        [PreserveSig] int GetChannelsPeakValues(int channelCount, [Out] float[] peakValues);
        [PreserveSig] int QueryHardwareSupport(out int hardwareSupportMask);
    }
}
