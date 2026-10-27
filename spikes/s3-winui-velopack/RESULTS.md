# S3: an unpackaged WinUI 3 app that installs, updates and answers toasts

**Verdict: GO**, with two changes from the obvious setup: publish needs
`EnableMsixTooling`, and toast activation has to use the OS toast API, because Windows
App SDK's `AppNotificationManager` cannot register in an unpackaged, self-contained
app.

Run on 2026-10-27. Built on Windows 11 x64 with .NET SDK 10.0.403, Windows App SDK
2.5.1 and Velopack 1.2.161. Tested on a clean Windows 11 VM (build 26715), driven
through its real UI: the app's buttons through UI Automation, and the toasts with
mouse clicks and a paste on the actual notification popup.

## Question

The design (§7) has the WinUI 3 app install and update itself with Velopack, with no
MSIX, no Store and no admin rights. It also makes the app the place an `ask_user`
question surfaces when nobody is looking at the dashboard, as an actionable toast.
That rests on four things:

1. An unpackaged, self-contained WinUI 3 app can be packed, installed per user and
   started.
2. A toast with option buttons and a free-text box reaches the app, both while it
   runs and when clicking the toast is what starts it.
3. A Velopack delta update applies and restarts, and toasts keep working afterwards.
4. Uninstall leaves nothing behind, including no dead toast activator.

## What was built

`App/` is a one-window WinUI 3 app (`net10.0-windows10.0.22621.0`, `WindowsPackageType`
`None`, Windows App SDK and .NET both self-contained). It shows its installed version
and how it was launched, and has three buttons:

* **Show question toast** shows a toast shaped like an `ask_user` question: two option
  buttons ("PostgreSQL (Recommended)", "SQLite") and a text box with a Send button.
* **Show toast, then exit** does the same and quits, so the toast is answered with the
  app closed.
* **Check for updates** points Velopack's `UpdateManager` at a local feed and applies
  any update.

Every answer is shown in the window and appended to
`%LOCALAPPDATA%\AgentBridgeS3Data\activations.log`. That folder sits outside the
install folder, which an update replaces.

`Program.Main` runs `VelopackApp.Build().Run()` first, then registers the toast
activator, then starts XAML (`DISABLE_XAML_GENERATED_MAIN`).

## Results

| Check | Result |
|---|---|
| Silent per-user install (`Setup.exe --silent`) | Pass. 3-5 s, into `%LOCALAPPDATA%\AgentBridgeS3`, with Start menu and desktop shortcuts and an HKCU uninstall entry |
| Launch, version read back | Pass: `1.0.0` from Velopack and the assembly |
| Toast option button while the app runs | Pass: `chose "PostgreSQL"`, with the toast's own argument (`q`) intact |
| Toast typed answer while the app runs | Pass: `DuckDB – 数据库 ✓` arrived as U+2013, U+6570 U+636E U+5E93 and U+2713 |
| Toast clicked with the app closed (cold start) | Pass: a new process started, reported "from a toast (cold start)" and received `chose "PostgreSQL"` |
| Delta update 1.0.0 → 1.0.1 | Pass. 0.17 MB delta against a 94 MB full package; downloaded, applied and restarted at 1.0.1 in under 13 s |
| Toast after the update | Pass: `chose "SQLite"`, with no re-registration |
| Uninstall (`Update.exe --uninstall --silent`) | Pass. Install folder, shortcuts, uninstall key, toast AUMID and COM activator registrations are all removed |
| Crash log over the whole run | Empty |

Sizes: `Setup.exe` 101 MB and the full package 94 MB, both self-contained with the
Windows App SDK and the .NET runtime. Code-only updates ship as deltas of a few
hundred KB.

## Findings

### 1. Windows App SDK toasts cannot take answers in an unpackaged self-contained app

`AppNotificationManager.Default.Register()` throws `COMException 0x8007007E`:
"Unable to load resource dll. Microsoft.WindowsAppRuntime.Insights.Resource.dll". The
DLL ships in none of the SDK's NuGet packages. This is
[microsoft/WindowsAppSDK#6774](https://github.com/microsoft/WindowsAppSDK/issues/6774):
reported fixed by microsoft/WindowsAppSDK#6725 and still failing on 2.5.1.
`WindowsAppSdkIncludeVersionInfo` does not help. Called from `OnLaunched` the exception
is fatal, and appears only as `0xC000027B` in `Microsoft.UI.Xaml.dll`. Catching it
leaves a toast that displays, but whose clicks never reach the app.

The workarounds are installing the Windows App Runtime (giving up self-contained),
MSIX or sparse packaging (giving up Velopack and adding signing to every install), or
**bypassing it**. The spike bypasses it with the OS API
(`Windows.UI.Notifications`) and the classic desktop COM activator, through
`Microsoft.Toolkit.Uwp.Notifications`' `ToastNotificationManagerCompat`. That works
in every case above.

The toolkit is in maintenance and pulls `System.Drawing.Common` 4.7.0, which has a
critical advisory (GHSA-rxg9-xrhp-64gj), pinned here to 10.0.14. **The product should
own a small activator instead**: an HKCU `LocalServer32` registration plus
`INotificationActivationCallback`, about 150 lines.

### 2. `dotnet publish` of an unpackaged WinUI 3 app omits the app's `.pri`

Without `EnableMsixTooling`, the publish folder has no `AgentBridgeS3.pri`, so the
compiled XAML is missing and startup fails. The failure is a `XamlParseException` that,
like the toast failure, surfaces only as `0xC000027B`. The build output runs, so a
developer only sees this in a published or installed copy. CI now publishes the spike
and checks for the file.

Both failures surface identically, so the product needs `Application.UnhandledException`
logging from day one, as `CrashLog.cs` does here. Without it there is no way to tell
them apart.

### 3. A cold-start activation can arrive before or after the window exists

Windows starts the exe with `-ToastActivated` and then calls the activator on a COM
thread. Here it arrived after `OnLaunched` had built the window, but nothing
guarantees that. The activator must be registered before XAML starts, buffer
anything that arrives before the UI is ready, and decide "cold start" from the
process's launch, not from timing. An earlier version did the latter and mislabelled
the answer. For the product, the activator should hand the answer to the Bridge Host
directly, as S1's injector worker does, rather than going through the window.

### 4. Registration is stable across updates

The activator is registered under the AUMID Velopack gives its shortcuts
(`velopack.AgentBridgeS3`). Its `LocalServer32` points at
`%LOCALAPPDATA%\AgentBridgeS3\current\AgentBridgeS3.exe`, a path Velopack keeps the
same across updates. So an update needs no re-registration: a toast shown after the
update was answered without the app registering anything again. Velopack's
`OnBeforeUninstallFastCallback` is the place to remove the registration, and does.

## Limitations

* The installer was started from Hyperloop's elevated shell. Everything it wrote is
  per user (HKCU and `%LOCALAPPDATA%`), and Velopack does not ask for elevation. A run
  from a standard user's Explorer is still to be done.
* Neither Setup nor the app is signed. A downloaded `Setup.exe` meets SmartScreen
  and, where enabled, Smart App Control. Signing is a release requirement (§7), not
  something this spike tests.
* The toast shows the app name with a default icon. Branding is cosmetic and was not
  tested.
* Only Windows 11 was tested. The project's floor (`TargetPlatformMinVersion`
  19041, Windows 10 2004) is untested.
* A toast shown before an update and answered after it was not tested. The activator
  path does not change, so it should work.

## Reproduce

From this folder, with the .NET 10 SDK and `vpk` (`dotnet tool install -g vpk`):

```powershell
foreach ($v in '1.0.0', '1.0.1') {
    dotnet publish App\AgentBridgeS3.csproj -c Release -p:Version=$v -o out\publish\$v
    vpk pack -u AgentBridgeS3 -v $v -p out\publish\$v -e AgentBridgeS3.exe -o out\releases --packTitle "Agent Bridge S3"
    if ($v -eq '1.0.0') { Copy-Item out\releases\AgentBridgeS3-win-Setup.exe out\Setup-1.0.0.exe }
}
```

On a test machine, run `Setup-1.0.0.exe` and put the path of a folder holding
`releases.win.json` and the 1.0.1 packages in
`%LOCALAPPDATA%\AgentBridgeS3Data\feed.txt`. Then use the window's buttons.
