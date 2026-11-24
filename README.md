# Clipsy

Leichtgewichtiger Game-Clipper für Windows 10/11. Läuft unsichtbar im Hintergrund und speichert
auf Tastendruck die letzten Sekunden als MP4.

## Funktionen

- **F10** speichert einen Clip (Standard: letzte 30 Sekunden, 1080p60)
- **Ohne Overlays:** nimmt direkt das Spielfenster auf (Windows Graphics Capture), nicht den Bildschirm –
  Fenster darüber (Discord-Overlay, Benachrichtigungen, Clipsys Status-Punkt …) landen nicht im Clip
- **Status-Punkt:** grün = Clippen an, rot = aus, orange = startet. Ist selbst nie in Aufnahmen sichtbar
- **Immer an:** startet mit Windows und ist nach jedem Neustart automatisch wieder aktiv –
  auch wenn man es vorher deaktiviert hat
- **⚙ Einstellungen** (Tray-Icon → Doppelklick): Qualität 720p / 1080p, 30 / 60 FPS, Clip-Länge,
  Tasten ändern, Speicherort, Spiel-Sound, Status-Punkt, Autostart
- **Clip-Ordner öffnen** direkt aus Tray-Menü oder Einstellungen
- Hardware-Encoding automatisch (NVIDIA NVENC → AMD AMF → Intel QSV → CPU)

## Installation

1. Unter **Releases** `Clipsy-win64.zip` herunterladen
2. Entpacken, z. B. nach `C:\Programme\Clipsy` oder `%LOCALAPPDATA%\Clipsy-App`
3. `Clipsy.exe` starten – fertig (Autostart richtet sich selbst ein)

Clips landen standardmäßig in `Videos\Clipsy`.

## Tasten (Standard)

| Taste | Aktion |
|---|---|
| `F10` | Clip speichern |
| `Ctrl+Shift+F10` | Clippen an/aus |

## Wichtig zu Overlays

- **Separate Overlay-Fenster** (aktuelles Discord-Overlay, Xbox Game Bar, Benachrichtigungen) → werden ignoriert.
- **In-Game-Overlays, die direkt ins Spielbild gezeichnet werden** (z. B. Steam-Overlay, ältere Overlays,
  FPS-Zähler von Afterburner/RTSS) sind Teil des Spielbilds. Die lassen sich ohne DLL-Injection nicht
  herausfiltern – und Injection riskiert Anti-Cheat-Banns. Diese Overlays daher im jeweiligen Programm abschalten.
- Spiele im **exklusiven Vollbild** zeigen den Status-Punkt evtl. nicht an (das Clippen funktioniert trotzdem).
  Empfehlung: „Randloses Fenster“ / „Borderless“ im Spiel nutzen.
- Wird ein Spiel nicht als Vollbild erkannt (z. B. kleines Fenster), nimmt Clipsy den Bildschirm auf.

## Technik

- .NET 10 WinForms (Tray-App), ffmpeg (`gfxcapture` + Hardware-Encoder) als Aufnahme-Engine
- Ringpuffer aus 2-s-Segmenten in `%LOCALAPPDATA%\Clipsy\buffer`, Clip = verlustfreies Zusammenfügen
- Spiel-Sound per WASAPI-Loopback (NAudio)
- Einstellungen: `%APPDATA%\Clipsy\settings.json`, Log: `%LOCALAPPDATA%\Clipsy\clipsy.log`

## Bauen

Wird automatisch über GitHub Actions gebaut (`.github/workflows/build.yml`). Lokal:

```
dotnet test tests/Clipsy.Tests/Clipsy.Tests.csproj
dotnet publish src/Clipsy/Clipsy.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out/Clipsy
```

Danach `ffmpeg.exe` (Build mit `gfxcapture`, z. B. BtbN „master“) nach `out/Clipsy/ffmpeg/` legen.

## Lizenz

MIT. Das gebündelte ffmpeg steht unter GPL (Lizenz liegt im Ordner `ffmpeg/`).
