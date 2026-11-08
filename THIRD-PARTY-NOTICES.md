# Verwendete Open-Source-Bibliotheken

EZTool selbst steht unter der MIT-Lizenz (siehe `LICENSE`). Es nutzt diese Bibliotheken, die jeweils unter ihrer eigenen Lizenz stehen:

| Bibliothek | Zweck | Lizenz |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | Hardware-Sensoren | MPL-2.0 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM-Grundlagen | MIT |
| [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) | FPS-Messung über ETW | MIT |
| [System.Management](https://github.com/dotnet/runtime) | WMI-Abfragen | MIT |
| [System.ServiceProcess.ServiceController](https://github.com/dotnet/runtime) | Windows-Dienste | MIT |
| [.NET Runtime / WPF / WinForms](https://github.com/dotnet) | Laufzeit (in der EXE enthalten) | MIT |

**PawnIO** (Sensor-Treiber) ist nicht in EZTool enthalten. Auf Wunsch lädt EZTool den offiziellen, signierten Installer von [github.com/namazso/PawnIO.Setup](https://github.com/namazso/PawnIO.Setup).
