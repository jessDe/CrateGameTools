using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using NAudio.Wave;
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
public partial class MainWindow : Window
{
    private DispatcherTimer _countdownTimer;
    private TimeSpan _timeRemaining;
    private TimeSpan _totalDuration;
    private DispatcherTimer _vrcCheckTimer;
    private DispatcherTimer _clipboardTimer;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private string? _tempRecordingPath;
    private bool _isRecording = false;
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

    public MainWindow()
    {
        InitializeComponent();
        
        InitializeTrayIcon();

        // Define settings file in AppData
        string appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrateGameTools");
        if (!Directory.Exists(appDataPath)) Directory.CreateDirectory(appDataPath);
        _settingsFilePath = System.IO.Path.Combine(appDataPath, "settings.json");

        SavesListBox.ItemsSource = _saves;
        SetupTimers();
        LoadSettings();
        RefreshMics();
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

    private void SetupTimers()
    {
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
            _timeRemaining = _totalDuration; // Repeating
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

    private void RefreshMics_Click(object sender, RoutedEventArgs e)
    {
        RefreshMics();
    }

    private void RefreshMics()
    {
        MicComboBox.Items.Clear();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var capabilities = WaveIn.GetCapabilities(i);
            MicComboBox.Items.Add(new { DeviceNumber = i, ProductName = capabilities.ProductName });
        }
        if (MicComboBox.Items.Count > 0) MicComboBox.SelectedIndex = 0;
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRecording)
        {
            StartRecording();
        }
        else
        {
            StopRecording();
        }
    }

    private void StartRecording()
    {
        if (MicComboBox.SelectedItem == null)
        {
            MessageBox.Show("Please select a microphone.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        dynamic selectedMic = MicComboBox.SelectedItem;
        int deviceNumber = selectedMic.DeviceNumber;

        try
        {
            _waveIn = new WaveInEvent();
            _waveIn.DeviceNumber = deviceNumber;
            _waveIn.WaveFormat = new WaveFormat(44100, 1);
            _waveIn.DataAvailable += (s, e) => _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            _waveIn.RecordingStopped += OnRecordingStopped;

            _tempRecordingPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"alarm_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            _waveWriter = new WaveFileWriter(_tempRecordingPath, _waveIn.WaveFormat);

            _waveIn.StartRecording();
            _isRecording = true;
            RecordBtn.Content = "Stop Recording";
            RecordStatus.Text = "Recording...";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start recording: {ex.Message}");
        }
    }

    private void StopRecording()
    {
        if (_waveIn != null)
        {
            _waveIn.RecordingStopped -= OnRecordingStopped; // Remove event handler to avoid re-entry if needed, though we'll handle it inside
            _waveIn.StopRecording();
        }
        _isRecording = false;
        RecordBtn.Content = "Start Recording";
        RecordStatus.Text = "Saving...";
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _waveWriter?.Dispose();
        _waveWriter = null;
        _waveIn?.Dispose();
        _waveIn = null;

        if (e.Exception != null)
        {
            Dispatcher.Invoke(() => MessageBox.Show($"Recording Error: {e.Exception.Message}"));
            return;
        }

        if (!string.IsNullOrEmpty(_tempRecordingPath) && System.IO.File.Exists(_tempRecordingPath))
        {
            string finalPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom_alarm.wav");
            try
            {
                if (System.IO.File.Exists(finalPath)) System.IO.File.Delete(finalPath);
                System.IO.File.Move(_tempRecordingPath, finalPath);
                Dispatcher.Invoke(() =>
                {
                    AlarmSoundPath.Text = finalPath;
                    SaveSettings();
                    RecordStatus.Text = "Recording saved!";
                    MessageBox.Show("Custom alarm recorded and set!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => MessageBox.Show($"Error saving recording: {ex.Message}"));
            }
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
                    VrcAlarmEnabled = VrcAlarmEnabled.IsChecked,
                    SaveDetectionEnabled = SaveDetectionEnabled.IsChecked,
                    NotificationsEnabled = NotificationsEnabled.IsChecked,
                    MinimizeToTray = MinimizeToTray.IsChecked,
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

                if (settings.TryGetProperty("MinimizeToTray", out var trayProperty))
                {
                    MinimizeToTray.IsChecked = trayProperty.GetBoolean();
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