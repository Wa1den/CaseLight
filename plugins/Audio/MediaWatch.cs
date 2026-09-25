using System;
using System.Threading;
using Windows.Media.Control;

namespace CaseLight.Audio;

/// <summary>
/// Whether something plays media: a player, a browser tab, a streaming app - whatever shows
/// in the media flyout of Windows next to the volume. Sounds of the system, of games and of
/// voice chats have no media session and do not count.
///
/// The sessions are polled rather than followed by events: the events of a session come
/// only while its object is held, and a new player would need its own subscription.
/// </summary>
sealed class MediaWatch : IDisposable
{
    const int PollMs = 250;
    const int RetryMs = 5000;

    /// <summary>How long polling goes on after it was last asked for.</summary>
    const int WantedForMs = 1500;

    readonly Thread _thread;
    readonly ManualResetEvent _stop = new(false);

    long _wantedTicks;
    volatile bool _playing;
    volatile bool _available = true;

    public MediaWatch()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "CaseLight media" };
        _thread.Start();
    }

    /// <summary>Called by the effect on every frame that depends on media. Polling runs only while these keep coming.</summary>
    public void Want() => Interlocked.Exchange(ref _wantedTicks, Environment.TickCount64);

    /// <summary>
    /// Whether a session played at the last poll. A player going over to the next track
    /// reports that it is changing rather than paused, and that counts as playing: the
    /// spectrum is not cut between songs.
    /// </summary>
    public bool Playing => _playing;

    /// <summary>
    /// False when Windows gives no media sessions at all. The effect then does not wait
    /// for media, or the spectrum would never show.
    /// </summary>
    public bool Available => _available;

    void Run()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        long retryAt = 0;

        while (!_stop.WaitOne(PollMs))
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _wantedTicks) >= WantedForMs)
            {
                // старый ответ после простоя показал бы спектр того, что играло тогда
                _playing = false;
                continue;
            }

            if (manager == null)
            {
                if (now < retryAt) continue;

                try
                {
                    manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
                    _available = true;
                }
                catch
                {
                    // службы мультимедиа нет: эффект не ждёт медиа, пока она не найдётся
                    _available = false;
                    retryAt = now + RetryMs;
                    continue;
                }
            }

            try
            {
                bool playing = false;

                foreach (var session in manager.GetSessions())
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus? status;
                    try { status = session.GetPlaybackInfo()?.PlaybackStatus; }
                    catch { continue; }   // сеанс закрылся посреди опроса

                    if (status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                               or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing)
                    {
                        playing = true;
                        break;
                    }
                }

                _playing = playing;
            }
            catch
            {
                // менеджер сломался: берётся заново на следующем такте
                manager = null;
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(2000);
        _stop.Dispose();
    }
}
