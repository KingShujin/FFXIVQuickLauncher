using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CheapLoc;
using CommandLine;
using Config.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Http.HappyEyeballs;
using XIVLauncher.Common.Util;
using XIVLauncher.Common.Windows;
using XIVLauncher.PlatformAbstractions;
using XIVLauncher.Settings;
using XIVLauncher.Settings.Parsers;
using XIVLauncher.Windows;
using XIVLauncher.Xaml;

namespace XIVLauncher
{
    /// <summary>
    ///     Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public class CmdLineOptions
        {
            [CommandLine.Option("dalamud-runner-override", Required = false, HelpText = "Path to a folder to override the dalamud runner with.")]
            public string DalamudRunnerOverride { get; set; }

            [CommandLine.Option("dalamud-beta-kind", Required = false, HelpText = "The Dalamud beta kind to use. Persists.")]
            public string DalamudBetaKind { get; set; }

            [CommandLine.Option("dalamud-beta-key", Required = false, HelpText = "The Dalamud beta key to use. Persists.")]
            public string DalamudBetaKey { get; set; }

            [CommandLine.Option("roamingPath", Required = false, HelpText = "Path to a folder to override the roaming path for XL with.")]
            public string RoamingPath { get; set; }

            [CommandLine.Option("noautologin", Required = false, HelpText = "Disable autologin.")]
            public bool NoAutoLogin { get; set; }

            [CommandLine.Option("canary-account-autologin", Required = false, HelpText = "Use the selected saved account for a guarded XIV Enhanced canary only.")]
            public bool CanaryAccountAutologin { get; set; }

            [CommandLine.Option("gen-localizable", Required = false, HelpText = "Generate localizable files.")]
            public bool DoGenerateLocalizables { get; set; }

            [CommandLine.Option("gen-integrity", Required = false, HelpText = "Generate integrity files. Provide a game path.")]
            public string DoGenerateIntegrity { get; set; }

            [CommandLine.Option("allow-fake-start", Required = false, HelpText = "Allow fake start.")]
            public bool AllowFakeStart { get; set; }

            [CommandLine.Option("account", Required = false, HelpText = "Account name to use.")]
            public string AccountName { get; set; }

            [CommandLine.Option("steamticket", Required = false, HelpText = "Steam ticket to use.")]
            public string SteamTicket { get; set; }

            [CommandLine.Option("clientlang", Required = false, HelpText = "Client language to use.")]
            public ClientLanguage? ClientLanguage { get; set; }

            // We don't care about these, just need it so that the parser doesn't error
            [CommandLine.Option("squirrel-updated", Hidden = true)]
            public string SquirrelUpdated { get; set; }

            [CommandLine.Option("squirrel-install", Hidden = true)]
            public string SquirrelInstall { get; set; }

            [CommandLine.Option("squirrel-obsolete", Hidden = true)]
            public string SquirrelObsolete { get; set; }

            [CommandLine.Option("squirrel-uninstall", Hidden = true)]
            public string SquirrelUninstall { get; set; }

            [CommandLine.Option("squirrel-firstrun", Hidden = true)]
            public bool SquirrelFirstRun { get; set; }
        }

        public const string REPO_URL = "https://github.com/goatcorp/FFXIVQuickLauncher";

        public static ILauncherSettingsV3 Settings;
        public static WindowsSteam Steam;
        public static CommonUniqueIdCache UniqueIdCache;

        private UpdateLoadingDialog updateWindow;

        public static CmdLineOptions CommandLine { get; private set; }

        public static HttpClient HttpClient { get; private set; }

        private FileInfo dalamudRunnerOverride = null;
        private MainWindow mainWindow;

        public static bool GlobalIsDisableAutologin { get; private set; }
        public static bool GlobalIsCanaryAccountAutologin { get; private set; }
        public static byte[] GlobalSteamTicket { get; private set; }
        public static DalamudUpdater DalamudUpdater { get; private set; }

        public App()
        {
            if (!DebugHelpers.IsDebugBuild)
            {
                try
                {
                    AppDomain.CurrentDomain.UnhandledException += EarlyInitExceptionHandler;
                    TaskScheduler.UnobservedTaskException += TaskSchedulerOnUnobservedTaskException;
                }
                catch
                {
                    // ignored
                }
            }
        }

        public static void SetupSettings()
        {
            Settings = new ConfigurationBuilder<ILauncherSettingsV3>()
                .UseCommandLineArgs()
                .UseJsonFile(GetConfigPath("launcher"))
                .UseTypeParser(new DirectoryInfoParser())
                .UseTypeParser(new AddonListParser())
                .UseTypeParser(new CommonJsonParser<PreserveWindowPosition.WindowPlacement>())
                .Build();

            if (string.IsNullOrEmpty(Settings.AcceptLanguage))
            {
                Settings.AcceptLanguage = ApiHelpers.GenerateAcceptLanguage();
            }

            UniqueIdCache = new CommonUniqueIdCache(new FileInfo(Path.Combine(Paths.RoamingPath, "uidCache.json")));

            try
            {
                if (!string.IsNullOrEmpty(CommandLine.AccountName))
                {
                    App.Settings.CurrentAccountId = CommandLine.AccountName;
                    Log.Verbose("Account override: '{0}'", CommandLine.AccountName);
                }

                if (!string.IsNullOrEmpty(CommandLine.SteamTicket))
                {
                    GlobalSteamTicket = Convert.FromBase64String(CommandLine.SteamTicket);
                }

                if (CommandLine.ClientLanguage != null)
                {
                    App.Settings.Language = CommandLine.ClientLanguage;
                }

                if (!string.IsNullOrEmpty(CommandLine.DalamudBetaKind))
                {
                    Settings.DalamudBetaKind = CommandLine.DalamudBetaKind;
                }

                if (!string.IsNullOrEmpty(CommandLine.DalamudBetaKey))
                {
                    Settings.DalamudBetaKey = CommandLine.DalamudBetaKey;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not apply settings overrides from command line");
            }
        }

        private void SetupHttpClient()
        {
            HttpClient = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectCallback = new HappyEyeballsCallback().ConnectCallback,
            });
        }

        private void OnUpdateCheckFinished(bool finishUp)
        {
            Dispatcher.Invoke(() =>
            {
                this.useFullExceptionHandler = true;

                this.updateWindow?.Hide();

                if (!finishUp)
                    return;

                this.mainWindow = new MainWindow();
                this.mainWindow.Initialize();

                try
                {
                    DalamudUpdater = new DalamudUpdater(HttpClient, new DirectoryInfo(Path.Combine(Paths.RoamingPath, "addon")),
                        new DirectoryInfo(Path.Combine(Paths.RoamingPath, "runtime")),
                        new DirectoryInfo(Path.Combine(Paths.RoamingPath, "dalamudAssets")),
                        UniqueIdCache,
                        Settings.DalamudRolloutBucket);

                    this.mainWindow.SettingsControl.SetUpdater(DalamudUpdater);

                    if (this.dalamudRunnerOverride != null)
                    {
                        DalamudUpdater.RunnerOverride = this.dalamudRunnerOverride;
                    }

                    Settings.DalamudRolloutBucket = DalamudUpdater.RolloutBucket;

                    var dalamudWindowThread = new Thread(DalamudOverlayThreadStart);
                    dalamudWindowThread.SetApartmentState(ApartmentState.STA);
                    dalamudWindowThread.IsBackground = true;
                    dalamudWindowThread.Start();

                    while (DalamudUpdater.Overlay == null)
                        Thread.Yield();

                    DalamudUpdater.Run(
                        Settings.DalamudBetaKind,
                        Settings.DalamudBetaKey);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not start dalamud updater");
                }
            });
        }

        // We need this because the main dispatcher is blocked by the main window/login task.
        private static void DalamudOverlayThreadStart()
        {
            var overlay = new DalamudLoadingOverlay();
            overlay.Hide();

            DalamudUpdater.Overlay = overlay;

            System.Windows.Threading.Dispatcher.Run();
        }

        private static void GenerateIntegrity(string path)
        {
            var result = IntegrityCheck.GenerateIntegrityReport(new DirectoryInfo(path), null);
            string saveIntegrityPath = Path.Combine(Paths.RoamingPath, $"{result.GameVersion}.json");

            File.WriteAllText(saveIntegrityPath, JsonConvert.SerializeObject(result));

            MessageBox.Show($"Successfully hashed {result.Hashes.Count} files to {path}.", "Hello Franz", MessageBoxButton.OK, MessageBoxImage.Asterisk);
            Environment.Exit(0);
        }

        private static void GenerateLocalizables()
        {
            try
            {
                Loc.ExportLocalizable();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());
            }

            Environment.Exit(0);
        }

        private bool useFullExceptionHandler = false;

        private void TaskSchedulerOnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            if (!e.Observed)
                EarlyInitExceptionHandler(sender, new UnhandledExceptionEventArgs(e.Exception, true));
        }

        private void EarlyInitExceptionHandler(object sender, UnhandledExceptionEventArgs e)
        {
            this.Dispatcher.Invoke(() =>
            {
                Log.Error((Exception)e.ExceptionObject, "Unhandled exception");

                if (this.useFullExceptionHandler)
                {
                    CustomMessageBox.Builder
                        .NewFrom((Exception)e.ExceptionObject, "Unhandled", CustomMessageBox.ExitOnCloseModes.ExitOnClose)
                        .WithAppendText("\n\nError during early initialization. Please report this error.\n\n" + e.ExceptionObject)
                        .Show();
                }
                else
                {
                    MessageBox.Show(
                        "Error during early initialization. Please report this error.\n\n" + e.ExceptionObject,
                        "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                Environment.Exit(-1);
            });
        }

        private static string GetConfigPath(string prefix) => Path.Combine(Paths.RoamingPath, $"{prefix}ConfigV3.json");

        private static bool IsGuardedCanaryAccountLogin(CmdLineOptions options)
        {
            if (!options.NoAutoLogin || string.IsNullOrWhiteSpace(options.RoamingPath) ||
                string.IsNullOrWhiteSpace(options.DalamudRunnerOverride) ||
                !Guid.TryParseExact(Environment.GetEnvironmentVariable("XIVENHANCED_CANARY_SESSION"), "N", out var session))
                return false;

            var fingerprint = Environment.GetEnvironmentVariable("XIVENHANCED_CANARY_FINGERPRINT");
            if (fingerprint?.Length != 64 || !fingerprint.All(Uri.IsHexDigit) ||
                !string.Equals(fingerprint, fingerprint.ToUpperInvariant(), StringComparison.Ordinal))
                return false;

            try
            {
                var profile = Path.GetFullPath(options.RoamingPath).TrimEnd(Path.DirectorySeparatorChar);
                var expectedProfile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "profile"));
                if (!string.Equals(profile, expectedProfile, StringComparison.OrdinalIgnoreCase) ||
                    !IsOrdinaryCanaryDirectory(AppContext.BaseDirectory) ||
                    !IsOrdinaryCanaryDirectory(profile))
                    return false;

                var certification = Path.Combine(profile, "patch-certification");
                var runners = Path.Combine(certification, "runners");
                var receipts = Path.Combine(certification, "canary-runtime-receipts");
                if (!IsOrdinaryCanaryDirectory(certification) ||
                    !IsOrdinaryCanaryDirectory(runners) ||
                    !IsOrdinaryCanaryDirectory(receipts))
                    return false;
                var runnerRoot = runners + Path.DirectorySeparatorChar;
                var injector = Path.GetFullPath(options.DalamudRunnerOverride);
                if (!injector.StartsWith(runnerRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(injector).Equals("Dalamud.Injector.exe", StringComparison.OrdinalIgnoreCase))
                    return false;

                var runner = Path.GetDirectoryName(injector);
                if (!IsOrdinaryCanaryDirectory(runner))
                    return false;
                var pending = ReadBoundedCanaryRecord(Path.Combine(certification, "pending-update.json"));
                var receipt = ReadBoundedCanaryRecord(Path.Combine(receipts, session.ToString("N") + ".json"));
                if (pending == null || receipt == null ||
                    (int?)pending["schemaVersion"] != 1 ||
                    (string)pending["evidenceType"] != "xiv-enhanced-pending-patch-certification" ||
                    (string)pending["candidateFingerprint"] != fingerprint ||
                    !string.Equals(Path.GetFullPath((string)pending["runner"] ?? ""), runner, StringComparison.OrdinalIgnoreCase) ||
                    (int?)receipt["schemaVersion"] != 1 ||
                    (string)receipt["evidenceType"] != "xiv-enhanced-canary-runtime-observation" ||
                    (string)receipt["status"] != "awaiting-process" ||
                    (string)receipt["sessionId"] != session.ToString("N") ||
                    (string)receipt["candidateFingerprint"] != fingerprint ||
                    (string)receipt["gameVersion"] != (string)pending["gameVersion"] ||
                    receipt["processId"]?.Type != JTokenType.Null ||
                    !string.Equals(Path.GetFullPath((string)receipt["expectedDalamudPath"] ?? ""),
                        Path.Combine(runner, "Dalamud.dll"), StringComparison.OrdinalIgnoreCase) ||
                    (string)receipt["expectedDalamudSha256"] != (string)pending["runnerDllSha256"])
                    return false;

                var requestedAt = (DateTimeOffset?)receipt["launchRequestedAtUtc"];
                var readyAt = (DateTimeOffset?)receipt["observerReadyAtUtc"];
                var discoveryDeadline = (DateTimeOffset?)receipt["processDiscoveryDeadlineUtc"];
                var now = DateTimeOffset.UtcNow;
                return requestedAt.HasValue && readyAt.HasValue && discoveryDeadline.HasValue &&
                       requestedAt.Value <= readyAt.Value && readyAt.Value <= now &&
                       now - requestedAt.Value <= TimeSpan.FromMinutes(10) && discoveryDeadline.Value > now;
            }
            catch (Exception)
            {
                // The opt-in has no fallback to ordinary autologin. Malformed or
                // inaccessible candidate evidence always leaves the canary closed.
                return false;
            }
        }

        private static JObject ReadBoundedCanaryRecord(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 65536 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            return JObject.Parse(File.ReadAllText(path));
        }

        private static bool IsOrdinaryCanaryDirectory(string path)
        {
            var directory = new DirectoryInfo(path);
            return directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) == 0;
        }

        private void App_OnStartup(object sender, StartupEventArgs e)
        {
            // HW rendering commonly causes issues with material design, so we turn it off by default for now
            try
            {
                if (!EnvironmentSettings.IsHardwareRendered)
                    RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            }
            catch
            {
                // ignored
            }

            try
            {
                var helpWriter = new StringWriter();
                var parser = new Parser(config =>
                {
                    config.HelpWriter = helpWriter;
                    config.IgnoreUnknownArguments = true;
                });
                var result = parser.ParseArguments<CmdLineOptions>(Environment.GetCommandLineArgs());

                if (result.Errors.Any())
                {
                    MessageBox.Show(helpWriter.ToString(), "Help");
                }

                CommandLine = result.Value ?? new CmdLineOptions();

                if (!string.IsNullOrEmpty(CommandLine.RoamingPath))
                {
                    Paths.OverrideRoamingPath(CommandLine.RoamingPath);
                }

                if (!string.IsNullOrEmpty(CommandLine.DalamudRunnerOverride))
                {
                    this.dalamudRunnerOverride = new FileInfo(CommandLine.DalamudRunnerOverride);
                }

                if (CommandLine.NoAutoLogin)
                {
                    GlobalIsDisableAutologin = true;
                }

                if (CommandLine.CanaryAccountAutologin)
                {
                    if (!IsGuardedCanaryAccountLogin(CommandLine))
                    {
                        Log.Error("Canary account login refused: guarded canary launch context is missing.");
                        Shutdown(-1);
                        return;
                    }

                    GlobalIsCanaryAccountAutologin = true;
                }

                if (!string.IsNullOrEmpty(CommandLine.DoGenerateIntegrity))
                {
                    GenerateIntegrity(CommandLine.DoGenerateIntegrity);
                }

                if (CommandLine.DoGenerateLocalizables)
                {
                    GenerateLocalizables();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not parse command line arguments. Please report this error.\n\n" + ex.Message, "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            try
            {
                SetupSettings();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Settings were corrupted, resetting");
                File.Delete(GetConfigPath("launcher"));
                SetupSettings();
            }

            SetupHttpClient();

#if !XL_LOC_FORCEFALLBACKS
            try
            {
                if (App.Settings.LauncherLanguage == null)
                {
                    var currentUiLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                    App.Settings.LauncherLanguage = App.Settings.LauncherLanguage.GetLangFromTwoLetterIso(currentUiLang);
                }

                Log.Information("Trying to set up Loc for language code {0}", App.Settings.LauncherLanguage.GetLocalizationCode());

                if (!App.Settings.LauncherLanguage.IsDefault())
                {
                    Loc.Setup(AppUtil.GetFromResources($"XIVLauncher.Resources.Loc.xl.xl_{App.Settings.LauncherLanguage.GetLocalizationCode()}.json"));
                }
                else
                {
                    Loc.SetupWithFallbacks();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not get language information. Setting up fallbacks.");
                Loc.Setup("{}");
            }
#else
            // Force all fallbacks
            Loc.Setup("{}");
#endif

            try
            {
                Steam = new WindowsSteam();

                if (Settings.AutoStartSteam.GetValueOrDefault(false))
                    Steam.KickoffAsyncStartup(Settings.IsFt.GetValueOrDefault(false) ? Constants.STEAM_FT_APP_ID : Constants.STEAM_APP_ID);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not set up Steam");
            }

            if (!AppUtil.IsDisableUpdates)
            {
                try
                {
                    Log.Information("Starting update check...");

                    this.updateWindow = new UpdateLoadingDialog();
                    this.updateWindow.Show();

                    var updateMgr = new Updates();
                    updateMgr.OnUpdateCheckFinished += OnUpdateCheckFinished;

                    ChangelogWindow changelogWindow = null;

                    try
                    {
                        changelogWindow = new ChangelogWindow(EnvironmentSettings.IsPreRelease);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not load changelog window");
                    }

                    Task.Run(() => updateMgr.Run(EnvironmentSettings.IsPreRelease, changelogWindow));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not dispatch update check");
                    MessageBox.Show(
                        "XIVLauncher could not check for updates. Please check your internet connection or try again.\n\n" + ex,
                        "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Environment.Exit(0);
                }
            }
            else
            {
                // Skip updates immediately
                OnUpdateCheckFinished(true);
            }
        }
    }
}
