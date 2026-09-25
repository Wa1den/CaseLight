using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CaseLight.Audio;

/// <summary>
/// The default output device: its volume, and the sound it plays for the spectrum.
///
/// Everything that touches Core Audio runs on one thread of its own. The default device is
/// looked up again every couple of seconds, so plugging in headphones moves the level and
/// the spectrum over to them; a notification client would need the same re-creation and
/// more code for it.
/// </summary>
sealed class AudioSource : IDisposable
{
    const int PollMs = 50;
    const int DeviceCheckMs = 2000;

    /// <summary>Samples kept for the spectrum: a power of two, 43 ms at 48 kHz.</summary>
    public const int Window = 2048;

    readonly Thread _thread;
    readonly ManualResetEvent _stop = new(false);
    readonly object _gate = new();
    readonly float[] _ring = new float[Window * 2];
    int _ringAt;

    volatile float _volume;
    volatile bool _muted;
    long _volumeChangedTicks;
    long _lastSoundTicks;
    volatile bool _known;
    volatile int _sampleRate = 48000;

    long _wantedTicks;

    /// <summary>How long capture goes on after the spectrum was last asked for.</summary>
    const int WantedForMs = 1500;

    /// <summary>
    /// Called by the effect on every frame it shows the spectrum. Capture runs only while
    /// these keep coming: with the painting stopped nobody reads the sound.
    /// </summary>
    public void WantCapture() => Interlocked.Exchange(ref _wantedTicks, Environment.TickCount64);

    public AudioSource()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "CaseLight audio" };
        _thread.Start();
    }

    /// <summary>Master volume of the default output, 0..1.</summary>
    public float Volume => _volume;

    public bool Muted => _muted;

    /// <summary>Whether the volume has been read at all.</summary>
    public bool Known => _known;

    /// <summary><see cref="Environment.TickCount64"/> of the last change of volume or mute; 0 if none since start.</summary>
    public long VolumeChangedTicks => Interlocked.Read(ref _volumeChangedTicks);

    /// <summary><see cref="Environment.TickCount64"/> of the last block of captured sound.</summary>
    public long LastSoundTicks => Interlocked.Read(ref _lastSoundTicks);

    public int SampleRate => _sampleRate;

    /// <summary>The latest <see cref="Window"/> samples, oldest first, mixed down to mono.</summary>
    public void Latest(float[] into)
    {
        lock (_gate)
        {
            int start = (_ringAt - Window + _ring.Length) % _ring.Length;
            for (int i = 0; i < Window; i++) into[i] = _ring[(start + i) % _ring.Length];
        }
    }

    void Run()
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        WasapiLoopbackCapture? capture = null;
        string deviceId = "";
        long lastDeviceCheck = 0;
        long captureRetryAt = 0;

        while (!_stop.WaitOne(PollMs))
        {
            long now = Environment.TickCount64;

            try
            {
                if (device == null || now - lastDeviceCheck >= DeviceCheckMs)
                {
                    lastDeviceCheck = now;
                    string id = DefaultId(enumerator);

                    if (id != deviceId)
                    {
                        StopCapture(ref capture);
                        device?.Dispose();
                        device = id == "" ? null : enumerator.GetDevice(id);
                        deviceId = id;
                        _known = false;
                        captureRetryAt = 0;
                    }
                }

                if (device == null) continue;

                ReadVolume(device);

                bool wanted = now - Interlocked.Read(ref _wantedTicks) < WantedForMs;

                if (wanted && capture == null && now >= captureRetryAt)
                {
                    capture = StartCapture(device);

                    // устройство занято или ушло: следующая попытка через пару секунд
                    if (capture == null) captureRetryAt = now + DeviceCheckMs;
                }
                else if (!wanted && capture != null)
                {
                    StopCapture(ref capture);
                }
            }
            catch
            {
                // устройство пропало посреди опроса: всё заводится заново на следующем такте
                StopCapture(ref capture);
                device?.Dispose();
                device = null;
                deviceId = "";
                _known = false;
            }
        }

        StopCapture(ref capture);
        device?.Dispose();
    }

    static string DefaultId(MMDeviceEnumerator enumerator)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return "";
            using var d = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.ID;
        }
        catch { return ""; }
    }

    void ReadVolume(MMDevice device)
    {
        var endpoint = device.AudioEndpointVolume;
        float volume = endpoint.MasterVolumeLevelScalar;
        bool muted = endpoint.Mute;

        // первое чтение не изменение: иначе шкала появлялась бы при каждом запуске
        if (_known && (Math.Abs(volume - _volume) > 0.001f || muted != _muted))
            Interlocked.Exchange(ref _volumeChangedTicks, Environment.TickCount64);

        _volume = volume;
        _muted = muted;
        _known = true;
    }

    WasapiLoopbackCapture? StartCapture(MMDevice device)
    {
        WasapiLoopbackCapture? capture = null;
        try
        {
            capture = new WasapiLoopbackCapture(device);
            var format = capture.WaveFormat;
            _sampleRate = format.SampleRate;

            capture.DataAvailable += (_, e) => Take(e.Buffer, e.BytesRecorded, format);
            capture.StartRecording();
            return capture;
        }
        catch
        {
            capture?.Dispose();
            return null;
        }
    }

    static void StopCapture(ref WasapiLoopbackCapture? capture)
    {
        if (capture == null) return;

        try { capture.StopRecording(); } catch { /* уже остановлен */ }
        try { capture.Dispose(); } catch { /* устройство ушло */ }
        capture = null;
    }

    /// <summary>Mixes a block of captured sound down to mono and appends it to the ring.</summary>
    void Take(byte[] buffer, int bytes, WaveFormat format)
    {
        int channels = Math.Max(1, format.Channels);
        int bits = format.BitsPerSample;
        int frameBytes = channels * bits / 8;
        if (frameBytes == 0 || bytes < frameBytes) return;

        // Общий режим WASAPI отдаёт 32-битный float; целые форматы разбираются на всякий случай.
        bool isFloat = bits == 32 && format.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible;

        int frames = bytes / frameBytes;
        bool loud = false;

        lock (_gate)
        {
            for (int f = 0; f < frames; f++)
            {
                double sum = 0;
                int at = f * frameBytes;

                for (int c = 0; c < channels; c++)
                {
                    int o = at + c * bits / 8;
                    sum += bits switch
                    {
                        32 when isFloat => BitConverter.ToSingle(buffer, o),
                        32 => BitConverter.ToInt32(buffer, o) / 2147483648.0,
                        24 => ((buffer[o] << 8 | buffer[o + 1] << 16 | buffer[o + 2] << 24) >> 8) / 8388608.0,
                        16 => BitConverter.ToInt16(buffer, o) / 32768.0,
                        _ => 0
                    };
                }

                float sample = (float)(sum / channels);
                if (Math.Abs(sample) > 1e-4f) loud = true;

                _ring[_ringAt] = sample;
                _ringAt = (_ringAt + 1) % _ring.Length;
            }
        }

        // тишина тоже приходит блоками нулей, пока играет поток
        if (loud) Interlocked.Exchange(ref _lastSoundTicks, Environment.TickCount64);
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(2000);
        _stop.Dispose();
    }
}
