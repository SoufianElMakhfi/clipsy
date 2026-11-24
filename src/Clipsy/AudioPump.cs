using System.Diagnostics;
using System.IO.Pipes;
using NAudio.Wave;

namespace Clipsy;

/// <summary>
/// Spiel-/System-Sound (WASAPI-Loopback) → Named Pipe → ffmpeg.
/// Taktet in Echtzeit und füllt Stille auf (Loopback liefert bei Stille keine Daten),
/// damit Ton und Bild synchron bleiben.
/// </summary>
internal sealed class AudioPump : IDisposable
{
    readonly NamedPipeServerStream pipe;
    readonly WasapiLoopbackCapture? capture;
    readonly ByteFifo fifo;
    readonly int rate, blockAlign;
    readonly CancellationTokenSource cts = new();
    long lastDataTicks;
    Thread? thread;

    public AudioInput Input { get; }

    public AudioPump()
    {
        var name = "clipsy_audio_" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                                         PipeOptions.Asynchronous, 0, 1 << 16);
        string fmt = "f32le";
        int channels = 2;
        rate = 48000;
        blockAlign = 8;
        try
        {
            capture = new WasapiLoopbackCapture();
            var wf = capture.WaveFormat;
            rate = wf.SampleRate;
            channels = wf.Channels;
            blockAlign = wf.BlockAlign;
            fmt = wf.BitsPerSample switch
            {
                16 => "s16le",
                24 => "s24le",
                _ => wf.Encoding == WaveFormatEncoding.Pcm ? "s32le" : "f32le",
            };
            capture.DataAvailable += (_, e) =>
            {
                fifo!.Write(e.Buffer, 0, e.BytesRecorded);
                Interlocked.Exchange(ref lastDataTicks, Stopwatch.GetTimestamp());
            };
        }
        catch (Exception ex)
        {
            Log.Write($"Audio-Loopback nicht verfügbar, nehme Stille auf: {ex.Message}");
            capture?.Dispose();
            capture = null;
        }
        fifo = new ByteFifo(rate * blockAlign * 2);
        Input = new AudioInput($@"\\.\pipe\{name}", fmt, rate, channels);
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "ClipsyAudio", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    void Run()
    {
        try
        {
            pipe.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult();
            try { capture?.StartRecording(); }
            catch (Exception ex) { Log.Write($"Audio-Start fehlgeschlagen: {ex.Message}"); }

            var sw = Stopwatch.StartNew();
            long written = 0;
            var buf = new byte[rate * blockAlign];
            while (!cts.IsCancellationRequested)
            {
                Thread.Sleep(10);
                long due = (long)(sw.Elapsed.TotalSeconds * rate) - written;
                if (due <= 0) continue;

                int frames = (int)Math.Min(due, rate);
                int fromFifo = Math.Min(fifo.Count / blockAlign, frames);
                long sinceData = Stopwatch.GetTimestamp() - Interlocked.Read(ref lastDataTicks);
                bool silent = sinceData > Stopwatch.Frequency / 20;   // > 50 ms keine Daten → Stille
                bool behind = due > rate / 10;                        // > 100 ms Rückstand → auffüllen
                int zeros = silent || behind ? frames - fromFifo : 0;

                if (fromFifo > 0)
                {
                    int n = fifo.Read(buf, 0, fromFifo * blockAlign);
                    pipe.Write(buf, 0, n);
                }
                if (zeros > 0)
                {
                    Array.Clear(buf, 0, zeros * blockAlign);
                    pipe.Write(buf, 0, zeros * blockAlign);
                }
                written += fromFifo + zeros;

                // Drift-Schutz: nie mehr als 200 ms puffern
                int left = fifo.Count / blockAlign;
                if (left > rate / 5) fifo.Discard((left - rate / 20) * blockAlign);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }               // ffmpeg beendet → Pipe zu
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Write($"Audio-Fehler: {ex.Message}"); }
    }

    public void Dispose()
    {
        cts.Cancel();
        try { capture?.StopRecording(); } catch { }
        try { capture?.Dispose(); } catch { }
        try { pipe.Dispose(); } catch { }
        thread?.Join(1000);
    }
}

/// <summary>Thread-sicherer Ringpuffer für Bytes; bei Überlauf fliegen die ältesten Daten raus.</summary>
public sealed class ByteFifo(int capacity)
{
    readonly byte[] buf = new byte[capacity];
    readonly object gate = new();
    int head, count;

    public int Count { get { lock (gate) return count; } }

    public void Write(byte[] src, int offset, int length)
    {
        lock (gate)
        {
            if (length > buf.Length) { offset += length - buf.Length; length = buf.Length; }
            int overflow = count + length - buf.Length;
            if (overflow > 0) { head = (head + overflow) % buf.Length; count -= overflow; }
            int tail = (head + count) % buf.Length;
            int first = Math.Min(length, buf.Length - tail);
            Buffer.BlockCopy(src, offset, buf, tail, first);
            Buffer.BlockCopy(src, offset + first, buf, 0, length - first);
            count += length;
        }
    }

    public int Read(byte[] dst, int offset, int length)
    {
        lock (gate)
        {
            length = Math.Min(length, count);
            int first = Math.Min(length, buf.Length - head);
            Buffer.BlockCopy(buf, head, dst, offset, first);
            Buffer.BlockCopy(buf, 0, dst, offset + first, length - first);
            head = (head + length) % buf.Length;
            count -= length;
            return length;
        }
    }

    public void Discard(int length)
    {
        lock (gate)
        {
            length = Math.Min(length, count);
            head = (head + length) % buf.Length;
            count -= length;
        }
    }
}
