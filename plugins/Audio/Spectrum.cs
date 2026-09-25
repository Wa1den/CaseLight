using System;

namespace CaseLight.Audio;

/// <summary>
/// Levels of logarithmic frequency bands from a window of samples, 0..1 each.
///
/// The scale is set so that a full-scale sine reads 0 dB in its band. Music carries far less
/// energy at the top than at the bottom, and with a flat scale the right half of the
/// keyboard stayed dark; a tilt of 3 dB per octave around 1 kHz evens it
/// out.
/// </summary>
sealed class Spectrum
{
    const double LowHz = 40, HighHz = 16000;
    const double TiltPerOctave = 3;

    readonly int _n = AudioSource.Window;
    readonly float[] _samples = new float[AudioSource.Window];
    readonly double[] _re = new double[AudioSource.Window];
    readonly double[] _im = new double[AudioSource.Window];
    readonly double[] _hann = new double[AudioSource.Window];

    public Spectrum()
    {
        for (int i = 0; i < _n; i++) _hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (_n - 1));
    }

    /// <param name="levels">Filled with one level per band.</param>
    /// <param name="gainDb">Added to every band before it is placed on the scale.</param>
    /// <param name="rangeDb">How many decibels below full scale read as zero.</param>
    public void Measure(AudioSource source, double[] levels, double gainDb, double rangeDb)
    {
        source.Latest(_samples);

        for (int i = 0; i < _n; i++)
        {
            _re[i] = _samples[i] * _hann[i];
            _im[i] = 0;
        }

        Fft(_re, _im);

        int bands = levels.Length;
        double rate = source.SampleRate;
        double top = Math.Min(HighHz, rate / 2 * 0.95);
        double binHz = rate / _n;

        // синус полной амплитуды с окном Ханна даёт в своём отсчёте n/4
        double fullScale = _n / 4.0;

        for (int b = 0; b < bands; b++)
        {
            double lo = LowHz * Math.Pow(top / LowHz, (double)b / bands);
            double hi = LowHz * Math.Pow(top / LowHz, (double)(b + 1) / bands);

            int from = (int)Math.Floor(lo / binHz), to = (int)Math.Ceiling(hi / binHz);
            from = Math.Clamp(from, 1, _n / 2 - 1);
            to = Math.Clamp(to, from + 1, _n / 2);

            // у нижних полос отсчётов меньше одного, тогда берётся ближайший
            double peak = 0;
            for (int k = from; k < to; k++)
                peak = Math.Max(peak, Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]));

            double centre = Math.Sqrt(lo * hi);
            double db = 20 * Math.Log10(Math.Max(1e-12, peak / fullScale))
                        + TiltPerOctave * Math.Log2(centre / 1000)
                        + gainDb;

            levels[b] = Math.Clamp((db + rangeDb) / rangeDb, 0, 1);
        }
    }

    /// <summary>In-place radix-2 transform; the length is a power of two.</summary>
    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);

            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci;
                    double ti = re[b] * ci + im[b] * cr;

                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;

                    double next = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = next;
                }
            }
        }
    }
}
