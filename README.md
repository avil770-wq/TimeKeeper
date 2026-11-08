# TimeKeeper

Windows desktop widget for daily time reporting in Dynamics 365 (BizAid).
A small quarter-circle sits in a screen corner; the timer always starts from a task.

## Layout

- `index.html` – HTML prototype (design reference)
- `src/TimeKeeper` – WPF app, .NET 8

## Build

Requires the .NET 8 SDK (or Visual Studio 2022 17.8+).

```
dotnet build TimeKeeper.sln
dotnet run --project src/TimeKeeper
```

## Stage 1 – skeleton (done)

- Corner window: always on top, click-through outside the quarter, grows on hover,
  drag to move, snaps to the nearest corner of the monitor it's dropped on.
  Falls back to the primary monitor if the saved one is disconnected (saved value kept).
- Tray icon: left click = same as the corner, right click = settings / hide / exit.
- Global hotkey (default Ctrl+Alt+T) via `RegisterHotKey`; a taken combo shows a message
  and the widget keeps working without it.
- Settings in `%AppData%\TimeKeeper\settings.json`, saved on every change.
  A corrupt file is backed up and replaced with defaults.
- Start with Windows (HKCU Run key), on by default.
- Light / Dark / follow Windows, and High Contrast uses system colours.
- Logs: `%AppData%\TimeKeeper\logs`.

The timer menu is a placeholder for now.

## Next

1. Sign-in (WebView2 Dynamics session) and Dataverse reads: clients → projects → tasks
2. Timer, stop dialog, local queue for unsent entries
3. Reminders (toast notifications)
