using Windows.Media.Control;

namespace FenceDesk.Services;

/// <summary>
/// 通过 Windows 全局媒体会话判断当前是否有音乐/视频在播放。
/// 避免把游戏音效、系统提示音误判成音乐，让桌面精灵在没有音乐时保持圆形。
/// </summary>
public sealed class MediaSessionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private DateTime _nextInitUtc;
    private DateTime _nextCheckUtc;
    private bool _lastPlaying;

    public bool IsPlaying()
    {
        var now = DateTime.UtcNow;
        if (now < _nextCheckUtc) return _lastPlaying;
        _nextCheckUtc = now.AddMilliseconds(450);

        try
        {
            if (_manager is null)
            {
                if (now < _nextInitUtc) return false;
                _nextInitUtc = now.AddSeconds(5);
                _manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                    .AsTask().GetAwaiter().GetResult();
            }

            var sessions = _manager?.GetSessions();
            _lastPlaying = sessions is not null && sessions.Any(session =>
            {
                try
                {
                    var info = session.GetPlaybackInfo();
                    return info?.PlaybackStatus ==
                           GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }
                catch
                {
                    return false;
                }
            });
        }
        catch
        {
            _manager = null;
            _nextInitUtc = DateTime.UtcNow.AddSeconds(5);
            _lastPlaying = false;
        }

        return _lastPlaying;
    }
}
