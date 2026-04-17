using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace CrateGameTools;

public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b) return b ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}

public class HotkeyDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is HotkeyInfo hk) return hk.ToString();
        return "None";
    }
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SaveItem : INotifyPropertyChanged
{
    private string _content = "";
    private string _notes = "";
    private DateTime _timestamp;

    public string Content 
    { 
        get => _content; 
        set { _content = value; OnPropertyChanged(); } 
    }
    public string Notes 
    { 
        get => _notes; 
        set { _notes = value; OnPropertyChanged(); } 
    }
    public DateTime Timestamp 
    { 
        get => _timestamp; 
        set { _timestamp = value; OnPropertyChanged(); } 
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class MaintenanceItem
{
    public string Name { get; set; } = "";
    public DateTime StartTime { get; set; }
    public string Status { get; set; } = "";
    public string DisplayString => $"{StartTime.ToLocalTime():g} - {Name} ({Status})";
}

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public class BoosterTimer : INotifyPropertyChanged
{
    private int _remainingSeconds = 0;
    private int _level = 0;
    private bool _isRunning = false;
    private bool _isCooldown = false;
    private string _name = "";

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public int RemainingSeconds { get => _remainingSeconds; set { _remainingSeconds = value; OnPropertyChanged(); } }
    public bool IsRunning { get => _isRunning; set { _isRunning = value; OnPropertyChanged(); } }
    public bool IsCooldown { get => _isCooldown; set { _isCooldown = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public int GetDuration(int level) => 1500 + (level * 30);
}

public class VrcLogWatcher
{
    public event EventHandler<string>? ManualBackupDetected;
    public event EventHandler<double>? ResearchDetected;
    public event EventHandler<string>? StatusChanged;

    private FileSystemWatcher? _watcher;
    private string? _currentLogFile;
    private long _lastPosition = 0;
    private readonly string _logDirectory;
    private bool _isRunning = false;

    public VrcLogWatcher()
    {
        _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "..", "LocalLow", "VRChat", "VRChat");
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;

        if (!Directory.Exists(_logDirectory))
        {
            StatusChanged?.Invoke(this, "VRChat log directory not found.");
            return;
        }

        _watcher = new FileSystemWatcher(_logDirectory, "output_log_*.txt");
        _watcher.Created += (s, e) => UpdateCurrentLogFile();
        _watcher.EnableRaisingEvents = true;

        UpdateCurrentLogFile();
        
        // Polling as a fallback and for research updates
        var timer = new DispatcherTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (s, e) => ReadNewLines();
        timer.Start();
    }

    private void UpdateCurrentLogFile()
    {
        try
        {
            var directory = new DirectoryInfo(_logDirectory);
            var latestFile = directory.GetFiles("output_log_*.txt")
                                      .OrderByDescending(f => f.LastWriteTime)
                                      .FirstOrDefault();

            if (latestFile != null && latestFile.FullName != _currentLogFile)
            {
                _currentLogFile = latestFile.FullName;
                _lastPosition = latestFile.Length; // Start from end of file to avoid processing old data
                StatusChanged?.Invoke(this, $"Watching: {latestFile.Name}");
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Error: {ex.Message}");
        }
    }

    private void ReadNewLines()
    {
        if (string.IsNullOrEmpty(_currentLogFile) || !File.Exists(_currentLogFile))
        {
            UpdateCurrentLogFile();
            return;
        }

        try
        {
            using (var stream = new FileStream(_currentLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length < _lastPosition)
                {
                    _lastPosition = 0; // Log file rotated or cleared
                }

                if (stream.Length == _lastPosition) return;

                stream.Seek(_lastPosition, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        ProcessLine(line);
                    }
                    _lastPosition = stream.Position;
                }
            }
        }
        catch (Exception ex)
        {
            // Silently ignore or update status if it's a persistent error
        }
    }

    private void ProcessLine(string line)
    {
        // Check for manual backup
        if (line.Contains("[SaveSystem][MANUAL BACKUP]"))
        {
            int index = line.IndexOf("ENC1:");
            if (index != -1)
            {
                string save = line.Substring(index);
                ManualBackupDetected?.Invoke(this, save);
            }
        }

        // Check for research progress
        // Format: 2026.04.17 19:47:00 Debug      -  [ResearchManager] Auto-researched Yellow Vinyl +29819.04
        if (line.Contains("[ResearchManager] Auto-researched"))
        {
            int plusIndex = line.LastIndexOf('+');
            if (plusIndex != -1)
            {
                string amountStr = line.Substring(plusIndex + 1).Trim();
                if (double.TryParse(amountStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double amount))
                {
                    ResearchDetected?.Invoke(this, amount);
                }
            }
        }
    }
}

public class HotkeyInfo : INotifyPropertyChanged
{
    private Key _key;
    private ModifierKeys _modifiers;
    private string _actionName = "";

    public Key Key { get => _key; set { _key = value; OnPropertyChanged(); } }
    public ModifierKeys Modifiers { get => _modifiers; set { _modifiers = value; OnPropertyChanged(); } }
    public string ActionName { get => _actionName; set { _actionName = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public override string ToString()
    {
        if (Key == Key.None) return "None";
        var sb = new System.Text.StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(Key);
        return sb.ToString();
    }
}

public partial class MainWindow : Window
{
    private ObservableCollection<HotkeyInfo> _hotkeys = new ObservableCollection<HotkeyInfo>();
    private HwndSource? _source;
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void SetupHotkeys()
    {
        _hotkeys.Add(new HotkeyInfo { ActionName = "Timer Start/Reset", Key = Key.None });
        _hotkeys.Add(new HotkeyInfo { ActionName = "Vending Start/Reset", Key = Key.None });
        _hotkeys.Add(new HotkeyInfo { ActionName = "Redline Start/Reset", Key = Key.None });
        _hotkeys.Add(new HotkeyInfo { ActionName = "Payday Start/Reset", Key = Key.None });
        _hotkeys.Add(new HotkeyInfo { ActionName = "Lucky Break Start/Reset", Key = Key.None });
        _hotkeys.Add(new HotkeyInfo { ActionName = "Hyperfocus Start/Reset", Key = Key.None });
        _hotkeys.Add(new HotkeyInfo { ActionName = "More Mods Start/Reset", Key = Key.None });
        _hotkeys.Add(new HotkeyInfo { ActionName = "Toggle Overlay", Key = Key.None });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source.AddHook(HwndHook);
        RegisterAllHotkeys();
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id >= 0 && id < _hotkeys.Count)
            {
                var hotkey = _hotkeys[id];
                HandleHotkeyAction(hotkey.ActionName);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void HandleHotkeyAction(string actionName)
    {
        Dispatcher.Invoke(() =>
        {
            switch (actionName)
            {
                case "Timer Start/Reset":
                    TimerCard_MouseLeftButtonUp(null!, null!);
                    break;
                case "Vending Start/Reset":
                    VendingCard_MouseLeftButtonUp(null!, null!);
                    break;
                case "Toggle Overlay":
                    ToggleOverlay_Click(null!, null!);
                    break;
                default:
                    if (actionName.EndsWith(" Start/Reset"))
                    {
                        string boosterName = actionName.Replace(" Start/Reset", "");
                        var booster = _boosters.FirstOrDefault(b => b.Name == boosterName);
                        if (booster != null)
                        {
                            RestartBooster(booster);
                        }
                    }
                    break;
            }
        });
    }

    private void DecreaseLevel_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(GlobalBoosterLevel.Text, out int level))
        {
            if (level > 1)
            {
                GlobalBoosterLevel.Text = (level - 1).ToString();
            }
        }
    }

    private void IncreaseLevel_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(GlobalBoosterLevel.Text, out int level))
        {
            GlobalBoosterLevel.Text = (level + 1).ToString();
        }
    }

    private void LevelTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    private void GlobalLevel_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoading && GlobalBoosterLevel != null)
        {
            if (int.TryParse(GlobalBoosterLevel.Text, out int level))
            {
                if (level < 1)
                {
                    GlobalBoosterLevel.Text = "1";
                    return;
                }
                
                // Update durations for boosters that are NOT running and NOT in cooldown
                foreach (var booster in _boosters)
                {
                    if (!booster.IsRunning && !booster.IsCooldown)
                    {
                        booster.RemainingSeconds = booster.GetDuration(level);
                    }
                }
                SaveSettings();
            }
        }
    }

    private void RegisterAllHotkeys()
    {
        if (_source == null) return;
        for (int i = 0; i < _hotkeys.Count; i++)
        {
            UnregisterHotKey(_source.Handle, i);
            var hk = _hotkeys[i];
            if (hk.Key != Key.None)
            {
                uint modifiers = 0;
                if (hk.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 0x0002;
                if (hk.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 0x0001;
                if (hk.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 0x0004;
                if (hk.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 0x0008;

                int vk = KeyInterop.VirtualKeyFromKey(hk.Key);
                RegisterHotKey(_source.Handle, i, modifiers, (uint)vk);
            }
        }
    }

    private void RestartBooster(BoosterTimer booster)
    {
        int level = 1;
        if (int.TryParse(GlobalBoosterLevel.Text, out int l)) level = l;
        
        booster.RemainingSeconds = booster.GetDuration(level);
        booster.IsRunning = true;
        booster.IsCooldown = false;
        SaveSettings();
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var textBox = (System.Windows.Controls.TextBox)sender;
        var hotkey = (HotkeyInfo)textBox.Tag;

        // Ignore modifier keys on their own
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LWin || e.Key == Key.RWin)
        {
            return;
        }

        if (e.Key == Key.Escape || e.Key == Key.Delete || e.Key == Key.Back)
        {
            hotkey.Key = Key.None;
            hotkey.Modifiers = ModifierKeys.None;
        }
        else
        {
            hotkey.Key = e.Key;
            hotkey.Modifiers = Keyboard.Modifiers;
        }

        textBox.Text = hotkey.ToString();
        RegisterAllHotkeys();
        SaveSettings();
    }

    private ObservableCollection<BoosterTimer> _boosters = new ObservableCollection<BoosterTimer>();
    private DispatcherTimer _boosterTickTimer;
    private DispatcherTimer _countdownTimer;
    private TimeSpan _timeRemaining;
    private TimeSpan _totalDuration;
    private DispatcherTimer _vrcCheckTimer;
    private DispatcherTimer _clipboardTimer;
    private DispatcherTimer _vendingTimer;
    private int _vendingSeconds = 900;
    private ObservableCollection<SaveItem> _saves = new ObservableCollection<SaveItem>();
    private string _lastClipboardText = "";
    private bool _isInternalCopy = false;
    private bool _isLoading = false;
    private readonly HttpClient _httpClient = new HttpClient();
    private DateTime? _nextMaintenance;
    private bool _maintenanceAlertShown = false;
    private MediaPlayer _mediaPlayer = new MediaPlayer();
    private string _settingsFilePath;
    private NotifyIcon _notifyIcon;
    private VrcLogWatcher _vrcLogWatcher;
    private OverlayWindow? _overlay;
    private double _currentResearchRate = 0;
    private double _accumulatedResearch = 0;
    private DispatcherTimer _researchResetTimer;

    public MainWindow()
    {
        InitializeComponent();
        SetupHotkeys();
        InitializeTrayIcon();

        // Define settings file in AppData
        string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrateGameTools");
        if (!Directory.Exists(appDataPath)) Directory.CreateDirectory(appDataPath);
        _settingsFilePath = System.IO.Path.Combine(appDataPath, "settings.json");

        SavesListBox.ItemsSource = _saves;
        HotkeysList.ItemsSource = _hotkeys;
        InitializeBoosters();
        BoostersList.ItemsSource = _boosters;
        SetupTimers();
        UpdateDashboard();
        LoadSettings();
        
        _vrcLogWatcher = new VrcLogWatcher();
        _vrcLogWatcher.ManualBackupDetected += (s, save) => 
        {
            Dispatcher.Invoke(() => AddSave(save));
        };
        _vrcLogWatcher.ResearchDetected += (s, amount) =>
        {
            _accumulatedResearch += amount;
        };
        _vrcLogWatcher.StatusChanged += (s, status) =>
        {
            Dispatcher.Invoke(() => LogWatcherStatusText.Text = status);
        };
        _vrcLogWatcher.Start();

        _researchResetTimer = new DispatcherTimer();
        _researchResetTimer.Interval = TimeSpan.FromSeconds(1);
        _researchResetTimer.Tick += (s, e) =>
        {
            _currentResearchRate = _accumulatedResearch;
            _accumulatedResearch = 0;
            ResearchPerSecondText.Text = _currentResearchRate.ToString("N2");
        };
        _researchResetTimer.Start();

        this.PreviewKeyDown += MainWindow_PreviewKeyDown;
        this.Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (MinimizeToTray.IsChecked == true && !_isExiting)
        {
            e.Cancel = true;
            this.Hide();
        }
        else
        {
            _notifyIcon.Dispose();
            SaveSettings();
        }
    }

    private bool _isExiting = false;

    private void InitializeTrayIcon()
    {
        _notifyIcon = new NotifyIcon();
        
        // Use the application icon from resources
        try
        {
            var iconUri = new Uri("pack://application:,,,/BoxIcon.ico");
            var streamInfo = Application.GetResourceStream(iconUri);
            if (streamInfo != null)
            {
                using (var stream = streamInfo.Stream)
                {
                    _notifyIcon.Icon = new Icon(stream);
                }
            }
            else
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
        }
        catch
        {
            _notifyIcon.Icon = SystemIcons.Application;
        }

        _notifyIcon.Visible = true;
        _notifyIcon.Text = "CrateGameTools";
        _notifyIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = WindowState.Normal; this.Activate(); };

        var contextMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open", null, (s, e) => { this.Show(); this.WindowState = WindowState.Normal; this.Activate(); });
        var toggleTimerItem = new ToolStripMenuItem("Toggle Timer", null, (s, e) => { TimerEnabled.IsChecked = !TimerEnabled.IsChecked; });
        var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => { _isExiting = true; this.Close(); });

        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(toggleTimerItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (_notifyIcon == null) return;
        
        string lastSaveStr = "Never";
        if (_saves.Any())
        {
            lastSaveStr = _saves.First().Timestamp.ToString("HH:mm:ss");
        }
        
        _notifyIcon.Text = $"CrateGameTools\nLast Save: {lastSaveStr}";
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                AddSave(text);
                e.Handled = true;
                // Switch to Saves tab if not already there? Maybe better not to force it.
            }
        }
    }

    private void AddSave(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        
        // Avoid duplicates
        if (_saves.Any(s => s.Content == content))
        {
            // If already exists, just select it
            var existing = _saves.FirstOrDefault(s => s.Content == content);
            if (existing != null) SavesListBox.SelectedItem = existing;
            return;
        }

        var newItem = new SaveItem
        {
            Content = content,
            Timestamp = DateTime.Now,
            Notes = "New Save"
        };
        _saves.Insert(0, newItem);
        SavesListBox.SelectedItem = newItem;
        UpdateTrayTooltip();

        // Reset the timer if a new save was detected and the timer is enabled
        if (TimerEnabled.IsChecked == true)
        {
            _timeRemaining = _totalDuration;
            TimerStatus.Text = $"{_timeRemaining:hh\\:mm\\:ss}";
        }

        SaveSettings();
    }

    private void InitializeBoosters()
    {
        string[] names = { "Redline", "Payday", "Lucky Break", "Hyperfocus", "More Mods" };
        int level = 1;
        if (GlobalBoosterLevel != null && int.TryParse(GlobalBoosterLevel.Text, out int l)) level = l;

        foreach (var name in names)
        {
            _boosters.Add(new BoosterTimer { Name = name, RemainingSeconds = 1500 + (level * 30) });
        }
    }

    private void SetupTimers()
    {
        // Dashboard update timer
        var dashUpdateTimer = new DispatcherTimer();
        dashUpdateTimer.Interval = TimeSpan.FromSeconds(1);
        dashUpdateTimer.Tick += (s, e) => UpdateDashboard();
        dashUpdateTimer.Start();

        // Vendingmachine timer
        _vendingTimer = new DispatcherTimer();
        _vendingTimer.Interval = TimeSpan.FromSeconds(1);
        _vendingTimer.Tick += VendingTimer_Tick;

        // Countdown timer for the manual timer alarm
        _countdownTimer = new DispatcherTimer();
        _countdownTimer.Interval = TimeSpan.FromSeconds(1);
        _countdownTimer.Tick += CountdownTimer_Tick;

        // VRChat maintenance check timer (every hour)
        _vrcCheckTimer = new DispatcherTimer();
        _vrcCheckTimer.Interval = TimeSpan.FromHours(1);
        _vrcCheckTimer.Tick += (s, e) => { _ = CheckVrcMaintenance(); };

        // Clipboard monitor timer (every 1 second)
        _clipboardTimer = new DispatcherTimer();
        _clipboardTimer.Interval = TimeSpan.FromSeconds(1);
        _clipboardTimer.Tick += ClipboardTimer_Tick;

        // Booster tick timer (every 1 second)
        _boosterTickTimer = new DispatcherTimer();
        _boosterTickTimer.Interval = TimeSpan.FromSeconds(1);
        _boosterTickTimer.Tick += BoosterTickTimer_Tick;
        _boosterTickTimer.Start();
    }

    private void BoosterTickTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var booster in _boosters)
        {
            if ((booster.IsRunning || booster.IsCooldown) && booster.RemainingSeconds > 0)
            {
                booster.RemainingSeconds--;
                if (booster.RemainingSeconds <= 0)
                {
                    if (booster.IsRunning)
                    {
                        // Active finished, start cooldown
                        booster.IsRunning = false;
                        booster.IsCooldown = true;
                        booster.RemainingSeconds = 500;
                        if (NotificationsEnabled.IsChecked == true)
                        {
                            ShowNotification("Booster Expired", $"{booster.Name} booster has finished. Cooldown started.");
                        }
                    }
                    else if (booster.IsCooldown)
                    {
                        // Cooldown finished
                        booster.IsCooldown = false;
                        booster.RemainingSeconds = 0;
                        PlayAlarmSound();
                        if (NotificationsEnabled.IsChecked == true)
                        {
                            ShowNotification("Booster Cooldown Finished", $"{booster.Name} booster is ready again.");
                        }
                    }
                }
            }
        }
    }

    private void BoosterCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is BoosterTimer booster)
        {
            RestartBooster(booster);
        }
    }

    private void BoosterLevel_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // No longer used, handled by GlobalLevel_TextChanged
    }

    private void VendingTimer_Tick(object? sender, EventArgs e)
    {
        _vendingSeconds--;
        if (_vendingSeconds <= 0)
        {
            _vendingTimer.Stop();
            _vendingSeconds = 0;
            VendingCard.Background = (System.Windows.Media.Brush)FindResource("BrightAlertBrush");
            
            if (NotificationsEnabled.IsChecked == true)
            {
                ShowNotification("Vendingmachine timer Expired", "The 900s timer has finished.");
            }
        }
        UpdateDashboard();
    }

    private void VendingCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ToggleVendingTimer();
    }

    private void TimerCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_countdownTimer.IsEnabled)
        {
            // Reset and restart
            if (TimeSpan.TryParse(TimerInput.Text, out TimeSpan time) && time.TotalSeconds > 0)
            {
                _totalDuration = time;
                _timeRemaining = time;
                TimerStatus.Text = $"{_timeRemaining:hh\\:mm\\:ss}";
                TimerCard.Background = (System.Windows.Media.Brush)FindResource("TimerBorderBrush");
                _countdownTimer.Stop();
                _countdownTimer.Start();
            }
        }
        else
        {
            // Just enable it if it was disabled
            TimerEnabled.IsChecked = true;
        }
    }

    private void ToggleVendingTimer()
    {
        if (_vendingTimer.IsEnabled || _vendingSeconds == 0)
        {
            // Reset
            _vendingTimer.Stop();
            _vendingSeconds = 900;
            VendingCard.Background = (System.Windows.Media.Brush)FindResource("CardBrush");
        }
        else
        {
            // Start
            _vendingTimer.Start();
        }
        UpdateDashboard();
    }

    private void UpdateDashboard()
    {
        // Timer Status
        if (_countdownTimer != null)
        {
            DashTimerStatus.Text = $"{_timeRemaining:hh\\:mm\\:ss}";
            DashTimerLabel.Text = _countdownTimer.IsEnabled ? "Remaining" : "Paused / Disabled";
            DashTimerStatus.Foreground = _countdownTimer.IsEnabled ? (System.Windows.Media.Brush)FindResource("PrimaryBrush") : System.Windows.Media.Brushes.Gray;
        }

        // Maintenance Status
        if (_nextMaintenance.HasValue)
        {
            DateTime localMaint = _nextMaintenance.Value.ToLocalTime();
            TimeSpan timeUntil = localMaint - DateTime.Now;
            
            if (timeUntil.TotalSeconds > 0)
            {
                if (timeUntil.TotalDays >= 1)
                {
                    DashMaintStatus.Text = $"Starts in: {timeUntil.Days}d {timeUntil.Hours}h {timeUntil.Minutes}m";
                }
                else
                {
                    DashMaintStatus.Text = $"Starts in: {timeUntil:hh\\:mm\\:ss}";
                }
                DashMaintTime.Text = localMaint.ToString("g");
                DashMaintStatus.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryBrush");
            }
            else
            {
                DashMaintStatus.Text = "Maintenance is ACTIVE";
                DashMaintTime.Text = localMaint.ToString("g");
                DashMaintStatus.Foreground = (System.Windows.Media.Brush)FindResource("BrightAlertBrush");
            }
        }
        else
        {
            DashMaintStatus.Text = "No upcoming maintenance";
            DashMaintTime.Text = "";
            DashMaintStatus.Foreground = System.Windows.Media.Brushes.Gray;
        }

        // Last Save
        if (_saves.Any())
        {
            var lastSave = _saves.First();
            TimeSpan timeSince = DateTime.Now - lastSave.Timestamp;
            
            string saveText = timeSince.TotalDays >= 1 
                ? $"{timeSince.Days}d {timeSince.Hours}h ago" 
                : $"{timeSince:hh\\:mm\\:ss} ago";

            DashLastSaveNotes.Text = saveText;
            DashLastSaveTime.Text = $"Saved at: {lastSave.Timestamp:HH:mm:ss}";

            if (_overlay != null)
            {
                _overlay.OverlayLastSave.Text = saveText;
            }
        }
        else
        {
            DashLastSaveTime.Text = "None";
            DashLastSaveNotes.Text = "No saves yet";
            if (_overlay != null) _overlay.OverlayLastSave.Text = "No saves";
        }

        // Vending Status
        VendingStatus.Text = $"{_vendingSeconds}s";

        // Update Overlay
        if (_overlay != null)
        {
            _overlay.OverlayMainTimer.Text = DashTimerStatus.Text;
            _overlay.OverlayVendingTimer.Text = VendingStatus.Text;
            // The Boosters are bound via ItemsSource, so they update automatically if the collection is correct
        }
    }

    private void ClipboardTimer_Tick(object? sender, EventArgs e)
    {
        if (SaveDetectionEnabled.IsChecked != true) return;

        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (text != _lastClipboardText)
                {
                    _lastClipboardText = text;
                    
                    // If it starts with ENC1: and it's not from our own copy action
                    if (text.StartsWith("ENC1:"))
                    {
                        // Check if it already exists in the list to avoid redetection
                        if (!_saves.Any(s => s.Content == text))
                        {
                            AddSave(text);
                        }
                    }
                }
            }
        }
        catch { } // Clipboard might be busy
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _timeRemaining = _timeRemaining.Subtract(TimeSpan.FromSeconds(1));
        if (_timeRemaining <= TimeSpan.Zero)
        {
            _timeRemaining = TimeSpan.Zero;
            _countdownTimer.Stop();
            TimerCard.Background = (System.Windows.Media.Brush)FindResource("BrightAlertBrush");
            
            PlayAlarmSound();
            if (NotificationsEnabled.IsChecked == true)
            {
                ShowNotification("Timer Expired", "The countdown timer has finished.");
            }
        }
        
        TimerStatus.Text = $"{_timeRemaining:hh\\:mm\\:ss}";
    }

    private void TimerEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (TimerEnabled.IsChecked == true)
        {
            if (TimeSpan.TryParse(TimerInput.Text, out TimeSpan time) && time.TotalSeconds > 0)
            {
                _totalDuration = time;
                _timeRemaining = time;
                TimerStatus.Text = $"{_timeRemaining:hh\\:mm\\:ss}";
                TimerInput.IsEnabled = false;
                _countdownTimer.Start();
            }
            else
            {
                TimerEnabled.IsChecked = false;
                MessageBox.Show("Invalid time format. Use hh:mm:ss", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            _countdownTimer.Stop();
            TimerInput.IsEnabled = true;
            if (TimeSpan.TryParse(TimerInput.Text, out TimeSpan time))
            {
                TimerStatus.Text = $"{time:hh\\:mm\\:ss}";
            }
        }
        SaveSettings();
    }

    private void TimerInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveSettings();
    }

    private void PlayAlarmSound()
    {
        string soundPath = AlarmSoundPath.Text;
        if (!string.IsNullOrEmpty(soundPath) && File.Exists(soundPath))
        {
            _mediaPlayer.Open(new Uri(soundPath));
            _mediaPlayer.Play();
        }
        else
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
    }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog openFileDialog = new OpenFileDialog();
        openFileDialog.Filter = "Audio files (*.mp3;*.wav;*.wma)|*.mp3;*.wav;*.wma|All files (*.*)|*.*";
        if (openFileDialog.ShowDialog() == true)
        {
            AlarmSoundPath.Text = openFileDialog.FileName;
            SaveSettings();
        }
    }

    private void ClearSound_Click(object sender, RoutedEventArgs e)
    {
        AlarmSoundPath.Text = "";
        _mediaPlayer.Stop();
        SaveSettings();
    }

    private void PlayTestSound_Click(object sender, RoutedEventArgs e)
    {
        PlayAlarmSound();
    }

    private void SaveDetectionEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateClipboardButton();
        if (SaveDetectionEnabled.IsChecked == true)
        {
            _clipboardTimer.Start();
        }
        else
        {
            _clipboardTimer.Stop();
        }
        SaveSettings();
    }

    private void NotificationsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void OverlayEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (OverlayEnabled.IsChecked == true)
        {
            if (_overlay == null)
            {
                _overlay = new OverlayWindow();
                _overlay.OverlayBoostersList.ItemsSource = _boosters;
                _overlay.Show();
                UpdateDashboard();
            }
        }
        else
        {
            if (_overlay != null)
            {
                _overlay.Close();
                _overlay = null;
            }
        }
        SaveSettings();
    }

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        OverlayEnabled.IsChecked = OverlayEnabled.IsChecked != true;
    }

    private void ShowNotification(string title, string message)
    {
        // Ensure this runs on the UI thread
        Dispatcher.Invoke((Action)(() =>
        {
            PlayAlarmSound();
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, System.Windows.MessageBoxOptions.ServiceNotification);
        }));
    }

    private void WatchClipboard_Click(object sender, RoutedEventArgs e)
    {
        SaveDetectionEnabled.IsChecked = SaveDetectionEnabled.IsChecked != true;
    }

    private void UpdateClipboardButton()
    {
        if (WatchClipboardBtn != null)
        {
            WatchClipboardBtn.Content = SaveDetectionEnabled.IsChecked == true ? "Watch Clipboard: ON" : "Watch Clipboard: OFF";
            WatchClipboardBtn.Background = SaveDetectionEnabled.IsChecked == true ? (SolidColorBrush)Application.Current.Resources["PrimaryBrush"] : new SolidColorBrush(Colors.Gray);
        }
    }

    private void SavesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavesListBox.SelectedItem is SaveItem item)
        {
            SaveDetailsPanel.Visibility = Visibility.Visible;
            NoSavesText.Visibility = Visibility.Collapsed;
            DetailTimestamp.Text = item.Timestamp.ToString("g");
            DetailNotes.Text = item.Notes;
            DetailContent.Text = item.Content;
        }
        else
        {
            SaveDetailsPanel.Visibility = Visibility.Collapsed;
            NoSavesText.Visibility = Visibility.Visible;
        }
    }

    private void DetailNotes_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SavesListBox.SelectedItem is SaveItem item)
        {
            if (item.Notes != DetailNotes.Text)
            {
                item.Notes = DetailNotes.Text;
                SaveSettings();
            }
        }
    }

    private void CopySave_Click(object sender, RoutedEventArgs e)
    {
        if (SavesListBox.SelectedItem is SaveItem item)
        {
            _lastClipboardText = item.Content; // Prevent immediate redetection
            Clipboard.SetText(item.Content);
        }
    }

    private void DeleteSave_Click(object sender, RoutedEventArgs e)
    {
        if (SavesListBox.SelectedItem is SaveItem item)
        {
            _saves.Remove(item);
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        if (_isLoading) return;
        try
        {
            var settings = new
            {
                AlarmSoundPath = AlarmSoundPath.Text,
                TimerEnabled = TimerEnabled.IsChecked,
                TimerDuration = TimerInput.Text,
                BoosterLevel = int.TryParse(GlobalBoosterLevel.Text, out int level) ? level : 1,
                VrcAlarmEnabled = VrcAlarmEnabled.IsChecked,
                SaveDetectionEnabled = SaveDetectionEnabled.IsChecked,
                NotificationsEnabled = NotificationsEnabled.IsChecked,
                MinimizeToTray = MinimizeToTray.IsChecked,
                OverlayEnabled = OverlayEnabled.IsChecked,
                Hotkeys = _hotkeys.ToList(),
                Saves = _saves.ToList()
            };
            string json = System.Text.Json.JsonSerializer.Serialize(settings);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch { }
    }

    private void LoadSettings()
    {
        _isLoading = true;
        try
        {
            // First check the old location to migrate settings if they exist
            string oldPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            if (File.Exists(oldPath) && !File.Exists(_settingsFilePath))
            {
                File.Move(oldPath, _settingsFilePath);
            }

            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
                
                if (settings.TryGetProperty("AlarmSoundPath", out var pathProperty))
                {
                    AlarmSoundPath.Text = pathProperty.GetString() ?? "";
                }

                if (settings.TryGetProperty("TimerDuration", out var durationProperty))
                {
                    TimerInput.Text = durationProperty.GetString() ?? "00:10:00";
                }

                // Load Saves BEFORE event-triggering checkboxes
                if (settings.TryGetProperty("Saves", out var savesProperty))
                {
                    var savedSaves = System.Text.Json.JsonSerializer.Deserialize<List<SaveItem>>(savesProperty.GetRawText());
                    if (savedSaves != null)
                    {
                        _saves.Clear();
                        foreach (var s in savedSaves) _saves.Add(s);
                        UpdateTrayTooltip();
                    }
                }

                if (settings.TryGetProperty("VrcAlarmEnabled", out var vrcEnabledProperty))
                {
                    VrcAlarmEnabled.IsChecked = vrcEnabledProperty.GetBoolean();
                }

                if (settings.TryGetProperty("SaveDetectionEnabled", out var saveDetProperty))
                {
                    SaveDetectionEnabled.IsChecked = saveDetProperty.GetBoolean();
                    UpdateClipboardButton();
                }

                if (settings.TryGetProperty("NotificationsEnabled", out var notifyProperty))
                {
                    NotificationsEnabled.IsChecked = notifyProperty.GetBoolean();
                }

                if (settings.TryGetProperty("BoosterLevel", out var boosterLevelProperty))
                {
                    GlobalBoosterLevel.Text = boosterLevelProperty.GetInt32().ToString();
                }

                if (settings.TryGetProperty("MinimizeToTray", out var trayProperty))
                {
                    MinimizeToTray.IsChecked = trayProperty.GetBoolean();
                }

                if (settings.TryGetProperty("OverlayEnabled", out var overlayProperty))
                {
                    OverlayEnabled.IsChecked = overlayProperty.GetBoolean();
                }

                if (settings.TryGetProperty("Hotkeys", out var hotkeysProperty))
                {
                    var savedHotkeys = System.Text.Json.JsonSerializer.Deserialize<List<HotkeyInfo>>(hotkeysProperty.GetRawText());
                    if (savedHotkeys != null)
                    {
                        foreach (var saved in savedHotkeys)
                        {
                            var existing = _hotkeys.FirstOrDefault(h => h.ActionName == saved.ActionName);
                            if (existing != null)
                            {
                                existing.Key = saved.Key;
                                existing.Modifiers = saved.Modifiers;
                            }
                        }
                    }
                }

                if (settings.TryGetProperty("TimerEnabled", out var timerEnabledProperty))
                {
                    TimerEnabled.IsChecked = timerEnabledProperty.GetBoolean();
                }
            }
        }
        catch { }
        finally
        {
            _isLoading = false;
            SaveSettings(); // Final save to ensure consistency
        }
    }

    private async void VrcAlarmEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (VrcAlarmEnabled.IsChecked == true)
        {
            _vrcCheckTimer.Start();
            await CheckVrcMaintenance();
        }
        else
        {
            _vrcCheckTimer.Stop();
        }
        SaveSettings();
    }

    private async void CheckVrcStatus_Click(object sender, RoutedEventArgs e)
    {
        await CheckVrcMaintenance();
    }

    private async Task CheckVrcMaintenance()
    {
        try
        {
            VrcStatusText.Text = "Status: Checking...";
            await CheckVrcMaintenanceJson();
        }
        catch (Exception ex)
        {
            VrcStatusText.Text = $"Status: Error - {ex.Message}";
        }
    }

    private async Task CheckVrcMaintenanceJson()
    {
        try
        {
            string json = await _httpClient.GetStringAsync("https://status.vrchat.com/api/v2/scheduled-maintenances.json");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var maintenances = doc.RootElement.GetProperty("scheduled_maintenances");
            
            var maintList = new List<MaintenanceItem>();
            _nextMaintenance = null;
            DateTime now = DateTime.UtcNow;

            foreach (var maint in maintenances.EnumerateArray())
            {
                string name = maint.GetProperty("name").GetString() ?? "Unknown";
                string status = maint.GetProperty("status").GetString() ?? "";
                string? scheduledFor = maint.GetProperty("scheduled_for").GetString();

                // Hide completed/resolved maintenances
                if (status == "completed" || status == "resolved")
                    continue;
                
                if (DateTime.TryParse(scheduledFor, out DateTime scheduledDate))
                {
                    maintList.Add(new MaintenanceItem 
                    { 
                        Name = name, 
                        StartTime = scheduledDate, 
                        Status = status 
                    });

                    if (status == "scheduled" || status == "in_progress")
                    {
                        if (scheduledDate > now)
                        {
                            if (_nextMaintenance == null || scheduledDate < _nextMaintenance)
                            {
                                _nextMaintenance = scheduledDate;
                            }
                        }
                    }
                }
            }

            MaintListBox.ItemsSource = maintList.OrderBy(m => m.StartTime).ToList();

            if (_nextMaintenance.HasValue)
            {
                DateTime localMaint = _nextMaintenance.Value.ToLocalTime();
                VrcStatusText.Text = $"Status: Next maintenance at {localMaint:g}";
                
                TimeSpan timeToMaint = _nextMaintenance.Value - DateTime.UtcNow;
                if (timeToMaint.TotalMinutes <= 5 && timeToMaint.TotalMinutes > 0)
                {
                    if (!_maintenanceAlertShown && VrcAlarmEnabled.IsChecked == true)
                    {
                        _maintenanceAlertShown = true;
                        if (NotificationsEnabled.IsChecked == true)
                        {
                            ShowNotification("Maintenance Alarm", $"VRChat Maintenance starting soon!\nScheduled for: {localMaint:g}");
                        }
                        else
                        {
                            MessageBox.Show($"VRChat Maintenance starting soon!\nScheduled for: {localMaint:g}", "Maintenance Alarm", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
                else
                {
                    _maintenanceAlertShown = false;
                }
            }
            else
            {
                VrcStatusText.Text = "Status: All systems operational.";
            }
        }
        catch (Exception ex)
        {
            VrcStatusText.Text = $"Status: Error checking JSON - {ex.Message}";
        }
    }
}