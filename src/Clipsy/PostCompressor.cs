using System.Diagnostics;

namespace Clipsy;

/// <summary>
/// Rechnet gespeicherte Clips im Hintergrund zu AV1 um (SVT-AV1, CPU) – gemessen ca. 60 % kleiner
/// bei nicht sichtbarem Qualitätsunterschied. Läuft mit niedrigster Priorität und nur, solange kein
/// Spiel im Vollbild läuft; startet ein Spiel, wird abgebrochen und später neu begonnen.
/// Das Original wird erst ersetzt, wenn die AV1-Datei fertig und kleiner ist.
/// </summary>
internal sealed class PostCompressor(string ffmpeg) : IDisposable
{
    const string TempPrefix = "~clipsy-";
    readonly LinkedList<string> queue = new();
    Process? current;
    string? currentFile, currentTemp;
    DateTime nextTryUtc = DateTime.MinValue;

    public int Pending => queue.Count + (current is null ? 0 : 1);

    public void Enqueue(string clipPath) => queue.AddLast(clipPath);

    /// <summary>Reste eines abgebrochenen Laufs (z. B. PC aus während der Umrechnung) entfernen.</summary>
    public static void CleanLeftovers(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.GetFiles(folder, TempPrefix + "*.mp4"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>Vom UI-Timer jede Sekunde aufgerufen.</summary>
    public void Tick(bool gameRunning)
    {
        if (current is not null)
        {
            if (gameRunning)
            {
                Log.Write("Spiel gestartet – Nachkomprimieren pausiert");
                Abort(requeue: true);
                return;
            }
            if (!current.HasExited)
            {
                HideTemp();
                return;
            }
            Finish();
        }

        if (gameRunning || queue.Count == 0 || DateTime.UtcNow < nextTryUtc) return;
        var next = queue.First!.Value;
        queue.RemoveFirst();
        Start(next);
    }

    void Start(string file)
    {
        if (!File.Exists(file)) return;
        currentFile = file;
        currentTemp = Path.Combine(Path.GetDirectoryName(file)!, TempPrefix + Path.GetFileName(file));
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in FfmpegArgs.PostCompress(file, currentTemp)) psi.ArgumentList.Add(a);
            current = Process.Start(psi)!;
            Native.KillWithApp(current);
            current.ErrorDataReceived += (_, _) => { }; // Ausgabe verwerfen, damit ffmpeg nie blockiert
            current.BeginErrorReadLine();
            try { current.PriorityClass = ProcessPriorityClass.Idle; } catch { }
            Log.Write($"Nachkomprimieren: {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Log.Write($"Nachkomprimieren nicht startbar: {ex.Message}");
            current = null;
        }
    }

    void Finish()
    {
        var p = current!;
        current = null;
        int code = -1;
        try { code = p.ExitCode; } catch { }
        p.Dispose();

        var file = currentFile!;
        var temp = currentTemp!;
        try
        {
            if (code != 0 || !File.Exists(temp) || !File.Exists(file))
            {
                Log.Write($"Nachkomprimieren fehlgeschlagen (Code {code}): {Path.GetFileName(file)}");
                TryDelete(temp);
                return;
            }
            long before = new FileInfo(file).Length, after = new FileInfo(temp).Length;
            if (after >= before * 0.95)
            {
                TryDelete(temp); // lohnt sich nicht → Original behalten
                return;
            }
            File.SetAttributes(temp, FileAttributes.Normal);
            File.Move(temp, file, overwrite: true);
            Log.Write($"Nachkomprimiert: {Path.GetFileName(file)} {before / 1048576.0:0.0} MB → {after / 1048576.0:0.0} MB");
        }
        catch (IOException)
        {
            // Datei gerade geöffnet (z. B. im Player) → später nochmal
            TryDelete(temp);
            queue.AddLast(file);
            nextTryUtc = DateTime.UtcNow.AddMinutes(1);
        }
        catch (Exception ex)
        {
            Log.Write($"Nachkomprimieren: {ex.Message}");
            TryDelete(temp);
        }
    }

    void Abort(bool requeue)
    {
        if (current is null) return;
        try { current.Kill(true); current.WaitForExit(3000); } catch { }
        current.Dispose();
        current = null;
        if (currentTemp is not null) TryDelete(currentTemp);
        if (requeue && currentFile is not null) queue.AddFirst(currentFile);
    }

    void HideTemp()
    {
        try
        {
            if (currentTemp is not null && File.Exists(currentTemp))
                File.SetAttributes(currentTemp, File.GetAttributes(currentTemp) | FileAttributes.Hidden);
        }
        catch { }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch { }
    }

    public void Dispose() => Abort(requeue: false);
}
