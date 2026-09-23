# HiddenGPT

HiddenGPT is a Windows 11 proof of concept that hosts ChatGPT in a dedicated Microsoft WebView2 window and asks Windows to exclude that window from screen capture with `WDA_EXCLUDEFROMCAPTURE`.

The app creates its window handle and verifies capture exclusion before showing any UI, including startup controls. It reapplies and verifies the setting before restoring the window through its shortcuts or second-instance activation. If this fails, it exits without displaying a window or error dialog; the error is recorded in `%LOCALAPPDATA%\HiddenGPT\HiddenGPT.log`. Acceptance only confirms the Windows API call; it does not verify your recorded video or outgoing stream. Complete the capture test below before signing in.

The same exclusion request applies to screenshots, screen recordings, and screen sharing through supported Windows capture APIs; it is not specific to Discord. Compatibility depends on the recorder and its capture method. Microsoft does not guarantee protection against every capture method. See [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity).

App tooltips and default JavaScript dialogs are suppressed to avoid separate popup windows. Browser accelerator shortcuts (including Ctrl+P) are disabled; use the app's navigation and zoom controls. JavaScript confirmations and prompts are dismissed, which may prevent site actions that require them.

HiddenGPT does not create a taskbar button. Use **Ctrl+Alt+H** to hide or restore its window. The **×** button exits the app.

Press **Mouse 4** (the back side button) to toggle **view-only mode**: the window stays visible on top, while mouse movement, clicks, and scrolling pass through to the application underneath. The virtual cursor is released and keyboard interaction with HiddenGPT is blocked. Press **Mouse 4** again to return to interactive mode and focus the browser for typing. This shortcut also works while another app has focus. If HiddenGPT is hidden or minimized, Mouse 4 restores interactive mode. Mouse 4 is the default. Use **Set mouse button**, then press and release either side button to save that button as the shortcut. Raw Input observes button events without globally blocking them, so an app underneath may also perform its normal Back/Forward action; HiddenGPT suppresses its own side-button navigation. The status bar shows the current mode.

Click-through uses the Windows layered-window transparent style ([Microsoft documentation](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)).

**Ctrl+Alt+P** also toggles view-only mode. The **View only (Mouse 4)** button provides an on-screen way to enter it. If the side button does not respond, try Ctrl+Alt+P to distinguish a mouse mapping issue from a click-through issue. Shortcut events and native style verification are recorded in `%LOCALAPPDATA%\HiddenGPT\HiddenGPT.log`. The mouse shortcut uses **Raw Input with RIDEV_INPUTSINK**, independently of window focus and virtual-cursor state. This replaces the low-level hook that was not receiving side-button events on the affected machine. See [Microsoft Raw Input examples](https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input). The selected button is saved in `%LOCALAPPDATA%\HiddenGPT\mouse-shortcut.txt`.

**Always on top** is enabled by default, so clicking another app leaves HiddenGPT visible. Uncheck it if you want other windows to cover HiddenGPT. Switching to another app releases the virtual cursor without hiding the window.

## Run from source

Requirements:

- Windows 11
- .NET 10 SDK (for building)
- Microsoft Edge WebView2 Runtime (included with current Windows 11 installations; the app shows a clear error if it is unavailable)

```powershell
dotnet restore
dotnet run --project .\HiddenGPT.csproj
```

The dedicated WebView2 profile is stored at `%LOCALAPPDATA%\HiddenGPT\WebView2Profile`. The **Clear session** button removes its cookies, storage, cache, permissions, and history.

## Screen recording and screen-share verification

For recordings, inspect the saved video from the exact recorder and capture method you plan to use. For Discord or other screen sharing, use a second account/device or another person to observe the actual outgoing stream. A recorder preview or a successful Windows API call alone is not verification of the saved output.

For a test that never automatically navigates to ChatGPT, close any running HiddenGPT instance and launch:

```powershell
.\artifacts\HiddenGPT-win-x64\HiddenGPT.exe --capture-test
# Or from source:
dotnet run --project .\HiddenGPT.csproj -- --capture-test
```

This opens the local marker page after browser initialization and keeps your existing profile/session. Normal launches still open ChatGPT automatically. A second launch only activates the existing instance, so close it first when changing launch mode. Use the test-mode command for each restart during verification.

- Start recording or sharing the entire monitor, then launch HiddenGPT with **--capture-test**. Confirm the colored test marker never appears in the output.
- Move, resize, maximize, minimize, restore, hide, and show the window. Confirm no frame flashes its content.
- Close and restart HiddenGPT while recording or sharing. Confirm no startup content appears. Also launch it a second time while hidden to test restoring the existing instance.
- Confirm right-click menus are disabled. Click links that request a new window and confirm they stay in the protected window.
- Confirm the desktop behind the protected window remains visible in the output, with no unwanted blank rectangle. Check the virtual cursor as well as the window.
- For recording, stop and play back the saved file. Inspect transitions frame by frame for flashes. Test screenshots separately if you use them.
- After every check passes, tick **Recording / screen-share test completed** to mark the current setup as verified for this app session. This is a manual acknowledgment, is not saved between runs, and does not enable or disable capture exclusion. **Open ChatGPT** becomes available as soon as the protected browser is ready.
- Repeat these checks for each recorder/capture method and after changing Windows, graphics drivers, or recorder settings. Passing a Discord test does not establish that a different recorder works.

`WDA_EXCLUDEFROMCAPTURE` behavior depends on Windows and the capture path. HiddenGPT protects only its own top-level window. It does not hide the normal ChatGPT desktop app, other browsers, Windows dialogs, or notifications. Downloads and file selection are disabled, external protocols are blocked, system notifications are denied, and popup links are redirected into the protected window to avoid creating unprotected surfaces.

### Recorder checklist for this machine

Actual output from these recorders has not yet been verified for this build. Use the test page and the transition checks above for every row; record the result only after inspecting the saved output.

| Recorder | What to test | Result |
| --- | --- | --- |
| Snipping Tool | Take a screenshot and a video covering HiddenGPT. Test video separately from still capture. | Pending |
| NVIDIA App | Record the desktop with HiddenGPT over another app. Separately test gameplay and Instant Replay if you use them. | Pending |
| OBS Studio | Test Display Capture with an explicitly selected capture method; test Windows Graphics Capture and DXGI separately if offered. If you use Window Capture or Game Capture, test those sources separately too. | Pending |

For Snipping Tool, **Win+Shift+S** opens screenshot capture and **Win+Shift+R** opens video capture ([Microsoft instructions](https://support.microsoft.com/en-us/windows/apps/use-snipping-tool-to-capture-screenshots)). For NVIDIA App, **Alt+Z** opens the overlay and the default **Alt+F9** shortcut starts/stops recording ([NVIDIA instructions](https://www.nvidia.com/en-us/geforce/news/gfecnt/202411/nvidia-app-download-and-features/)). For OBS, set the capture method in the Display Capture source properties so the test identifies the actual method instead of relying on Automatic ([OBS instructions](https://obsproject.com/kb/laptop-troubleshooting)). None of these instructions establishes compatibility without a saved-output test.

If HiddenGPT appears in a recorder's output, that setup has failed verification. The app cannot force every recorder to honor Windows display affinity. In OBS, capturing only the desired application's window is another option: its documented behavior excludes overlapping windows ([OBS Window Capture](https://obsproject.com/kb/window-capture-sources)). This is a different setup and needs its own test.

## Publish an executable

Run `publish.ps1`. It restores dependencies and creates a self-contained x64 package in `artifacts\HiddenGPT-win-x64`. The machine still needs the WebView2 Runtime.

```powershell
.\publish.ps1
```

## Shortcut regression checks

Run `dotnet run --project tests/SideButtonShortcut.Tests.csproj` to check press/release handling, repeated events, and changing the side-button binding. These checks do not replace testing the physical mouse and click-through behavior on Windows.
