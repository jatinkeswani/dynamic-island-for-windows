using System.IO;
using Windows.Media.Control;

namespace DynamicIsland;

public sealed record MediaState(
    string Title,
    string Artist,
    byte[]? Thumbnail,
    bool IsPlaying,
    bool CanPrevious,
    bool CanNext,
    bool CanPlayPause,
    string SourceApp);

/// <summary>
/// Reads "now playing" info from Windows' system media controls, the same source as the
/// media flyout that appears when you press a volume key. Works with Spotify, browsers and most players.
/// </summary>
public sealed class MediaService
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _version;

    /// <summary>Raised on a background thread; null means nothing is playing.</summary>
    public event Action<MediaState?>? Changed;

    public async Task InitAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => Attach();
        _manager.SessionsChanged += (_, _) => Attach();
        Attach();
    }

    public void TogglePlayPause() => _ = _session?.TryTogglePlayPauseAsync();
    public void Next() => _ = _session?.TrySkipNextAsync();
    public void Previous() => _ = _session?.TrySkipPreviousAsync();

    private void Attach()
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate)
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
            }

            _session = session = PickSession();

            if (session != null)
            {
                session.MediaPropertiesChanged += OnSessionChanged;
                session.PlaybackInfoChanged += OnSessionChanged;
            }
        }
        _ = RefreshAsync(session);
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync(sender);

    // Prefer whatever is actually playing over the session Windows considers "current"
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        var sessions = _manager.GetSessions();
        return sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? _manager.GetCurrentSession()
            ?? sessions.FirstOrDefault();
    }

    private async Task RefreshAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        int version = Interlocked.Increment(ref _version);

        if (session == null)
        {
            Changed?.Invoke(null);
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();

            byte[]? thumbnail = null;
            if (props.Thumbnail != null)
            {
                using var winrtStream = await props.Thumbnail.OpenReadAsync();
                using var stream = winrtStream.AsStreamForRead();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                thumbnail = memory.ToArray();
            }

            // A newer refresh started while this one was awaiting; let it win
            if (version != _version) return;

            var controls = info.Controls;
            Changed?.Invoke(new MediaState(
                props.Title ?? "",
                props.Artist ?? "",
                thumbnail,
                info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                controls.IsPreviousEnabled,
                controls.IsNextEnabled,
                controls.IsPlayPauseToggleEnabled,
                session.SourceAppUserModelId ?? ""));
        }
        catch (Exception)
        {
            // The session closed mid-read; the manager's SessionsChanged event will refresh us
        }
    }
}
