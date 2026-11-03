# Native app acceptance against the isolated .NET stack

The existing **Connection Settings** UI is sufficient. No debug endpoint, token
injection, manifest-reading app API or TLS bypass is needed. Run the actual Mac
app and the iOS app on dedicated iPhone and iPad simulators, without
`--ui-fixture` or `--sample-sessions`.

This exercises native onboarding, Keychain persistence, relay/Noise transport and
live views against real .NET services with invented transcript data. The local
identity-provider stub auto-approves its disposable account. This does not prove
external GitHub approval, physical-device networking or real agent typing.

## Prepare one fresh run

1. Start `AgentBridge.AppleInterop` with a valid localhost certificate and a new
   private manifest. The harness expires after 30 minutes; restart with a new
   manifest if it has expired. Keep its stdin available for fixture commands.
2. Obtain the separately approved certificate trust on the Mac and each dedicated
   simulator. Retain normal app TLS checks. Before trust is established, a
   sign-in attempt should fail without a successful account or fixture connection;
   record that separately if testing the negative path.
3. Read only the manifest's public relay URL; the manifest also contains a token,
   so never dump it into logs, screenshots or a shared terminal transcript:

   ```sh
   python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["relayUrl"])' /absolute/path/to/manifest.json
   ```

   Use that exact `https://localhost:<port>` origin. The harness's internal HTTP
   listener is not an app setting. Build/install the current development apps
   normally and keep existing signing and security settings.

## Connect each native app

1. Open **Connection Settings** from the empty state or the **Session filters and
   connections** toolbar menu. Enter the relay URL and a distinct device name:
   `Interop Mac`, `Interop iPhone` or `Interop iPad`.
2. On Mac, retain the intended real local host profile, such as the separately
   configured `mac-check` profile. The isolated fixture is reached through the
   relay; its arbitrary temporary Unix socket needs no app override or symlink.
   Another local machine card may coexist, so select the fixture by its name.
3. If the app already shows another account, use **Apply and Reconnect** after
   editing the origin so the displayed account switches to that origin. For the
   fresh fixture origin, press **Sign In with GitHub**. This button applies the
   edited fields before starting the production device flow.
4. Do not press **Continue on GitHub** or copy the `TEST-ONLY` code to GitHub. The
   local provider approves automatically after polling. Verify the account is
   `apple-interop-fixture`, then close Connection settings.
5. Open **Machines** and select **Isolated .NET interoperability host**. Verify it
   is online and marked **View only**, with the reason:
   `Synthetic transcript fixture: no real agent actions are connected.`
6. Open **Synthetic Apple interoperability session**. Verify the main response
   contains `The .NET host parsed this temporary transcript.` Open **Interop
   helper** under **Background activity** and verify its own detail contains
   `The delegate is connected through the real .NET host.`
7. Open **Reply / Actions** for this fixture session. It should offer no reply
   editor, attachment upload or stop action and should say that remote replies
   are unavailable. A toolbar button opening this explanatory sheet is expected.
   A different real host's launch controls do not establish fixture permissions.

Keep all three apps connected at the initial response before advancing the run.

## Exercise updates and lifecycle

1. Send `advance` once to the harness stdin. Alternatively, run the Swift interop
   probe against this same fresh manifest after the three initial screens are
   verified; the probe creates the one-shot update trigger itself. Do not advance
   before that probe, which expects the initial response first.
2. Verify all three apps update without a manual refresh to
   `Live update received from the real .NET session parser.` Reopen the delegate
   inspector and verify `Synthetic delegate work is complete.` Capture each app's
   own screen; a successful CLI probe does not prove a native view updated.
3. Send `reconnect` to the harness stdin. Verify the same fixture machine returns
   online with one copy of the session and the updated response. A brief offline
   state may be too short to observe. Then use each app's **Disconnect** and
   **Reconnect** once and verify the same result.
4. Terminate and relaunch each app without reinstalling it. It should recover its
   saved origin/account through the normal Keychain path and reconnect without
   another device-code exchange. On iOS, also background and foreground the app;
   its ordinary scene lifecycle reconnect should recover the updated session.
5. Finally send `remove`. Verify the synthetic live session disappears from every
   app while the fixture machine remains connected. Run destructive fixture
   removal last; a fresh stack is required to restore the initial transcript.
6. Sign out of the fixture origin in each app and stop the harness with `quit`.
   Restore any connection settings changed for the run. Certificate cleanup is
   the separately approved trust-setup procedure; never delete unrelated trust.

Record build commit, app version, simulator model/OS, and pass/fail evidence for
each platform: sign-in, initial response, delegate detail, live update, reconnect,
relaunch, read-only actions and removal. Until observed, mark a step **not run**.
Do not include the manifest, Keychain contents or any account token in evidence.

The Mac's profile-based connection to the real product host is a separate local
acceptance check. The Swift probe can also test the fixture's temporary local
socket directly. Physical iPhone/iPad acceptance requires a separately authorized
relay reachable from those devices with valid trust; a device's `localhost` is
the device itself, and this fixture listens only on the Mac's loopback interface.
