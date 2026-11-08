# EZTool

[![Neueste Version](https://img.shields.io/github/v/release/sweeax-exe/EZTool?label=Download&color=00E5FF)](https://github.com/sweeax-exe/EZTool/releases/latest)
[![Lizenz: MIT](https://img.shields.io/badge/Lizenz-MIT-8B5CF6)](LICENSE)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-2CF5A0)

Futuristisches System-Tool für Windows 10/11 im Glas-Design: Hardware-Monitoring mit FPS, Gaming-Modus, Speicher-Cleaner, Deinstaller, Updates, Autostart, Tweaks und Tastenbelegung – kostenlos und Open Source.

## Download

1. Unter [**Releases**](https://github.com/sweeax-exe/EZTool/releases/latest) die Datei **`EZTool.exe`** herunterladen.
2. Starten – fertig. Keine Installation nötig, .NET ist bereits enthalten.

**Hinweise:**
- EZTool fragt beim Start nach Administrator-Rechten. Die braucht es für Temperatursensoren, FPS-Messung, Tiefenreinigung und Deinstallationen.
- Beim ersten Start kann Windows SmartScreen „Der Computer wurde durch Windows geschützt“ anzeigen, weil die EXE nicht kostenpflichtig signiert ist. Dann über **Weitere Informationen → Trotzdem ausführen** starten. Der komplette Quellcode liegt in diesem Repository, und jede EXE wird öffentlich von GitHub Actions gebaut.
- **Updates kommen automatisch:** EZTool prüft regelmäßig auf neue Releases. Ein Update wird per SHA-256 geprüft und installiert, danach startet EZTool neu. Das lässt sich in den Einstellungen abschalten.

**Technik:** C# · WPF · .NET 10 · [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (Sensoren) · CommunityToolkit.Mvvm · Windows-SDK (Store-Apps)

## Design

Unter Windows 11 (ab 22H2) läuft das Fenster auf echtem **Acrylic-Glas**: Der Desktop scheint verschwommen durch. Darüber liegen halbtransparente Glaskarten mit Lichtkante und langsam treibende Leucht-Nebel (Aurora). Unter Windows 10 gibt es stattdessen einen dunklen, deckenden Hintergrund. Alle Farben und Glas-Materialien stehen zentral in `Themes/Theme.xaml` (`CardFill`, `CardBorderGradient`, `GlassPanel`, `GlassEdge`).

## Module

Die Navigation ist in fünf Gruppen gegliedert:

| Gruppe | Modul | Was es kann |
|---|---|---|
| Monitor | **Dashboard** | Ringanzeigen für CPU, GPU, RAM und Systemlaufwerk, 60-Sekunden-Verlauf, Temperaturen, Kern-Auslastung, Laufwerke, Netzwerk, Systeminfos |
| | **Prozesse** | Prozesse nach Programm gruppiert (z. B. „Chrome 30×“), CPU und RAM live, mit einem Klick beenden. Windows-Kernprozesse sind geschützt |
| | **Hardware** | Alle Sensoren (oft 150+) nach Bauteil gruppiert, mit Filter und Suche |
| | **Verlauf** | Temperaturen, Auslastung und FPS der letzten 24 Stunden als Diagramm mit Fadenkreuz, Höchstwerte, CSV-Export |
| | **Datenträger** | SSD-/HDD-Gesundheit: Restlebensdauer, Temperatur, Betriebsstunden, geschriebene TB, Lese-/Schreibfehler |
| Gaming | **Gaming-Modus** | Boost-Knopf (Höchstleistung, Standby-RAM leeren, Windows Update anhalten, gewählte Apps schließen), Live-FPS, Session-Bericht mit Ø-FPS, 1 % Low und Höchsttemperaturen |
| Aufräumen | **Speicher-Cleaner** | Temp, Caches, Crash-Dumps, Browser, Papierkorb – plus Tiefenreinigung (alte Windows-Updates per DISM, `Windows.old`) |
| | **Speicher-Karte** | Laufwerk als Treemap: Kacheln proportional zur Größe, zwei Ebenen, Klick zoomt hinein, Rechtsklick zurück |
| | **Ordner-Analyse** | Große und lange ungenutzte, verwaiste und leere Ordner |
| | **Duplikate** | Doppelte Dateien, verglichen über den echten Inhalt (SHA-256) |
| Software | **Programme** | Desktop-Programme und Store-Apps deinstallieren, danach die Reste entfernen |
| | **Updates** | Verfügbare Updates aller Programme über winget, einzeln oder alle mit einem Klick |
| | **Autostart** | Run-Keys, Autostart-Ordner und geplante Aufgaben ein- und ausschalten |
| System | **Tastenbelegung** | Medienfunktionen (Titel vor/zurück, Play/Pause, Lautstärke), andere Tasten, Programme oder EZTool-Aktionen auf beliebige Tasten legen – global, auch in Spielen. Schnellstart: Bild ↑/↓ = Titel vor/zurück |
| | **Windows-Tweaks** | 14 Schalter für Werbung, Datenschutz, Explorer und Gaming (Mausbeschleunigung, Game DVR …), jederzeit rückgängig, mit Wiederherstellungspunkt |
| | **Einstellungen** | Mit Windows starten, Hintergrundbetrieb, Sensor-Treiber, Alarme, Overlay |

## Hintergrundbetrieb

- **Tray-Symbol:** Das X minimiert in den Infobereich. Der Tooltip zeigt CPU/GPU/RAM live, das Rechtsklick-Menü öffnet Module, schaltet das Overlay und beendet EZTool.
- **Mit Windows starten:** legt eine geplante Aufgabe mit höchsten Rechten an. Ein normaler Run-Eintrag würde an der UAC scheitern, weil EZTool Admin-Rechte braucht. Die Aufgabe startet EZTool unsichtbar mit `--tray`.
- **Alarme:** Eine Windows-Benachrichtigung erscheint, wenn CPU oder GPU über dem Grenzwert liegen (erst nach 5 Messungen in Folge, dann höchstens alle 10 Minuten) oder ein Laufwerk fast voll ist. Ein Klick auf die Meldung öffnet das passende Modul.
- **Overlay:** kleine Live-Anzeige über allen Fenstern mit CPU, GPU, RAM und **FPS**. Klicks gehen hindurch. Funktioniert über randlosen Spielen, nicht in exklusivem Vollbild.
- **Sensor-Log:** alle 5 Sekunden, tageweise als CSV unter `%LOCALAPPDATA%\EZTool\Logs`. Wird nach 30 Tagen automatisch gelöscht.

## Tastenbelegung

Ein globaler Low-Level-Tastatur-Hook (`WH_KEYBOARD_LL`) fängt die belegte Taste ab. Ihre normale Funktion entfällt, und stattdessen sendet EZTool per `SendInput` die gewünschte Aktion, z. B. die Medientaste „Nächster Titel“. Weitere Details:

- **Kein Leerlauf:** Der Hook ist nur aktiv, wenn mindestens eine Belegung eingeschaltet ist.
- **Keine Endlosschleife:** Selbst gesendete Tasten werden ignoriert.
- **Normale Rechte:** Programme, die per Taste gestartet werden, laufen über den Explorer mit normalen Rechten, nicht als Administrator.
- **Gespeichert** werden die Belegungen in `settings.json` (`KeyBindings`).

## FPS-Messung

EZTool liest die DirectX-„Present“-Ereignisse über Windows-ETW, also dieselbe Quelle, die auch PresentMon nutzt. Gemessen wird das Fenster im Vordergrund. Das funktioniert mit DirectX 9 bis 12. Vulkan- und OpenGL-Spiele werden nur erfasst, wenn sie über DXGI präsentieren. Die Messung braucht Administrator-Rechte und läuft nur, solange das Overlay, die Gaming-Seite oder der Gaming-Modus aktiv ist. Session-Berichte liegen unter `%LOCALAPPDATA%\EZTool\Sessions`.
- **Nur eine Instanz:** Ein zweiter Start holt das laufende Fenster nach vorn.

## Sicherheitsprinzipien

- **Cleaner:** nur bekannte Cache- und Temp-Orte. Temp-Dateien erst ab einem Alter von 24 Stunden, gesperrte Dateien werden übersprungen, Junctions nie verfolgt. Bei Browsern wird **nur** der Cache gelöscht. Die Tiefenreinigung nutzt ausschließlich die Windows-eigenen Werkzeuge (DISM, Datenträgerbereinigung).
- **Programme:** Es startet immer das Deinstallationsprogramm des Herstellers (Store-Apps über die offizielle PackageManager-API). Ordner anderer installierter Programme werden nie als Reste vorgeschlagen. Registry-Schlüssel werden vorher als `.reg` gesichert. Systemrelevante Store-Apps (Store, App Installer, Xbox-Identität …) lassen sich nicht entfernen.
- **Ordner & Duplikate:** Löschen bedeutet immer *Papierkorb*. System-, Programm-, Punkt- und Build-Ordner sind ausgeschlossen. Cloud-Dateien (OneDrive-Platzhalter) werden nie gelesen. Duplikate sind nur bei Medien vorausgewählt, und in jeder Gruppe bleibt mindestens ein Exemplar.
- **Autostart:** Ausschalten nutzt denselben Mechanismus wie der Task-Manager (`StartupApproved`), der Eintrag bleibt also erhalten. Vor dem dauerhaften Entfernen wird eine JSON- bzw. XML-Sicherung abgelegt.
- **Prozesse:** Windows-Kernprozesse (csrss, lsass, svchost, dwm …) können nicht beendet werden.

## Starten

```bash
dotnet run --project src/EZTool
```

Die App fordert Administrator-Rechte an (UAC). Die braucht sie für Temperatursensoren, die Tiefenreinigung, Deinstallationen und den Autostart.

Startparameter:
- `--page=<modul>` öffnet ein Modul direkt (`dashboard`, `processes`, `hardware`, `history`, `disks`, `gaming`, `cleaner`, `storagemap`, `folders`, `duplicates`, `programs`, `updates`, `autostart`, `tweaks`, `settings`).
- `--tray` startet unsichtbar im Infobereich.
- Umgebungsvariable `EZTOOL_DEV=1`: eigene Instanz für Tests, die eine laufende EZTool-Instanz nicht stört.

### CPU-Temperaturen (PawnIO)

LibreHardwareMonitor liest CPU-Temperatur, -Takt und -Leistung über den kostenlosen Open-Source-Treiber **PawnIO**. Fehlt er, bietet EZTool beim ersten Start (und jederzeit im Dashboard bzw. in den Einstellungen) die Installation an:

1. Der offizielle Installer wird von `github.com/namazso/PawnIO.Setup` geladen (neueste Version laut GitHub-API).
2. Seine Authenticode-Signatur wird mit `WinVerifyTrust` geprüft. Ohne gültige Signatur wird nichts ausgeführt.
3. Der Installer startet mit seinem eigenen Dialog (`-install`, genau wie bei LibreHardwareMonitor).
4. Die Sensoren werden ohne Neustart von EZTool neu initialisiert.

## Neue Version veröffentlichen

Releases baut GitHub Actions automatisch (`.github/workflows/release.yml`). Für eine neue Version:

```bash
git tag v1.0.1
```

```bash
git push origin v1.0.1
```

Der Workflow baut eine eigenständige Einzeldatei. Die Versionsnummer kommt aus dem Tag. Veröffentlicht werden `EZTool.exe`, `EZTool-win-x64.zip` und `SHA256SUMS.txt` als Release. Alle installierten EZTool-Versionen finden das Update danach von selbst.

Lokal dieselbe EXE bauen:

```bash
dotnet publish src/EZTool -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

## Erweitern – neues Modul in 2 Minuten

1. Ordner `src/EZTool/Modules/MeinModul/` anlegen.
2. Eine Klasse schreiben, die `IModule` implementiert:

```csharp
public sealed class MeinModul : IModule
{
    public string Id => "meinmodul";
    public string Title => "Mein Modul";
    public string Subtitle => "Beschreibung oben auf der Seite.";
    public string Icon => "";   // Glyph aus "Segoe Fluent Icons"
    public int Order => 60;            // Position in der Navigation

    public FrameworkElement CreateView() => new MeinModulView { DataContext = new MeinModulViewModel() };
}
```

Das Modul wird automatisch gefunden und in der Navigation angezeigt. Implementiert das ViewModel `IActivatable`, wird `OnActivated()` bei jedem Öffnen der Seite aufgerufen.

**Bausteine zum Wiederverwenden:**
- **Controls** (in `Controls/`): `RadialGauge`, `Sparkline`, `GaugeCard`
- **Styles** (in `Themes/Theme.xaml`): `Card`, `PrimaryButton`, `DangerButton`, `GhostButton`, `SegmentButton`, `Toggle`, `NeonBar`, `SearchBox`
- **Dienste:** `NeonDialog.Show(...)` für Rückfragen, `TrayService.Instance.Notify(...)` für Benachrichtigungen, `SettingsService` für eigene Einstellungen

## Projektstruktur

```
src/EZTool/
├── Assets/         App-Icon
├── Core/           IModule, Formatierung, Konverter, Native (Papierkorb, Icons, Acrylic)
├── Controls/       RadialGauge, Sparkline, GaugeCard
├── Themes/         Farben, Glas-Materialien, Fonts, alle Control-Styles
├── Services/       Hardware, SystemInfo, Settings, Tray, Alerts, PawnIo, StartupTask, Cleaner (+ DeepClean),
│                   Program, StoreApp, Autostart, Process, Folder, Duplicate
├── Dialogs/        NeonDialog, LeftoverDialog, OverlayWindow
└── Modules/        Dashboard, Processes, Hardware, Cleaner, Programs, Autostart, Folders, Duplicates, Settings
```

## Ideen für später

- Autostart: Windows-Dienste und Startzeit-Messung pro Eintrag
- Prozess-Details: Verlauf pro Prozess, Netzwerk- und Datenträgerlast
- Gaming-Modus automatisch starten, wenn ein Spiel erkannt wird
- Akzentfarben-Wahl und Sprache (Englisch)
- Treiber-Update-Check (GPU-Treiber)
