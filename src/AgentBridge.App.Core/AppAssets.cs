namespace AgentBridge.App.Core;

// The app's brand art as it ships beside the exe, relative to the app's folder. tools/design-gen
// draws both from design/ (the mark and tokens.json appIcon); the window, the tray and the toast
// registration name them here, so a new name or file for the art (#537, #541) is one change.
// The app's project names the same files for the exe's icon and the installer's (AppIconFile,
// AppLogoFile in AgentBridge.App.csproj, read by build/app-channels.ps1); AppBrandTests holds the
// two together.
public static class AppAssets
{
    // The window, taskbar, tray and exe icon (16 to 256 px).
    public static readonly string Icon = Path.Combine("Assets", "AgentBridge.ico");

    // The toast sender's icon, in Notification Center and Settings > Notifications.
    public static readonly string Logo = Path.Combine("Assets", "AgentBridge.png");
}
