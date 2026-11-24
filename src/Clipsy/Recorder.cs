using System.Collections.Concurrent;
using System.Diagnostics;

namespace Clipsy;

public enum RecorderState { Off, Starting, Recording, Error }

/// <summary>
/// Replay-Buffer: ffmpeg nimmt dauerhaft in 2-Sekunden-Segmente (Ringpuffer auf der Platte) auf.
/// Beim Clip werden die letzten N Sekunden ohne Neu-Encoding zu einer MP4 zusammengefügt.
/// Alle öffentlichen Methoden außer SaveClipAsync laufen auf dem UI-Thread.
/// </summary>
internal sealed class Recorder : IDisposable
{
    readonly string ffmpeg;
    readonly string bufferRoot;
    AppSettings settings;
    volatile string? encoder;
    Session? session;
    CaptureTarget? target;
    bool useDesktopDuplication;
    bool windowCaptureWorked;
    DateTime ddaSinceUtc;
    int quickFailures;
    DateTime nextStartUtc = DateTime.MinValue;
    DateTime lastDeviceCheckUtc = DateTime.MinValue;

    public Recorder(string ffmpegPath, AppSettings settings)
    {
        ffmpeg = ffmpegPath;
        this.settings = settings;
        bufferRoot = Path.Combine(Log.Dir, "buffer");
        CleanBuffer();
    }

    public bool Enabled { get; private set; }
    public RecorderState State { get; private set; } = RecorderState.Off;
    public string? Encoder => encoder;
    public CaptureTarget? Target => target;
    public string Mode => session?.Source.Mode switch
    {
        CaptureMode.Window => "Spielfenster (ohne Overlays)",
        CaptureMode.Monitor => "Bildschirm",
        CaptureMode.DesktopDuplication => "Bildschirm (Fallback)",
        _ => "–",
    };

    public void SetEncoder(string name)
    {
        encoder = name;
        Log.Write($"Encoder: {name}");
    }

    public void SetEnabled(bool on)
    {
        if (Enabled == on) return;
        Enabled = on;
        Log.Write(on ? "Aufnahme aktiviert" : "Aufnahme deaktiviert");
        quickFailures = 0;
        nextStartUtc = DateTime.MinValue;
        if (!on) StopSession();
    }

    public void SetTarget(CaptureTarget t)
    {
        if (target is not null && target.Hwnd == t.Hwnd && target.Monitor == t.Monitor) return;
        target = t;
        useDesktopDuplication = false;
        quickFailures = 0;
        nextStartUtc = DateTime.MinValue;
        Log.Write($"Ziel: {(t.IsWindow ? "Fenster" : "Monitor")} '{t.Name}'");
        StopSession();
    }

    public void ApplySettings(AppSettings s)
    {
        bool restart = s.Height != settings.Height || s.Fps != settings.Fps || s.ClipSeconds != settings.ClipSeconds ||
                       s.AudioOutput != settings.AudioOutput || s.Microphone != settings.Microphone;
        settings = s;
        if (restart) { StopSession(); nextStartUtc = DateTime.MinValue; }
    }

    /// <summary>Watchdog, ca. jede Sekunde vom UI-Timer aufgerufen.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (!Enabled) { State = RecorderState.Off; return; }

        if (session is { } s)
        {
            if (!s.Process.HasExited)
            {
                if (s.HasSegments())
                {
                    State = RecorderState.Recording;
                    if (s.Source.Mode == CaptureMode.Window) windowCaptureWorked = true;
                    if (now - s.StartedUtc > TimeSpan.FromSeconds(20)) quickFailures = 0;

                    // USB-Headset ein-/ausgesteckt o. Ä. → auf das neue Gerät umschalten
                    if (now - lastDeviceCheckUtc > TimeSpan.FromSeconds(5))
                    {
                        lastDeviceCheckUtc = now;
                        var (outId, micId) = ResolveDevices();
                        if (outId != s.OutputDeviceId || micId != s.MicDeviceId)
                        {
                            Log.Write("Audiogerät gewechselt – Aufnahme wird neu gestartet");
                            StopSession();
                            nextStartUtc = DateTime.MinValue;
                            return;
                        }
                    }

                    // Nach Fallback regelmäßig wieder sauberes Fenster-Capture versuchen
                    if (useDesktopDuplication && windowCaptureWorked && target is { IsWindow: true } t &&
                        !Native.IsIconic(t.Hwnd) && now - ddaSinceUtc > TimeSpan.FromMinutes(2))
                    {
                        useDesktopDuplication = false;
                        StopSession();
                    }
                }
                else if (now - s.StartedUtc > TimeSpan.FromSeconds(15))
                {
                    Log.Write("ffmpeg liefert keine Segmente – Neustart");
                    try { s.Process.Kill(true); } catch { }
                }
                else State = RecorderState.Starting;
                return;
            }

            var lived = now - s.StartedUtc;
            int code = -1;
            try { code = s.Process.ExitCode; } catch { }
            Log.Write($"ffmpeg beendet (Code {code}) nach {lived.TotalSeconds:0}s: {s.ErrorTail()}");
            session = null;
            Task.Run(s.Dispose);

            if (lived < TimeSpan.FromSeconds(20))
            {
                quickFailures++;
                if (quickFailures >= 2 && !useDesktopDuplication)
                {
                    useDesktopDuplication = true;
                    ddaSinceUtc = now;
                    quickFailures = 0;
                    Log.Write("Fenster-Capture klappt nicht – Fallback auf Desktop Duplication");
                }
            }
            nextStartUtc = now + TimeSpan.FromSeconds(Math.Min(2 * Math.Max(quickFailures, 1), 15));
            State = RecorderState.Error;
        }

        if (encoder is null || target is null) { State = RecorderState.Starting; return; }
        if (now < nextStartUtc) return;
        StartSession(target);
    }

    void StartSession(CaptureTarget t)
    {
        var dir = Path.Combine(bufferRoot, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
        var pumps = new List<AudioPump>();
        Process? p = null;
        try
        {
            Directory.CreateDirectory(dir);
            var (outId, micId) = ResolveDevices();
            lastDeviceCheckUtc = DateTime.UtcNow;
            if (settings.AudioOutput != AudioDevices.Off) pumps.Add(new AudioPump(outId, loopback: true));
            if (micId != null) pumps.Add(new AudioPump(micId, loopback: false));

            var src = useDesktopDuplication ? new CaptureSource(CaptureMode.DesktopDuplication, 0, t.OutputIndex)
                    : t.IsWindow ? new CaptureSource(CaptureMode.Window, t.Hwnd.ToInt64(), t.OutputIndex)
                    : new CaptureSource(CaptureMode.Monitor, t.Monitor.ToInt64(), t.OutputIndex);

            var args = FfmpegArgs.Record(src, pumps.Select(x => x.Input).ToList(), encoder!, settings.Height, settings.Fps,
                                         settings.ClipSeconds, Path.Combine(dir, "seg%03d.ts"));
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var errors = new ConcurrentQueue<string>();
            p = new Process { StartInfo = psi };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                errors.Enqueue(e.Data);
                while (errors.Count > 40 && errors.TryDequeue(out string? dropped)) { }
            };
            p.Start();
            Native.KillWithApp(p);
            p.BeginErrorReadLine();
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            foreach (var pump in pumps) pump.Start();

            session = new Session(p, dir, pumps, errors, DateTime.UtcNow, src) { OutputDeviceId = outId, MicDeviceId = micId };
            State = RecorderState.Starting;
            Log.Write($"Aufnahme gestartet: {src.Mode}, {settings.Height}p{settings.Fps}, {encoder}, Tonquellen={pumps.Count}");
        }
        catch (Exception ex)
        {
            Log.Write($"Start fehlgeschlagen: {ex.Message}");
            foreach (var pump in pumps) pump.Dispose();
            try { p?.Kill(true); } catch { }
            p?.Dispose();
            quickFailures++;
            nextStartUtc = DateTime.UtcNow.AddSeconds(5);
            State = RecorderState.Error;
        }
    }

    (string? outId, string? micId) ResolveDevices() =>
        (settings.AudioOutput == AudioDevices.Off ? null : AudioDevices.Resolve(settings.AudioOutput, NAudio.CoreAudioApi.DataFlow.Render),
         AudioDevices.Resolve(settings.Microphone, NAudio.CoreAudioApi.DataFlow.Capture));

    void StopSession()
    {
        if (session is null) return;
        var s = session;
        session = null;
        Task.Run(s.Dispose); // ffmpeg sauber beenden, ohne die UI zu blockieren
    }

    /// <summary>Speichert die letzten Sekunden als MP4. Gibt den Pfad zurück oder null.</summary>
    public async Task<string?> SaveClipAsync()
    {
        var s = session;
        if (s is null || s.Process.HasExited) return null;
        var clipSeconds = settings.ClipSeconds;
        var folder = settings.OutputFolder;

        var files = new DirectoryInfo(s.Dir).GetFiles("seg*.ts").Select(f => (f.Name, f.LastWriteTimeUtc));
        var selected = SegmentSelector.Select(files, DateTime.UtcNow, clipSeconds, FfmpegArgs.SegmentSeconds);
        if (selected.Count == 0) return null;

        Directory.CreateDirectory(folder);
        var name = $"{SafeName(target?.Name)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
        var outPath = Path.Combine(folder, name);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = s.Dir,
            RedirectStandardError = true,
        };
        foreach (var a in FfmpegArgs.Concat(selected, outPath)) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        Native.KillWithApp(p);
        var err = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }

        if (p.HasExited && p.ExitCode == 0 && File.Exists(outPath))
        {
            Log.Write($"Clip gespeichert: {name}");
            return outPath;
        }
        Log.Write($"Clip fehlgeschlagen: {(await err).Trim()}");
        try { File.Delete(outPath); } catch { }
        return null;
    }

    static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Clip";
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !bad.Contains(c)).ToArray()).Trim();
        return clean.Length == 0 ? "Clip" : clean[..Math.Min(clean.Length, 40)];
    }

    void CleanBuffer()
    {
        try
        {
            if (!Directory.Exists(bufferRoot)) return;
            foreach (var d in Directory.GetDirectories(bufferRoot))
                try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    public void Dispose()
    {
        session?.Dispose();
        session = null;
        CleanBuffer();
    }

    sealed class Session(Process process, string dir, List<AudioPump> audio,
                         ConcurrentQueue<string> errors, DateTime startedUtc, CaptureSource source) : IDisposable
    {
        bool hasSegments;
        public string? OutputDeviceId { get; init; }
        public string? MicDeviceId { get; init; }
        public Process Process { get; } = process;
        public string Dir { get; } = dir;
        public DateTime StartedUtc { get; } = startedUtc;
        public CaptureSource Source { get; } = source;

        public bool HasSegments()
        {
            if (hasSegments) return true;
            try { hasSegments = Directory.EnumerateFiles(Dir, "seg*.ts").Skip(1).Any(); } catch { }
            return hasSegments;
        }

        public string ErrorTail() => string.Join(" | ", errors.TakeLast(5));

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    try { Process.StandardInput.Write('q'); Process.StandardInput.Flush(); } catch { }
                    if (!Process.WaitForExit(4000)) Process.Kill(true);
                }
            }
            catch { }
            foreach (var pump in audio) pump.Dispose();
            try { Process.Dispose(); } catch { }
            try { Directory.Delete(Dir, true); } catch { }
        }
    }
}
