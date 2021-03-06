﻿using System;
using System.Windows;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Threading;
using System.ComponentModel;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Reflection;
using log4net;
using log4net.Core;
using System.Deployment.Application;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace mtga_log_client
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly TimeSpan UPDATE_CHECK_INTERVAL = TimeSpan.FromHours(6);


        private static readonly HashSet<String> REQUIRED_FILENAMES = new HashSet<string> { "output_log.txt", "Player.log", "Player-prev.log" };
        private static readonly string STARTUP_REGISTRY_CUSTOM_KEY = "17LandsMTGAClient";
        private static readonly string STARTUP_REGISTRY_LOCATION = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private static readonly string STARTUP_FILENAME = @"\17Lands.com\17Lands MTGA Client.appref-ms";
        private static readonly string DOWNLOAD_URL = "https://github.com/rconroy293/mtga-log-client";
        private static readonly int MESSAGE_HISTORY = 150;

        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private LogParser parser;
        private ApiClient client;
        BackgroundWorker worker;

        private bool isStarted = false;
        private string filePath;
        private string userToken;
        private bool runAtStartup;

        public MainWindow()
        {
            InitializeComponent();

            log4net.Config.XmlConfigurator.Configure();
            log.Info("        =============  Started Logging  =============        ");

            LoadSettings();
            UpdateStartupRegistryKey();
            SetupTrayMinimization();
            StartUpdateCheckThread();

            client = new ApiClient(LogMessage);

            if (!ValidateClientVersion()) return;

            if (ValidateUserInputs(false))
            {
                StartParser();
            }
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            if (!isStarted)
            {
                MessageBox.Show(
                    "Welcome to the 17Lands MTGA client. Please locate your log file and user token, then click 'Start Parsing' to begin.",
                    "Welcome",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (Properties.Settings.Default.do_not_ask_on_close)
            {
                if (Properties.Settings.Default.minimize_on_close)
                {
                    e.Cancel = true;
                    this.Hide();
                }
                else
                {
                    base.OnClosing(e);
                }
                return;
            }

            ExitConfirmation dialog = new ExitConfirmation();
            dialog.ShowDialog();

            switch (dialog.GetExitState())
            {
                case ExitConfirmation.ExitState.EXIT:
                    Properties.Settings.Default.do_not_ask_on_close = dialog.GetRemember();
                    Properties.Settings.Default.minimize_on_close = false;
                    Properties.Settings.Default.Save();
                    base.OnClosing(e);
                    break;
                case ExitConfirmation.ExitState.MINIMIZE:
                    Properties.Settings.Default.do_not_ask_on_close = dialog.GetRemember();
                    Properties.Settings.Default.minimize_on_close = true;
                    Properties.Settings.Default.Save();
                    e.Cancel = true;
                    this.Hide();
                    break;
                case ExitConfirmation.ExitState.CANCEL:
                    Properties.Settings.Default.Save();
                    e.Cancel = true;
                    break;
            }
        }

        public void SetupTrayMinimization()
        {
            InitializeComponent();

            System.Windows.Forms.NotifyIcon ni = new System.Windows.Forms.NotifyIcon();

            System.Windows.Forms.MenuItem trayMenuShow = new System.Windows.Forms.MenuItem();
            trayMenuShow.Index = 0;
            trayMenuShow.Text = "S&how";
            trayMenuShow.Click += new EventHandler(this.ShowClient);

            System.Windows.Forms.MenuItem trayMenuExit = new System.Windows.Forms.MenuItem();
            trayMenuExit.Index = 1;
            trayMenuExit.Text = "E&xit";
            trayMenuExit.Click += new EventHandler(this.ExitClient);

            System.Windows.Forms.MenuItem trayMenuClearPreferences = new System.Windows.Forms.MenuItem();
            trayMenuClearPreferences.Index = 2;
            trayMenuClearPreferences.Text = "C&lear Preferences";
            trayMenuClearPreferences.Click += new EventHandler(this.ClearPreferences);

            System.Windows.Forms.ContextMenu trayMenu = new System.Windows.Forms.ContextMenu();
            trayMenu.MenuItems.AddRange(new System.Windows.Forms.MenuItem[] { trayMenuShow, trayMenuExit, trayMenuClearPreferences });

            ni.Icon = Properties.Resources.icon_white;
            ni.Visible = true;
            ni.DoubleClick += new EventHandler(this.ShowClient);
            ni.ContextMenu = trayMenu;
        }

        private void ShowClient(object Sender, EventArgs e)
        {
            this.Show();
            this.WindowState = WindowState.Normal;
        }

        private void ExitClient(object Sender, EventArgs e)
        {
            base.Close();
        }

        private void ClearPreferences(object Sender, EventArgs e)
        {
            Properties.Settings.Default.do_not_ask_on_close = false;
            Properties.Settings.Default.Save();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized) this.Hide();
            base.OnStateChanged(e);
        }

        private void LoadSettings()
        {
            userToken = Properties.Settings.Default.client_token;
            filePath = Properties.Settings.Default.mtga_log_filename;
            runAtStartup = Properties.Settings.Default.run_at_startup;

            RunAtStartupCheckbox.IsChecked = runAtStartup;
            LogFileTextBox.Text = filePath;
            ClientTokenTextBox.Text = userToken;
        }

        private void SaveSettings()
        {
            Properties.Settings.Default.client_token = userToken;
            Properties.Settings.Default.mtga_log_filename = filePath;
            Properties.Settings.Default.run_at_startup = runAtStartup;
            Properties.Settings.Default.Save();
        }

        private bool ValidateClientVersion()
        {
            var versionValidation = client.GetVersionValidation();
            if (versionValidation.is_supported)
            {
                return true;
            }

            MessageBox.Show(
                "This version of the client is no longer supported. Please update.",
                "Outdated Client",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            System.Diagnostics.Process.Start(DOWNLOAD_URL);
            Application.Current.Shutdown();
            return false;
        }

        private void StartParser()
        {
            if (worker != null && !worker.CancellationPending)
            {
                worker.CancelAsync();
            }
            isStarted = true;
            StartButton.IsEnabled = false;
            StartButton.Content = "Parsing";

            parser = new LogParser(client, userToken, filePath, LogMessage);

            worker = new BackgroundWorker();
            worker.DoWork += parser.ResumeParsing;
            worker.WorkerSupportsCancellation = true;
            worker.RunWorkerAsync();
        }

        private void StopParser()
        {
            if (!isStarted) return;
            LogMessage("Stopped parsing.", Level.Info);

            if (worker != null && !worker.CancellationPending)
            {
                worker.CancelAsync();
            }
            StartButton.IsEnabled = true;
            StartButton.Content = "Start Parsing";
            isStarted = false;
        }

        private bool ValidateLogFileInput(bool promptForUpdate)
        {
            if (File.Exists(LogFileTextBox.Text) && IsValidLogFile(LogFileTextBox.Text)) return true;

            if (promptForUpdate)
            {
                MessageBox.Show(
                    "You must choose a valid log file name from " + String.Join(", ", REQUIRED_FILENAMES),
                    "Choose Filename",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                filePath = ChooseLogFile();
                if (filePath != null)
                {
                    return true;
                }

                MessageBox.Show(
                    "You must enter a log file.",
                    "Choose Valid Log File",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return false;
        }

        private bool ValidateTokenInput(bool promptForUpdate)
        {
            if (IsValidToken(ClientTokenTextBox.Text)) return true;

            if (promptForUpdate)
            {
                MessageBox.Show(
                    "You must enter a valid token from 17lands.com",
                    "Enter Valid Token",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return false;
        }

        private bool ValidateUserInputs(bool promptForUpdate)
        {
            if (!ValidateLogFileInput(promptForUpdate)) return false;
            filePath = LogFileTextBox.Text;

            if (!ValidateTokenInput(promptForUpdate)) return false;
            userToken = ClientTokenTextBox.Text;

            return true;
        }

        private void RunAtStartupCheckbox_onClick(object sender, EventArgs e)
        {
            runAtStartup = RunAtStartupCheckbox.IsChecked.GetValueOrDefault(false);
            SaveSettings();
            UpdateStartupRegistryKey();
        }

        private void UpdateStartupRegistryKey()
        {
            var startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs) + STARTUP_FILENAME;
            if (runAtStartup)
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(STARTUP_REGISTRY_LOCATION, true);
                key.SetValue(STARTUP_REGISTRY_CUSTOM_KEY, startupPath);
            }
            else
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(STARTUP_REGISTRY_LOCATION, true);
                key.DeleteValue(STARTUP_REGISTRY_CUSTOM_KEY, false);
            }
        }

        private void ClientTokenTextBox_onTextChanged(object sender, EventArgs e)
        {
            StopParser();
        }

        private bool IsValidToken(string clientToken)
        {
            var validationResponse = client.GetTokenValidation(clientToken);
            return validationResponse.is_valid;
        }

        private void ChooseFile_onClick(object sender, RoutedEventArgs e)
        {
            string newFilename = ChooseLogFile();
            if (newFilename != null)
            {
                LogFileTextBox.Text = newFilename;
                StopParser();
            }
        }

        private string ChooseLogFile()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt";
            openFileDialog.InitialDirectory = filePath;

            if (openFileDialog.ShowDialog() == true)
            {
                if (IsValidLogFile(openFileDialog.FileName))
                {
                    LogFileTextBox.Text = openFileDialog.FileName;
                    return openFileDialog.FileName;
                }
                else
                {
                    MessageBox.Show(
                        "You must choose a file name from one of " + String.Join(", ", REQUIRED_FILENAMES),
                        "Bad Filename",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            return null;
        }

        private bool IsValidLogFile(string filename)
        {
            foreach (String possibleFilename in REQUIRED_FILENAMES)
            {
                if (filename.EndsWith("\\" + possibleFilename)) {
                    return true;
                }
            }
            return false;
        }

        private void ValidateInputsApplyAndStart()
        {
            if (!ValidateUserInputs(true)) return;
            SaveSettings();
            StartParser();
        }

        private void OpenUserPageInBrowser(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("http://www.17lands.com/user");
        }

        private void OpenAccountPageInBrowser(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("http://www.17lands.com/account");
        }

        private void StartButton_onClick(object sender, EventArgs e)
        {
            if (!isStarted)
            {
                ValidateInputsApplyAndStart();
            }
        }

        private void LogMessage(string message, Level logLevel)
        {
            log.Logger.Log(null, logLevel, message, null);

            if (logLevel >= Level.Info)
            {
                Application.Current.Dispatcher.Invoke((Action)delegate {
                    var item = new ListBoxItem();
                    item.Content = message;
                    MessageListBox.Items.Insert(0, item);

                    while (MessageListBox.Items.Count > MESSAGE_HISTORY)
                    {
                        MessageListBox.Items.RemoveAt(MESSAGE_HISTORY);
                    }
                });
            }
        }

        private static async Task RunPeriodicAsync(Action onTick, TimeSpan initialWait, TimeSpan interval, CancellationToken token)
        {
            if (initialWait > TimeSpan.Zero)
                await Task.Delay(initialWait, token);

            while (!token.IsCancellationRequested)
            {
                onTick?.Invoke();
                if (interval > TimeSpan.Zero) await Task.Delay(interval, token);
            }
        }

        protected void StartUpdateCheckThread()
        {
            _ = RunPeriodicAsync(InstallUpdateSyncWithInfo, UPDATE_CHECK_INTERVAL, UPDATE_CHECK_INTERVAL, CancellationToken.None);
        }

        private void InstallUpdateSyncWithInfo()
        {
            LogMessage("Checking for updates", Level.Info);
            if (!ApplicationDeployment.IsNetworkDeployed)
            {
                LogMessage("Not network deployed", Level.Info);
                return;
            }

            ApplicationDeployment ad = ApplicationDeployment.CurrentDeployment;
            UpdateCheckInfo info;
            try
            {
                info = ad.CheckForDetailedUpdate();
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} while checking for updates", e), Level.Error);
                return;
            }

            if (!info.UpdateAvailable)
            {
                LogMessage("No update available", Level.Info);
                return;
            }

            if (!info.IsUpdateRequired)
            {
                LogMessage("An optional update is available. Please restart the 17Lands client if you wish to apply this update.", Level.Info);
                return;
            }

            MessageBox.Show(
                "17Lands has detected a mandatory update from your current version to version " +
                info.MinimumRequiredVersion.ToString() + ". The application will now install the update and restart.",
                "Update Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            try
            {
                ad.Update();
                MessageBox.Show("17Lands has been upgraded and will now restart.");
                System.Windows.Forms.Application.Restart();
                Application.Current.Shutdown();
            }
            catch (DeploymentDownloadException e)
            {
                LogMessage(String.Format("Error {0} while applying updates", e), Level.Error);
                return;
            }
        }

    }

    delegate void LogMessageFunction(string message, Level logLevel);

    class LogParser
    {
        public const string CLIENT_VERSION = "0.1.21";
        public const string CLIENT_TYPE = "windows";

        private const int SLEEP_TIME = 750;
        private const int BUFFER_SIZE = 65536;
        private static readonly Regex LOG_START_REGEX = new Regex(
            "^\\[(UnityCrossThreadLogger|Client GRE)\\]([\\d/.-]+[ T][\\d]+:[\\d]+:[\\d]+( AM| PM)?)");
        private static readonly Regex TIMESTAMP_REGEX = new Regex(
            "^([\\d/.-]+[ T][\\d]+:[\\d]+:[\\d]+( AM| PM)?)");
        private static readonly Regex LOG_START_REGEX_UNTIMED = new Regex(
            "^\\[(UnityCrossThreadLogger|Client GRE)\\]");
        private static readonly Regex LOG_START_REGEX_UNTIMED_2 = new Regex(
            "^\\(Filename:");
        private static readonly Regex JSON_DICT_REGEX = new Regex("\\{.+\\}");
        private static readonly Regex JSON_LIST_REGEX = new Regex("\\[.+\\]");
        private static readonly Regex ACCOUNT_INFO_REGEX = new Regex(
            ".*Updated account\\. DisplayName:(.*), AccountID:(.*), Token:.*");

        private static readonly List<string> TIME_FORMATS = new List<string>() {
            "yyyy-MM-dd h:mm:ss tt",
            "yyyy-MM-dd HH:mm:ss",
            "M/d/yyyy h:mm:ss tt",
            "M/d/yyyy HH:mm:ss",
            "yyyy/MM/dd h:mm:ss tt",
            "yyyy/MM/dd HH:mm:ss",
            "dd/MM/yyyy HH:mm:ss",
            "dd.MM.yyyy HH:mm:ss"
        };

        private static long SECONDS_AT_YEAR_2000 = 63082281600L;
        private static DateTime YEAR_2000 = new DateTime(2000, 1, 1);

        private bool first = true;
        private long farthestReadPosition = 0;
        private List<string> buffer = new List<string>();
        private Nullable<DateTime> currentLogTime = new DateTime(0);
        private Nullable<DateTime> lastUtcTime = new DateTime(0);
        private string lastRawTime = "";
        private string currentUser = null;
        private string currentConstructedLevel = null;
        private string currentLimitedLevel = null;
        private string currentOpponentLevel = null;
        private string currentMatchId = null;
        private string currentMatchEventId = null;
        private int startingTeamId = -1;
        private readonly Dictionary<int, Dictionary<int, int>> objectsByOwner = new Dictionary<int, Dictionary<int, int>>();
        private readonly Dictionary<int, List<int>> cardsInHand = new Dictionary<int, List<int>>();
        private readonly Dictionary<int, List<List<int>>> drawnHands = new Dictionary<int, List<List<int>>>();
        private readonly Dictionary<int, List<int>> openingHand = new Dictionary<int, List<int>>();

        private const int ERROR_LINES_RECENCY = 10;
        private LinkedList<string> recentLines = new LinkedList<string>();
        private string lastBlob = "";
        private string currentDebugBlob = "";

        private readonly ApiClient apiClient;
        private readonly string apiToken;
        private readonly string filePath;
        private readonly LogMessageFunction messageFunction;

        public LogParser(ApiClient apiClient, string apiToken, string filePath, LogMessageFunction messageFunction)
        {
            this.apiClient = apiClient;
            this.apiToken = apiToken;
            this.filePath = filePath;
            this.messageFunction = messageFunction;
        }

        public void ResumeParsing(object sender, DoWorkEventArgs e)
        {
            LogMessage("Starting parsing of " + filePath, Level.Info);
            BackgroundWorker worker = sender as BackgroundWorker;

            while (!worker.CancellationPending)
            {
                ParseRemainderOfLog(worker);
                Thread.Sleep(SLEEP_TIME);
            }
        }

        public void ParseRemainderOfLog(BackgroundWorker worker) {
            try
            {
                using (FileStream filestream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BUFFER_SIZE))
                {
                    if (first || filestream.Length < farthestReadPosition)
                    {
                        filestream.Position = 0;
                        farthestReadPosition = filestream.Length;
                    }
                    else if (filestream.Length >= farthestReadPosition)
                    {
                        filestream.Position = farthestReadPosition;
                        farthestReadPosition = filestream.Length;
                    }
                    first = false;

                    using (StreamReader reader = new StreamReader(filestream))
                    {
                        while (!worker.CancellationPending)
                        {
                            string line = line = reader.ReadLine();
                            if (line == null)
                            {
                                break;
                            }
                            ProcessLine(line);
                        }
                    }
                }
            }
            catch (FileNotFoundException e)
            {
                LogMessage(String.Format("File not found error while parsing log. If this message persists, please email seventeenlands@gmail.com: {0}", e), Level.Warn);
            }
            catch (Exception e)
            {
                LogError(String.Format("Error parsing log: {0}", e), e.StackTrace, Level.Error);
            }
        }

        private DateTime ParseDateTime(string dateString)
        {
            if (dateString.EndsWith(":") || dateString.EndsWith(" "))
            {
                dateString = dateString.TrimEnd(':', ' ');
            }

            try
            {
                return DateTime.Parse(dateString);
            }
            catch (FormatException)
            {
                // pass
            }

            DateTime readDate;
            foreach (string format in TIME_FORMATS)
            {
                if (DateTime.TryParseExact(dateString, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out readDate))
                {
                    return readDate;
                }
            }
            return DateTime.Parse(dateString);
        }

        private void ProcessLine(string line)
        {
            if (recentLines.Count >= ERROR_LINES_RECENCY) recentLines.RemoveFirst();
            recentLines.AddLast(line);

            if (line.StartsWith("DETAILED LOGS: DISABLED"))
            {
                LogMessage("Warning! Detailed logs disabled in MTGA.", Level.Error);
                ShowMessageBoxAsync(
                    "17Lands needs detailed logging enabled in MTGA. To enable this, click the gear at the top right of MTGA, then 'View Account' (at the bottom), then check 'Detailed Logs', then restart MTGA.",
                    "MTGA Logging Disabled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (line.StartsWith("DETAILED LOGS: ENABLED"))
            {
                LogMessage("Detailed logs enabled in MTGA", Level.Info);
            }

            MaybeHandleAccountInfo(line);

            var timestampMatch = TIMESTAMP_REGEX.Match(line);
            if (timestampMatch.Success)
            {
                lastRawTime = timestampMatch.Groups[1].Value;
                currentLogTime = ParseDateTime(lastRawTime);
            }

            var match = LOG_START_REGEX_UNTIMED.Match(line);
            var match2 = LOG_START_REGEX_UNTIMED_2.Match(line);
            if (match.Success || match2.Success)
            {
                HandleCompleteLogEntry();

                if (match.Success)
                {
                    buffer.Add(line.Substring(match.Length));
                }
                else
                {
                    buffer.Add(line.Substring(match2.Length));
                }

                var timedMatch = LOG_START_REGEX.Match(line);
                if (timedMatch.Success)
                {
                    lastRawTime = timedMatch.Groups[2].Value;
                    currentLogTime = ParseDateTime(lastRawTime);
                }
            }
            else
            {
                buffer.Add(line);
            }
        }

        private void HandleCompleteLogEntry()
        {
            if (buffer.Count == 0)
            {
                return;
            }
            if (!currentLogTime.HasValue)
            {
                buffer.Clear();
                return;
            }

            var fullLog = String.Join("", buffer);
            currentDebugBlob = fullLog;
            try
            {
                HandleBlob(fullLog);
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} while processing {1}", e, fullLog), e.StackTrace, Level.Error);
            }
            lastBlob = fullLog;

            buffer.Clear();
            // currentLogTime = null;
        }

        private void HandleBlob(string fullLog)
        {
            var dictMatch = JSON_DICT_REGEX.Match(fullLog);
            if (!dictMatch.Success)
            {
                return;
            }

            var listMatch = JSON_LIST_REGEX.Match(fullLog);
            if (listMatch.Success && listMatch.Value.Length > dictMatch.Value.Length && listMatch.Index < dictMatch.Index)
            {
                return;
            }

            var blob = ParseBlob(dictMatch.Value);
            blob = ExtractPayload(blob);
            if (blob == null) return;

            DateTime? maybeUtcTimestamp = MaybeGetUtcTimestamp(blob);
            if (maybeUtcTimestamp != null)
            {
                lastUtcTime = maybeUtcTimestamp;
            }

            if (MaybeHandleLogin(blob)) return;
            if (MaybeHandleGameEnd(blob)) return;
            if (MaybeHandleDraftLog(blob)) return;
            if (MaybeHandleDraftPick(blob)) return;
            if (MaybeHandleDeckSubmission(blob)) return;
            if (MaybeHandleDeckSubmissionV3(blob)) return;
            if (MaybeHandleEventCompletion(blob)) return;
            if (MaybeHandleGameRoomStateChanged(blob)) return;
            if (MaybeHandleGreToClientMessages(blob)) return;
            if (MaybeHandleSelfRankInfo(blob)) return;
            if (MaybeHandleMatchCreated(blob)) return;
            if (MaybeHandleCollection(fullLog, blob)) return;
        }

        private JObject ExtractPayload(JObject blob)
        {
            if (!blob.ContainsKey("id"))
            {
                return blob;
            }

            try
            {
                if (blob.ContainsKey("payload"))
                {
                    return blob["payload"].Value<JObject>();
                }

                if (blob.ContainsKey("request"))
                {
                    return ParseBlob(blob["request"].Value<String>());
                }
            }
            catch (Exception)
            {
                // pass
            }

            return blob;
        }

        private DateTime? MaybeGetUtcTimestamp(JObject blob)
        {
            String timestamp;
            if (blob.ContainsKey("timestamp"))
            {
                timestamp = blob["timestamp"].Value<String>();
            }
            else if (blob.ContainsKey("payloadObject") && blob.GetValue("payloadObject").Value<JObject>().ContainsKey("timestamp"))
            {
                timestamp = blob.GetValue("payloadObject").Value<JObject>().GetValue("timestamp").Value<String>();
            }
            else if (blob.ContainsKey("params") 
                && blob.GetValue("params").Value<JObject>().ContainsKey("payloadObject")
                && blob.GetValue("params").Value<JObject>().GetValue("payloadObject").Value<JObject>().ContainsKey("timestamp"))
            {
                timestamp = blob.GetValue("params").Value<JObject>().GetValue("payloadObject").Value<JObject>().GetValue("timestamp").Value<String>();
            }
            else
            {
                return null;
            }

            long secondsSinceYear2000;
            if (long.TryParse(timestamp, out secondsSinceYear2000))
            {
                secondsSinceYear2000 /= 10000000L;
                secondsSinceYear2000 -= SECONDS_AT_YEAR_2000;
                return YEAR_2000.AddSeconds(secondsSinceYear2000);
            }
            else
            {
                DateTime output;
                if (DateTime.TryParse(timestamp, out output))
                {
                    return output;
                }
                else
                {
                    return null;
                }
            }
        }

        private JObject ParseBlob(String blob)
        {
            JsonReaderException firstError = null;
            var endIndex = blob.Length - 1;
            while (true)
            {
                try
                {
                    return JObject.Parse(blob.Substring(0, endIndex + 1));
                }
                catch (JsonReaderException e)
                {
                    if (firstError == null)
                    {
                        firstError = e;
                    }

                    var nextIndex = blob.LastIndexOf("}", endIndex - 1);
                    if (nextIndex == endIndex)
                    {
                        LogError(String.Format("endIndex didn't change: {0}", endIndex), "", Level.Error);
                        throw e;
                    }
                    else if (nextIndex < 0)
                    {
                        throw firstError;
                    }
                    else
                    {
                        endIndex = nextIndex;
                    }
                }
            }
        }

        private String GetRankString(String rankClass, String level, String percentile, String place, String step)
        {
            return String.Format("{0}-{1}-{2}-{3}-{4}", rankClass, level, percentile, place, step == null ? "None" : step);
        }

        private void ClearGameData()
        {
            objectsByOwner.Clear();
            drawnHands.Clear();
            openingHand.Clear();
            startingTeamId = -1;
        }

        private void MaybeHandleAccountInfo(String line)
        {
            if (!line.StartsWith("[Accounts - AccountClient]")) return;
            var match = ACCOUNT_INFO_REGEX.Match(line);
            if (match.Success)
            {
                var screenName = match.Groups[1].Value;
                currentUser = match.Groups[2].Value;

                MTGAAccount account = new MTGAAccount();
                account.token = apiToken;
                account.client_version = CLIENT_VERSION;
                account.player_id = currentUser;
                account.raw_time = lastRawTime;
                account.screen_name = screenName;
                apiClient.PostMTGAAccount(account);
            }

        }

        private bool MaybeHandleLogin(JObject blob)
        {
            JToken token;
            if (!blob.TryGetValue("params", out token)) return false;
            if (!token.Value<JObject>().TryGetValue("messageName", out token)) return false;
            if (!token.Value<String>().Equals("Client.Connected")) return false;

            ClearGameData();

            try
            {
                var payload = blob["params"]["payloadObject"];

                currentUser = payload["playerId"].Value<String>();
                var screenName = payload["screenName"].Value<String>();

                MTGAAccount account = new MTGAAccount();
                account.token = apiToken;
                account.client_version = CLIENT_VERSION;
                account.player_id = currentUser;
                account.raw_time = lastRawTime;

                account.screen_name = screenName;
                apiClient.PostMTGAAccount(account);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing login from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleGameEnd(JObject blob)
        {
            JToken token;
            if (!blob.TryGetValue("params", out token)) return false;
            if (!token.Value<JObject>().TryGetValue("messageName", out token)) return false;
            if (!token.Value<String>().Equals("DuelScene.GameStop")) return false;

            try
            {
                var payload = blob["params"]["payloadObject"];

                var seatId = payload["seatId"].Value<int>();
                var opponentId = seatId == 1 ? 2 : 1;

                var mulligans = new List<List<int>>();
                foreach (JArray hand in payload["mulliganedHands"].Value<JArray>())
                {
                    var mulliganHand = new List<int>();
                    foreach (JObject card in hand)
                    {
                        mulliganHand.Add(card["grpId"].Value<int>());
                    }
                    mulligans.Add(mulliganHand);
                }

                var eventName = payload["eventId"].Value<string>();
                var matchId = payload["matchId"].Value<string>();
                var onPlay = payload["teamId"].Value<int>() == payload["startingTeamId"].Value<int>();
                var won = payload["teamId"].Value<int>() == payload["winningTeamId"].Value<int>();
                var winType = payload["winningType"].Value<string>();
                var gameEndReason = payload["winningReason"].Value<string>();
                var turnCount = payload["turnCount"].Value<int>();

                int duration;
                try
                {
                    duration = payload["secondsCount"].Value<int>();
                }
                catch (OverflowException e)
                {
                    duration = 0;
                }

                return SendHandleGameEnd(seatId, matchId, mulligans, eventName, onPlay, won, winType, gameEndReason, turnCount, duration);
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing game result from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool SendHandleGameEnd(int seatId, string matchId, List<List<int>> mulliganedHands, string eventName, bool onPlay, bool won, string winType, string gameEndReason, int turnCount, int duration)
        {
            try
            {
                var opponentId = seatId == 1 ? 2 : 1;
                var opponentCardIds = new List<int>();
                if (objectsByOwner.ContainsKey(opponentId))
                {
                    foreach (KeyValuePair<int, int> entry in objectsByOwner[opponentId])
                    {
                        opponentCardIds.Add(entry.Value);
                    }
                }

                if (!matchId.Equals(currentMatchId))
                {
                    currentOpponentLevel = null;
                }

                Game game = new Game();
                game.token = apiToken;
                game.client_version = CLIENT_VERSION;
                game.player_id = currentUser;
                game.time = GetDatetimeString(currentLogTime.Value);
                game.utc_time = GetDatetimeString(lastUtcTime.Value);

                game.event_name = eventName;
                game.match_id = matchId;
                game.on_play = onPlay;
                game.won = won;
                game.win_type = winType;
                game.game_end_reason = gameEndReason;

                if (openingHand.ContainsKey(seatId) && openingHand[seatId].Count > 0)
                {
                    game.opening_hand = openingHand[seatId];
                }

                if (drawnHands.ContainsKey(opponentId) && drawnHands[opponentId].Count > 0)
                {
                    game.opponent_mulligan_count = drawnHands[opponentId].Count - 1;
                }

                if (drawnHands.ContainsKey(seatId) && drawnHands[seatId].Count > 0)
                {
                    game.drawn_hands = drawnHands[seatId];
                }

                game.mulligans = mulliganedHands;
                game.turns = turnCount;
                game.limited_rank = currentLimitedLevel;
                game.constructed_rank = currentConstructedLevel;
                game.opponent_rank = currentOpponentLevel;
                game.duration = duration;
                
                game.opponent_card_ids = opponentCardIds;

                ClearGameData();
                apiClient.PostGame(game);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} sending game result", e), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleDraftLog(JObject blob)
        {
            if (!blob.ContainsKey("DraftStatus")) return false;
            if (!"Draft.PickNext".Equals(blob["DraftStatus"].Value<String>())) return false;

            ClearGameData();

            try
            {
                Pack pack = new Pack();
                pack.token = apiToken;
                pack.client_version = CLIENT_VERSION;
                pack.player_id = currentUser;
                pack.time = GetDatetimeString(currentLogTime.Value);
                pack.utc_time = GetDatetimeString(lastUtcTime.Value);

                var cardIds = new List<int>();
                foreach (JToken cardString in blob["DraftPack"].Value<JArray>())
                {
                    cardIds.Add(int.Parse(cardString.Value<String>()));
                }

                pack.event_name = blob["DraftId"].Value<String>().Split(':')[1];
                pack.pack_number = blob["PackNumber"].Value<int>();
                pack.pick_number = blob["PickNumber"].Value<int>();
                pack.card_ids = cardIds;

                apiClient.PostPack(pack);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing draft pack from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleDraftPick(JObject blob)
        {
            if (!blob.ContainsKey("method")) return false;
            if (!"Draft.MakePick".Equals(blob["method"].Value<String>())) return false;

            ClearGameData();

            try
            {
                var parameters = blob["params"].Value<JObject>();
                var draftIdComponents = parameters["draftId"].Value<String>().Split(':');

                Pick pick = new Pick();
                pick.token = apiToken;
                pick.client_version = CLIENT_VERSION;
                pick.player_id = currentUser;
                pick.time = GetDatetimeString(currentLogTime.Value);
                pick.utc_time = GetDatetimeString(lastUtcTime.Value);

                pick.event_name = draftIdComponents[1];
                pick.pack_number = parameters["packNumber"].Value<int>();
                pick.pick_number = parameters["pickNumber"].Value<int>();
                pick.card_id = parameters["cardId"].Value<int>();

                apiClient.PostPick(pick);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing draft pick from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleDeckSubmission(JObject blob)
        {
            if (!blob.ContainsKey("method")) return false;
            if (!"Event.DeckSubmit".Equals(blob["method"].Value<String>())) return false;

            ClearGameData();

            try
            {
                objectsByOwner.Clear();

                Deck deck = new Deck();
                deck.token = apiToken;
                deck.client_version = CLIENT_VERSION;
                deck.player_id = currentUser;
                deck.time = GetDatetimeString(currentLogTime.Value);
                deck.utc_time = GetDatetimeString(lastUtcTime.Value);

                var parameters = blob["params"].Value<JObject>();
                var deckInfo = JObject.Parse(parameters["deck"].Value<String>());

                var maindeckCardIds = GetCardIdsFromDeck(deckInfo["mainDeck"].Value<JArray>());

                if (deckInfo["sideboard"] == null)
                {
                    deck.sideboard_card_ids = new List<int>();
                }
                else
                {
                    deck.sideboard_card_ids = GetCardIdsFromDeck(deckInfo["sideboard"].Value<JArray>());
                }

                deck.event_name = parameters["eventName"].Value<String>();
                deck.maindeck_card_ids = maindeckCardIds;
                deck.is_during_match = false;

                if (deckInfo.ContainsKey("companionGRPId"))
                {
                    deck.companion = deckInfo["companionGRPId"].Value<int>();
                }

                apiClient.PostDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing deck submission from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleDeckSubmissionV3(JObject blob)
        {
            if (!blob.ContainsKey("method")) return false;
            if (!"Event.DeckSubmitV3".Equals(blob["method"].Value<String>())) return false;

            ClearGameData();

            try
            {
                objectsByOwner.Clear();

                Deck deck = new Deck();
                deck.token = apiToken;
                deck.client_version = CLIENT_VERSION;
                deck.player_id = currentUser;
                deck.time = GetDatetimeString(currentLogTime.Value);
                deck.utc_time = GetDatetimeString(lastUtcTime.Value);

                var parameters = blob["params"].Value<JObject>();
                var deckInfo = JObject.Parse(parameters["deck"].Value<String>());

                var maindeckCardIds = GetCardIdsFromDecklistV3(deckInfo["mainDeck"].Value<JArray>());

                if (deckInfo["sideboard"] == null)
                {
                    deck.sideboard_card_ids = new List<int>();
                }
                else
                {
                    deck.sideboard_card_ids = GetCardIdsFromDecklistV3(deckInfo["sideboard"].Value<JArray>());
                }

                deck.event_name = parameters["eventName"].Value<String>();
                deck.maindeck_card_ids = maindeckCardIds;
                deck.is_during_match = false;

                if (deckInfo.ContainsKey("companionGRPId"))
                {
                    deck.companion = deckInfo["companionGRPId"].Value<int>();
                }

                apiClient.PostDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing v3 deck submission from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleEventCompletion(JObject blob)
        {
            if (!blob.ContainsKey("CurrentEventState")) return false;
            if (!"DoneWithMatches".Equals(blob["CurrentEventState"].Value<String>())) return false;

            try
            {
                Event event_ = new Event();
                event_.token = apiToken;
                event_.client_version = CLIENT_VERSION;
                event_.player_id = currentUser;
                event_.time = GetDatetimeString(currentLogTime.Value);
                event_.utc_time = GetDatetimeString(lastUtcTime.Value);

                event_.event_name = blob["InternalEventName"].Value<String>();
                if (blob["ModuleInstanceData"]["HasPaidEntry"] != null)
                {
                    event_.entry_fee = blob["ModuleInstanceData"]["HasPaidEntry"].Value<String>();
                }
                else
                {
                    event_.entry_fee = "None";
                }
                event_.wins = blob["ModuleInstanceData"]["WinLossGate"]["CurrentWins"].Value<int>();
                event_.losses = blob["ModuleInstanceData"]["WinLossGate"]["CurrentLosses"].Value<int>();

                apiClient.PostEvent(event_);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing event completion from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleGreMessage_DeckSubmission(JToken blob)
        {
            if (!"GREMessageType_SubmitDeckReq".Equals(blob["type"].Value<string>())) return false;

            try
            {
                objectsByOwner.Clear();

                Deck deck = new Deck();
                deck.token = apiToken;
                deck.client_version = CLIENT_VERSION;
                deck.player_id = currentUser;
                deck.time = GetDatetimeString(currentLogTime.Value);
                deck.utc_time = GetDatetimeString(lastUtcTime.Value);

                deck.event_name = null;
                deck.maindeck_card_ids = JArrayToIntList(blob["submitDeckReq"]["deck"]["deckCards"].Value<JArray>());
                if (blob["submitDeckReq"]["deck"]["sideboardCards"] == null) {
                    deck.sideboard_card_ids = new List<int>();
                }
                else
                {
                    deck.sideboard_card_ids = JArrayToIntList(blob["submitDeckReq"]["deck"]["sideboardCards"].Value<JArray>());
                }
                deck.is_during_match = true;

                apiClient.PostDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing GRE deck submission from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleGreMessage_GameState(JToken blob)
        {
            if (!"GREMessageType_GameStateMessage".Equals(blob["type"].Value<string>())) return false;

            try
            {
                var gameStateMessage = blob["gameStateMessage"].Value<JObject>();
                if (blob.Value<JObject>().ContainsKey("systemSeatIds"))
                {
                    MaybeHandleGameOverStage(blob["systemSeatIds"].Value<JArray>()[0].Value<int>(), gameStateMessage);
                }
                if (gameStateMessage.ContainsKey("gameObjects"))
                {
                    foreach (JToken gameObject in gameStateMessage["gameObjects"].Value<JArray>())
                    {
                        if (!"GameObjectType_Card".Equals(gameObject["type"].Value<string>())) continue;

                        var owner = gameObject["ownerSeatId"].Value<int>();
                        var instanceId = gameObject["instanceId"].Value<int>();
                        var cardId = gameObject["overlayGrpId"].Value<int>();

                        if (!objectsByOwner.ContainsKey(owner))
                        {
                            objectsByOwner.Add(owner, new Dictionary<int, int>());
                        }
                        objectsByOwner[owner][instanceId] = cardId;
                    }

                }
                if (gameStateMessage.ContainsKey("zones"))
                {
                    foreach (JObject zone in gameStateMessage["zones"].Value<JArray>())
                    {
                        if (!"ZoneType_Hand".Equals(zone["type"].Value<string>())) continue;

                        var owner = zone["ownerSeatId"].Value<int>();
                        var cards = new List<int>();
                        if (zone.ContainsKey("objectInstanceIds"))
                        {
                            var playerObjects = objectsByOwner.ContainsKey(owner) ? objectsByOwner[owner] : new Dictionary<int, int>();
                            foreach (JToken objectInstanceId in zone["objectInstanceIds"].Value<JArray>())
                            {
                                if (objectInstanceId != null && playerObjects.ContainsKey(objectInstanceId.Value<int>()))
                                {
                                    cards.Add(playerObjects[objectInstanceId.Value<int>()]);
                                }
                            }
                        }
                        cardsInHand[owner] = cards;
                    }

                }
                if (gameStateMessage.ContainsKey("players"))
                {
                    foreach (JObject player in gameStateMessage.GetValue("players").Value<JArray>())
                    {
                        if (player.ContainsKey("pendingMessageType") && player.GetValue("pendingMessageType").Value<string>().Equals("ClientMessageType_MulliganResp"))
                        {
                            JToken tmp;
                            if (gameStateMessage.ContainsKey("turnInfo"))
                            {
                                var turnInfo = gameStateMessage.GetValue("turnInfo").Value<JObject>();
                                if (startingTeamId == -1 && turnInfo.TryGetValue("activePlayer", out tmp))
                                {
                                    startingTeamId = tmp.Value<int>();
                                }
                            }

                            var playerId = player.GetValue("systemSeatNumber").Value<int>();

                            if (!drawnHands.ContainsKey(playerId))
                            {
                                drawnHands.Add(playerId, new List<List<int>>());
                            }
                            var mulliganCount = 0;
                            if (player.TryGetValue("mulliganCount", out tmp))
                            {
                                mulliganCount = tmp.Value<int>();
                            }
                            if (mulliganCount == drawnHands[playerId].Count)
                            {
                                drawnHands[playerId].Add(new List<int>(cardsInHand[playerId]));
                            }
                        }
                    }
                }
                if (gameStateMessage.ContainsKey("turnInfo"))
                {
                    var turnInfo = gameStateMessage.GetValue("turnInfo").Value<JObject>();
                    if (openingHand.Count == 0 && turnInfo.ContainsKey("phase") && turnInfo.ContainsKey("step") && turnInfo.ContainsKey("turnNumber"))
                    {
                        if (turnInfo.GetValue("phase").Value<string>().Equals("Phase_Beginning") && turnInfo.GetValue("step").Value<string>().Equals("Step_Upkeep") && turnInfo.GetValue("turnNumber").Value<int>() == 1)
                        {
                            LogMessage("Recording opening hands", Level.Info);
                            foreach (int playerId in cardsInHand.Keys)
                            {
                                openingHand[playerId] = new List<int>(cardsInHand[playerId]);
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing GRE message from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleGameOverStage(int seatId, JObject gameStateMessage)
        {
            if (!gameStateMessage.ContainsKey("gameInfo")) return false;
            var gameInfo = gameStateMessage["gameInfo"].Value<JObject>();
            if (!gameInfo.ContainsKey("stage") || !gameInfo["stage"].Value<String>().Equals("GameStage_GameOver")) return false;
            if (!gameInfo.ContainsKey("results")) return false;

            var results = gameInfo["results"].Value<JArray>();
            for (int i = results.Count - 1; i >= 0; i--)
            {
                var result = results[i].Value<JObject>();
                if (!result.ContainsKey("scope") || !result["scope"].Value<String>().Equals("MatchScope_Game")) continue;

                var matchId = gameInfo["matchID"].Value<String>();
                var eventName = currentMatchEventId;

                int turnNumber = -1;
                if (gameStateMessage.ContainsKey("turnInfo") && gameStateMessage["turnInfo"].Value<JObject>().ContainsKey("turnNumber"))
                {
                    turnNumber = gameStateMessage["turnInfo"].Value<JObject>()["turnNumber"].Value<int>();
                }
                else if (gameStateMessage.ContainsKey("players"))
                {
                    turnNumber = 0;
                    foreach (JToken turnToken in gameStateMessage["players"].Value<JArray>())
                    {
                        turnNumber += turnToken.Value<JObject>()["turnNumber"].Value<int>();
                    }
                }

                var mulligans = new List<List<int>>();
                if (drawnHands.ContainsKey(seatId) && drawnHands[seatId].Count > 0)
                {
                    mulligans = drawnHands[seatId].GetRange(0, drawnHands[seatId].Count - 1);
                }
                var onPlay = seatId.Equals(startingTeamId);
                var won = seatId.Equals(result["winningTeamId"].Value<int>());
                var winType = result["result"].Value<String>();
                var gameEndReason = result["reason"].Value<String>();
                var duration = -1;

                return SendHandleGameEnd(seatId, matchId, mulligans, eventName, onPlay, won, winType, gameEndReason, turnNumber, duration);
            }
            return false;
        }

        private bool MaybeHandleGameRoomStateChanged(JObject blob)
        {
            if (!blob.ContainsKey("matchGameRoomStateChangedEvent")) return false;
            if (!blob["matchGameRoomStateChangedEvent"].Value<JObject>().ContainsKey("gameRoomInfo")) return false;
            if (!blob["matchGameRoomStateChangedEvent"].Value<JObject>()["gameRoomInfo"].Value<JObject>().ContainsKey("gameRoomConfig")) return false;

            var gameRoomConfig = blob["matchGameRoomStateChangedEvent"].Value<JObject>()["gameRoomInfo"].Value<JObject>()["gameRoomConfig"].Value<JObject>();
            if (gameRoomConfig.ContainsKey("eventId") && gameRoomConfig.ContainsKey("matchId"))
            {
                currentMatchEventId = gameRoomConfig["eventId"].Value<String>();
            }
            return false;
        }

        private bool MaybeHandleGreToClientMessages(JObject blob)
        {
            if (!blob.ContainsKey("greToClientEvent")) return false;
            if (!blob["greToClientEvent"].Value<JObject>().ContainsKey("greToClientMessages")) return false;

            try
            {
                foreach (JToken message in blob["greToClientEvent"]["greToClientMessages"])
                {
                    if (MaybeHandleGreMessage_DeckSubmission(message)) continue;
                    if (MaybeHandleGreMessage_GameState(message)) continue;
                }
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing event completion from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleSelfRankInfo(JObject blob)
        {
            if (!blob.ContainsKey("limitedStep")) return false;

            try
            {
                currentLimitedLevel = GetRankString(
                    blob["limitedClass"].Value<String>(),
                    blob["limitedLevel"].Value<String>(),
                    blob["limitedPercentile"].Value<String>(),
                    blob["limitedLeaderboardPlace"].Value<String>(),
                    blob["limitedStep"].Value<String>()
                );
                currentConstructedLevel = GetRankString(
                    blob["constructedClass"].Value<String>(),
                    blob["constructedLevel"].Value<String>(),
                    blob["constructedPercentile"].Value<String>(),
                    blob["constructedLeaderboardPlace"].Value<String>(),
                    blob["constructedStep"].Value<String>()
                );

                if (blob.ContainsKey("playerId"))
                {
                    currentUser = blob["playerId"].Value<String>();
                }

                LogMessage(String.Format("Parsed rank info for {0} as limited {1} and constructed {2}", currentUser, currentLimitedLevel, currentConstructedLevel), Level.Info);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing self rank info from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleMatchCreated(JObject blob)
        {
            if (!blob.ContainsKey("opponentRankingClass")) return false;

            ClearGameData();

            try
            {
                currentOpponentLevel = GetRankString(
                    blob["opponentRankingClass"].Value<String>(),
                    blob["opponentRankingTier"].Value<String>(),
                    blob["opponentMythicPercentile"].Value<String>(),
                    blob["opponentMythicLeaderboardPlace"].Value<String>(),
                    null
                );

                if (blob.ContainsKey("matchId"))
                {
                    currentMatchId = blob["matchId"].Value<String>();
                }

                LogMessage(String.Format("Parsed opponent rank info as {0} in match {1}", currentOpponentLevel, currentMatchId), Level.Info);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing match creation from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleCollection(String fullLog, JObject blob)
        {
            if (!fullLog.Contains(" PlayerInventory.GetPlayerCardsV3 ")) return false;
            if (blob.ContainsKey("method")) return false;

            try
            {
                Collection collection = new Collection();
                collection.token = apiToken;
                collection.client_version = CLIENT_VERSION;
                collection.player_id = currentUser;
                collection.time = GetDatetimeString(currentLogTime.Value);
                collection.utc_time = GetDatetimeString(lastUtcTime.Value);
                collection.card_counts = blob.ToObject<Dictionary<string, int>>();

                apiClient.PostCollection(collection);

                LogMessage(String.Format("Parsed opponent rank info as {0} in match {1}", currentOpponentLevel, currentMatchId), Level.Info);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing collection from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private void LogMessage(string message, Level logLevel)
        {
            messageFunction(message, logLevel);
        }

        private void LogError(string message, string stacktrace, Level logLevel)
        {
            LogMessage(message, logLevel);

            messageFunction(String.Format("Current blob: {0}", currentDebugBlob), Level.Debug);
            messageFunction(String.Format("Previous blob: {0}", lastBlob), Level.Debug);
            messageFunction("Recent lines:", Level.Debug);
            foreach (string line in recentLines)
            {
                messageFunction(line, Level.Debug);
            }

            var errorInfo = new ErrorInfo();
            errorInfo.client_version = CLIENT_VERSION;
            errorInfo.token = apiToken;
            errorInfo.blob = currentDebugBlob;
            errorInfo.recent_lines = new List<string>(recentLines);
            errorInfo.stacktrace = String.Format("{0}\r\n{1}", message, stacktrace);
            apiClient.PostErrorInfo(errorInfo);
        }

        private string GetDatetimeString(DateTime value)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private List<int> JArrayToIntList(JArray arr)
        {
            var output = new List<int>();
            foreach (JToken token in arr)
            {
                output.Add(token.Value<int>());
            }
            return output;
        }

        private List<int> GetCardIdsFromDeck(JArray decklist)
        {
            var cardIds = new List<int>();
            foreach (JObject cardInfo in decklist)
            {
                int cardId;
                if (cardInfo.ContainsKey("id"))
                {
                    cardId = cardInfo["id"].Value<int>();
                }
                else
                {
                    cardId = cardInfo["Id"].Value<int>();
                }

                for (int i = 0; i < cardInfo["Quantity"].Value<int>(); i++)
                {
                    cardIds.Add(cardId);
                }
            }
            return cardIds;
        }

        private List<int> GetCardIdsFromDecklistV3(JArray decklist)
        {
            var cardIds = new List<int>();
            for (int i = 0; i < decklist.Count / 2; i++)
            {
                var cardId = decklist[2 * i].Value<int>();
                var count = decklist[2 * i + 1].Value<int>();
                for (int j = 0; j < count; j++)
                {
                    cardIds.Add(cardId);
                }
            }
            return cardIds;
        }

        private delegate void ShowMessageBoxDelegate(string strMessage, string strCaption, MessageBoxButton enmButton, MessageBoxImage enmImage);
        private static void ShowMessageBox(string strMessage, string strCaption, MessageBoxButton enmButton, MessageBoxImage enmImage)
        {
            MessageBox.Show(strMessage, strCaption, enmButton, enmImage);
        }
        private static void ShowMessageBoxAsync(string strMessage, string strCaption, MessageBoxButton enmButton, MessageBoxImage enmImage)
        {
            ShowMessageBoxDelegate caller = new ShowMessageBoxDelegate(ShowMessageBox);
            caller.BeginInvoke(strMessage, strCaption, enmButton, enmImage, null, null);
        }

    }

    class ApiClient
    {
        private const string API_BASE_URL = "https://www.17lands.com";
        private const string ENDPOINT_ACCOUNT = "api/account";
        private const string ENDPOINT_COLLECTION = "collection";
        private const string ENDPOINT_DECK = "deck";
        private const string ENDPOINT_EVENT = "event";
        private const string ENDPOINT_GAME = "game";
        private const string ENDPOINT_PACK = "pack";
        private const string ENDPOINT_PICK = "pick";
        private const string ENDPOINT_CLIENT_VERSION_VALIDATION = "api/version_validation";
        private const string ENDPOINT_TOKEN_VERSION_VALIDATION = "api/token_validation";
        private const string ENDPOINT_ERROR_INFO = "api/client_errors";

        private static readonly DataContractJsonSerializerSettings SIMPLE_SERIALIZER_SETTINGS = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };

        private static readonly DataContractJsonSerializer SERIALIZER_MTGA_ACCOUNT = new DataContractJsonSerializer(typeof(MTGAAccount));
        private static readonly DataContractJsonSerializer SERIALIZER_PACK = new DataContractJsonSerializer(typeof(Pack));
        private static readonly DataContractJsonSerializer SERIALIZER_PICK = new DataContractJsonSerializer(typeof(Pick));
        private static readonly DataContractJsonSerializer SERIALIZER_DECK = new DataContractJsonSerializer(typeof(Deck));
        private static readonly DataContractJsonSerializer SERIALIZER_GAME = new DataContractJsonSerializer(typeof(Game));
        private static readonly DataContractJsonSerializer SERIALIZER_EVENT = new DataContractJsonSerializer(typeof(Event));
        private static readonly DataContractJsonSerializer SERIALIZER_COLLECTION = new DataContractJsonSerializer(typeof(Collection), SIMPLE_SERIALIZER_SETTINGS);
        private static readonly DataContractJsonSerializer SERIALIZER_ERROR_INFO= new DataContractJsonSerializer(typeof(ErrorInfo));

        private HttpClient client;
        private readonly LogMessageFunction messageFunction;

        private const int ERROR_COOLDOWN_MINUTES = 2;
        private DateTime? lastErrorPosted = null;

        [DataContract]
        public class VersionValidationResponse
        {
            [DataMember]
            internal bool is_supported;
            [DataMember]
            internal string latest_version;
        }

        [DataContract]
        public class TokenValidationResponse
        {
            [DataMember]
            internal bool is_valid;
        }

        public ApiClient(LogMessageFunction messageFunction)
        {
            this.messageFunction = messageFunction;
            InitializeClient();
        }

        public void InitializeClient()
        {
            client = new HttpClient();
            client.BaseAddress = new Uri(API_BASE_URL);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public void StopClient()
        {
            client.Dispose();
        }

        private Stream GetJson(string endpoint)
        {
            HttpResponseMessage response = client.GetAsync(endpoint).Result;
            if (response.IsSuccessStatusCode)
            {
                return response.Content.ReadAsStreamAsync().Result;
            }
            else
            {
                LogMessage(String.Format("Got error response {0} ({1})", (int) response.StatusCode, response.ReasonPhrase), Level.Warn);
                return null;
            }
        }

        private void PostJson(string endpoint, String blob)
        {
            LogMessage(String.Format("Posting {0} of {1}", endpoint, blob), Level.Info);
            var content = new StringContent(blob, Encoding.UTF8, "application/json");
            var response = client.PostAsync(endpoint, content).Result;
            if (!response.IsSuccessStatusCode)
            {
                LogMessage(String.Format("Got error response {0} ({1})", (int)response.StatusCode, response.ReasonPhrase), Level.Warn);
            }
        }

        public VersionValidationResponse GetVersionValidation()
        {
            var jsonResponse = GetJson(ENDPOINT_CLIENT_VERSION_VALIDATION + "?client=" + LogParser.CLIENT_TYPE + "&version=" + LogParser.CLIENT_VERSION);
            if (jsonResponse == null)
            {
                return null;
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(VersionValidationResponse));
            return ((VersionValidationResponse)serializer.ReadObject(jsonResponse));
        }

        public TokenValidationResponse GetTokenValidation(string token)
        {
            var jsonResponse = GetJson(ENDPOINT_TOKEN_VERSION_VALIDATION + "?token=" + token);
            if (jsonResponse == null)
            {
                return null;
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(TokenValidationResponse));
            return ((TokenValidationResponse)serializer.ReadObject(jsonResponse));
        }

        public void PostMTGAAccount(MTGAAccount account)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_MTGA_ACCOUNT.WriteObject(stream, account);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_ACCOUNT, jsonString);
        }

        public void PostPack(Pack pack)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_PACK.WriteObject(stream, pack);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_PACK, jsonString);
        }

        public void PostPick(Pick pick)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_PICK.WriteObject(stream, pick);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_PICK, jsonString);
        }

        public void PostDeck(Deck deck)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_DECK.WriteObject(stream, deck);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_DECK, jsonString);
        }

        public void PostGame(Game game)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_GAME.WriteObject(stream, game);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_GAME, jsonString);
        }

        public void PostEvent(Event event_)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_EVENT.WriteObject(stream, event_);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_EVENT, jsonString);
        }

        public void PostCollection(Collection collection)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_COLLECTION.WriteObject(stream, collection);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_COLLECTION, jsonString);
        }

        public void PostErrorInfo(ErrorInfo errorInfo)
        {
            DateTime now = DateTime.UtcNow;
            if (lastErrorPosted != null && now < lastErrorPosted.GetValueOrDefault().AddMinutes(ERROR_COOLDOWN_MINUTES))
            {
                LogMessage(String.Format("Waiting to post another error, as last message was sent recently at {0}", lastErrorPosted), Level.Warn);
                return;
            }
            else
            {
                lastErrorPosted = now;
                MemoryStream stream = new MemoryStream();
                SERIALIZER_ERROR_INFO.WriteObject(stream, errorInfo);
                string jsonString = Encoding.UTF8.GetString(stream.ToArray());
                PostJson(ENDPOINT_ERROR_INFO, jsonString);
            }

        }

        private void LogMessage(string message, Level logLevel)
        {
            messageFunction(message, logLevel);
        }
    }

    [DataContract]
    internal class MTGAAccount
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string screen_name;
        [DataMember]
        internal string raw_time;
    }
    [DataContract]
    internal class Pack
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal string time;
        [DataMember]
        internal int pack_number;
        [DataMember]
        internal int pick_number;
        [DataMember]
        internal List<int> card_ids;
        [DataMember]
        internal string utc_time;
    }
    [DataContract]
    internal class Pick
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal string time;
        [DataMember]
        internal int pack_number;
        [DataMember]
        internal int pick_number;
        [DataMember]
        internal int card_id;
        [DataMember]
        internal string utc_time;
    }
    [DataContract]
    internal class Deck
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string time;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal List<int> maindeck_card_ids;
        [DataMember]
        internal List<int> sideboard_card_ids;
        [DataMember]
        internal int companion;
        [DataMember]
        internal bool is_during_match;
        [DataMember]
        internal string utc_time;
    }
    [DataContract]
    internal class Game
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal string match_id;
        [DataMember]
        internal string time;
        [DataMember]
        internal bool on_play;
        [DataMember]
        internal bool won;
        [DataMember]
        internal string win_type;
        [DataMember]
        internal string game_end_reason;
        [DataMember]
        internal List<int> opening_hand;
        [DataMember]
        internal List<List<int>> mulligans;
        [DataMember]
        internal List<List<int>> drawn_hands;
        [DataMember]
        internal int opponent_mulligan_count;
        [DataMember]
        internal int turns;
        [DataMember]
        internal int duration;
        [DataMember]
        internal List<int> opponent_card_ids;
        [DataMember]
        internal string utc_time;
        [DataMember]
        internal string limited_rank;
        [DataMember]
        internal string constructed_rank;
        [DataMember]
        internal string opponent_rank;
    }
    [DataContract]
    internal class Event
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal string time;
        [DataMember]
        internal string entry_fee;
        [DataMember]
        internal int wins;
        [DataMember]
        internal int losses;
        [DataMember]
        internal string utc_time;
    }
    [DataContract]
    internal class ErrorInfo
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string stacktrace;
        [DataMember]
        internal string blob;
        [DataMember]
        internal List<string> recent_lines;
    }
    [DataContract]
    internal class Collection
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string time;
        [DataMember]
        internal string utc_time;
        [DataMember]
        internal Dictionary<string, int> card_counts;
    }
}
