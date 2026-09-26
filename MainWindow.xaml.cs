using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace TronAutoSweeper;

public partial class MainWindow : Window
{
    private readonly Sweeper _sweeper = new();
    private readonly string _appDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TRON-Auto-Sweeper");
    private string KeyPath => Path.Combine(_appDir, "privatekey.dat");
    private string ConfigPath => Path.Combine(_appDir, "config.json");

    public MainWindow()
    {
        InitializeComponent();
        _sweeper.Log += message => Dispatcher.Invoke(() =>
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n"));
        _sweeper.StateChanged += state => Dispatcher.Invoke(() => StatusText.Text = state);
        LoadConfig();
    }

    private sealed record Config(string Receiver, decimal MinDeposit, decimal Reserve, string ApiKey);

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath));
            if (cfg is null) return;

            ReceiverBox.Text = cfg.Receiver;
            MinDepositBox.Text = cfg.MinDeposit.ToString("0.######", CultureInfo.InvariantCulture);
            ReserveBox.Text = cfg.Reserve.ToString("0.######", CultureInfo.InvariantCulture);
            ApiKeyBox.Password = cfg.ApiKey;
        }
        catch (Exception ex)
        {
            LogBox.AppendText($"Ошибка загрузки настроек: {ex.Message}\n");
        }
    }

    private bool ReadInputs(
        out string privateKey,
        out string receiver,
        out decimal minDeposit,
        out decimal reserve,
        out string apiKey)
    {
        privateKey = PrivateKeyBox.Password.Trim();
        receiver = ReceiverBox.Text.Trim();
        apiKey = ApiKeyBox.Password.Trim();
        minDeposit = 0;
        reserve = 0;

        if (privateKey.Length != 64 || !privateKey.All(Uri.IsHexDigit))
        {
            MessageBox.Show("Приватный ключ должен содержать 64 hex-символа.");
            return false;
        }

        if (!TronAddress.IsValidBase58(receiver))
        {
            MessageBox.Show("Адрес получателя TRON указан неверно.");
            return false;
        }

        if (!TryParseTrx(MinDepositBox.Text, out minDeposit) || minDeposit < 0)
        {
            MessageBox.Show("Неверный минимальный депозит.");
            return false;
        }

        if (!TryParseTrx(ReserveBox.Text, out reserve) || reserve < 0)
        {
            MessageBox.Show("Неверный резерв TRX.");
            return false;
        }

        return true;
    }

    private static bool TryParseTrx(string text, out decimal value)
    {
        return decimal.TryParse(
            text.Replace(',', '.'),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadInputs(out var privateKey, out var receiver, out var minDeposit, out var reserve, out var apiKey))
            return;

        try
        {
            Directory.CreateDirectory(_appDir);

            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(privateKey),
                null,
                DataProtectionScope.CurrentUser);

            File.WriteAllBytes(KeyPath, protectedBytes);

            var config = new Config(receiver, minDeposit, reserve, apiKey);
            File.WriteAllText(
                ConfigPath,
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            PrivateKeyBox.Clear();
            MessageBox.Show("Настройки сохранены. Приватный ключ зашифрован средствами Windows.");
        }
        catch (Exception ex)
        {
            LogBox.AppendText($"Ошибка сохранения: {ex.Message}\n");
            MessageBox.Show(ex.Message);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string privateKey;

            if (!string.IsNullOrWhiteSpace(PrivateKeyBox.Password))
            {
                privateKey = PrivateKeyBox.Password.Trim();
            }
            else if (File.Exists(KeyPath))
            {
                privateKey = Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(
                        File.ReadAllBytes(KeyPath),
                        null,
                        DataProtectionScope.CurrentUser));
            }
            else
            {
                MessageBox.Show("Сначала введите приватный ключ и нажмите Сохранить.");
                return;
            }

            if (!ReadInputsFromStored(out var receiver, out var minDeposit, out var reserve, out var apiKey))
                return;

            await _sweeper.ConfigureAsync(
                privateKey,
                receiver,
                minDeposit,
                reserve,
                apiKey,
                _appDir);

            await _sweeper.StartAsync();
        }
        catch (Exception ex)
        {
            LogBox.AppendText($"Ошибка запуска: {ex.Message}\n");
            MessageBox.Show(ex.Message);
        }
    }

    private bool ReadInputsFromStored(
        out string receiver,
        out decimal minDeposit,
        out decimal reserve,
        out string apiKey)
    {
        receiver = ReceiverBox.Text.Trim();
        apiKey = ApiKeyBox.Password.Trim();
        minDeposit = 0;
        reserve = 0;

        if (!TronAddress.IsValidBase58(receiver))
        {
            MessageBox.Show("Адрес получателя TRON указан неверно.");
            return false;
        }

        if (!TryParseTrx(MinDepositBox.Text, out minDeposit) || minDeposit < 0)
        {
            MessageBox.Show("Неверный минимальный депозит.");
            return false;
        }

        if (!TryParseTrx(ReserveBox.Text, out reserve) || reserve < 0)
        {
            MessageBox.Show("Неверный резерв TRX.");
            return false;
        }

        return true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _sweeper.Stop();

    protected override void OnClosed(EventArgs e)
    {
        _sweeper.Stop();
        base.OnClosed(e);
    }
}
