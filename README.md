# claude-code-notifier

Taskbar-flash notifications for Claude Code on Windows — the same "keep blinking
until you come back" behaviour WeChat uses.

When Claude Code finishes a response, the Windows Terminal taskbar button flashes
until you switch back to it. If you are already looking at the terminal, nothing
happens.

```
~/.claude/notify-taskbar.exe    ← 12 KB, ~110 ms, no runtime dependencies
```

---

## Why this needs to exist

Claude Code already has a notification system, and Windows Terminal already
flashes the taskbar on a bell. So the obvious approach is:

```jsonc
// ~/.claude/settings.json
{ "preferredNotifChannel": "terminal_bell" }
```

```jsonc
// Windows Terminal settings.json
"profiles": { "defaults": { "bellStyle": ["audible", "taskbar"] } }
```

That works — but **only for some events**. Reading the notification logic out of
the Claude Code bundle (`claude.exe`, v2.1.269) shows the bell fires on:

| Trigger | When |
|---|---|
| tool completion | a tool call that ran for **more than 5 s** finishes |
| `idle_prompt` | "Claude is waiting for your input" |
| `input needed` | "Claude needs your permission" |

The relevant code:

```js
var RL = 500, nS = 5000;
function IEe() {
  let l = rn(),          // "should notify" (terminal unfocused)
      p = AC(),          // notification channel
      h = et().host;
  return re(() => {
    if (!l) return;
    let y = bq.of(h), R = Date.now();
    if (R - y.lastBellAt < RL) return;   // 500 ms debounce
    y.lastBellAt = R;
    p.notifyBell();
  }, [l, p, h])
}
// ...
E(() => {
  if (p) b.current ??= Date.now();
  else if (b.current !== null) {
    if (Date.now() - b.current > nS) k();   // only if the tool ran > 5 s
    b.current = null
  }
}, [p, k])
```

**A normal turn ending does not ring the bell.** That is the gap this fills.

`preferredNotifChannel: "terminal_bell"` is still worth setting — it covers
permission prompts and idle waits for free. This tool covers turn completion.

---

## How it works

The hard part is not the flashing, it is **finding the right window**.

`FlashWindowEx` is trivial:

```csharp
f.dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG;  // caption + taskbar button,
                                            // keep flashing until foregrounded
```

`FLASHW_TIMERNOFG` is exactly the WeChat behaviour, and it is self-cancelling: if
the window is already in the foreground the call does nothing, so you never get
flashed while you are watching.

Finding the window is where the subtleties are:

**1. Walk up the process tree.** The notifier is a child of Claude Code, which is
a child of your shell, which is a child of the terminal emulator:

```
notify-taskbar.exe → bash.exe → claude.exe → powershell.exe → WindowsTerminal.exe
```

One `CreateToolhelp32Snapshot` gives the whole process table in about a
millisecond, so the walk is cheap. No WMI, no `Get-Process` — those cost 600 ms+
and this runs on every single turn.

**2. Match the terminal by process name, not by "first ancestor with a window".**
Walking up, the first process owning a visible window is often **not** the
terminal — `explorer.exe` sits above it in the chain and owns a large number of
top-level windows. The walk collects every ancestor running a known terminal
emulator (`windowsterminal.exe`, `conhost.exe`, `code.exe`, …) and prefers those.

**3. Skip DWM-cloaked windows.** `IsWindowVisible()` returns `true` for windows
that DWM has cloaked and that never appear on screen. Without the
`DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, …)` check, the search reliably
latches onto explorer's `ThumbnailDeviceHelperWnd` — a real failure observed
while building this.

**4. Skip owned windows.** `GetWindow(hwnd, GW_OWNER) != 0` means the window is a
dialog or tool window belonging to another window. Skipping these stops the
notifier from flashing a stray popup.

Fallbacks, in order: a terminal ancestor → any non-shell ancestor → any terminal
emulator on the machine. The last one handles a broken process tree (a wrapper
shell that exits and orphans its child), which is common when Claude Code is
launched from a script.

---

## Install

**1. Build the binary.**

```cmd
build.cmd
```

Or by hand:

```cmd
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
    -nologo -target:exe -optimize+ -out:notify-taskbar.exe notify-taskbar.cs
```

This is C# 5 compiled against .NET Framework 4.x, which ships with Windows — no
SDK, no NuGet, no runtime install. Copy `notify-taskbar.exe` somewhere
permanent, e.g. `%USERPROFILE%\.claude\`.

**2. Register the hooks.** Merge into `%USERPROFILE%\.claude\settings.json`:

```json
{
  "preferredNotifChannel": "terminal_bell",
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command",
                     "command": "C:\\Users\\YOU\\.claude\\notify-taskbar.exe",
                     "args": [], "timeout": 10 } ] }
    ],
    "Notification": [
      { "hooks": [ { "type": "command",
                     "command": "C:\\Users\\YOU\\.claude\\notify-taskbar.exe",
                     "args": [], "timeout": 10 } ] }
    ]
  }
}
```

`"args": []` selects the *exec* form: the binary is spawned directly with no
shell, so backslashes in the path never reach a shell parser. See
`settings.example.json`.

**3. Windows Terminal bell style.** Windows Terminal flashes the taskbar button
on a BEL character. Without `"taskbar"` in `bellStyle`, the notifier still finds
the window and still calls `FlashWindowEx` — nothing visible happens.

```jsonc
"profiles": { "defaults": { "bellStyle": ["taskbar"] } }
```

`windows-terminal.example.json` in this repo is a working settings file built
from a real setup, covering the bell plus font, scheme, opacity and acrylic.
Copy it in whole, or just take the `bellStyle` line. Drop `"audible"` if you do
not want a sound on permission prompts.

**4. Restart Claude Code.** Hook configuration is read at startup.

---

## Usage

```cmd
notify-taskbar.exe                 # flash, silent
notify-taskbar.exe --verbose       # explain how the window was resolved
```

Every run appends one line to `%USERPROFILE%\.claude\notify-taskbar.log`
(truncated past 64 KB). A hook that silently does nothing is otherwise
impossible to distinguish from a hook that never ran:

```
2026-09-12 15:54:12  via terminal ancestor | hwnd=0x320922 pid=26200 foreground=False cloaked=False title="◑ Claude Code 任务完成通知" class="CASCADIA_HOSTING_WINDOW_CLASS" | flashed=True
```

`class="CASCADIA_HOSTING_WINDOW_CLASS"` is Windows Terminal's window class —
that is what a correctly resolved window looks like. `flashed=False` alongside
`foreground=True` is normal: it means you were already looking at the terminal.

When the precise path loses, the log records the ancestor chain, so the fallback
can be diagnosed from the log alone:

```
via machine-wide scan | ... | ancestors: 26640:claude.exe <- 35468:sh.exe <- 23888:(gone)
```

`(gone)` means a process in the chain had already exited — the walk stops there.

---

## Uninstall

Remove the `hooks` block from `settings.json`, or set `"disableAllHooks": true`.

---

## Known limitations

- **Windows only.** Everything here is Win32.
- **Multiple terminal *windows* plus a broken process tree** can flash the wrong
  window. Multiple Windows Terminal *tabs* are one window, so they are unaffected.
  The precise path handles multiple windows correctly; only the last-resort
  fallback guesses.
- **Console-title matching does not work under Windows Terminal.** An obvious
  way to disambiguate multiple windows is to match the window title against
  `GetConsoleTitle()`. Windows Terminal runs on ConPTY, where an application's
  OSC title sequence updates the WT *tab* but is not reflected back to the
  attached console — `GetConsoleTitle()` returns the shell's path instead
  (`D:\Git\Git\bin\..\usr\bin\bash.exe`). Tried, measured, and removed rather
  than left in looking like it does something.

---

## Layout

```
notify-taskbar.cs                single-file source; all P/Invoke, no dependencies
build.cmd                        csc invocation
settings.example.json            sanitized Claude Code hook configuration
windows-terminal.example.json    Windows Terminal notification + appearance settings
```

## License

MIT
