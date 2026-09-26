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
    private string SeedPath => Path.Combine(_appDir, "seedphrase.dat");
    private string ConfigPath => Path.Combine(_appDir, "config.json");

    public MainWindow()
    {
        InitializeComponent();
        _sweeper.Log += message => Dispatcher.Invoke(() =>
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n"));
        _sweeper.StateChanged += state => Dispatcher.Invoke(() => StatusText.Text = state);
        LoadConfig();
    }

    private sealed record Config(string Receiver, decimal MinDeposit, decimal Reserve, string ApiKey, int DerivationIndex);

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
            DerivationIndexBox.Text = cfg.DerivationIndex.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) { LogBox.AppendText($"Ошибка загрузки настроек: {ex.Message}\n"); }
    }

    private bool ReadInputs(out string seed, out string receiver, out decimal minDeposit, out decimal reserve, out string apiKey, out int derivationIndex)
    {
        seed = SeedPhraseBox.Password.Trim();
        receiver = ReceiverBox.Text.Trim();
        apiKey = ApiKeyBox.Password.Trim();
        minDeposit = reserve = 0;
        derivationIndex = 0;

        var words = seed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (12 or 15 or 18 or 21 or 24))
        { MessageBox.Show("Seed-фраза должна содержать 12, 15, 18, 21 или 24 слова."); return false; }
        if (!TronAddress.IsValidBase58(receiver))
        { MessageBox.Show("Адрес получателя TRON указан неверно."); return false; }
        if (!int.TryParse(DerivationIndexBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out derivationIndex) || derivationIndex < 0)
        { MessageBox.Show("Derivation index должен быть целым числом 0 или больше."); return false; }
        if (!TryParseTrx(MinDepositBox.Text, out minDeposit) || minDeposit < 0)
        { MessageBox.Show("Неверный минимальный депозит."); return false; }
        if (!TryParseTrx(ReserveBox.Text, out reserve) || reserve < 0)
        { MessageBox.Show("Неверный резерв TRX."); return false; }
        return true;
    }

    private static bool TryParseTrx(string text, out decimal value) => decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadInputs(out var seed, out var receiver, out var minDeposit, out var reserve, out var apiKey, out var derivationIndex)) return;
        try
        {
            Directory.CreateDirectory(_appDir);
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(seed), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(SeedPath, protectedBytes);
            var config = new Config(receiver, minDeposit, reserve, apiKey, derivationIndex);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            SeedPhraseBox.Clear();
            MessageBox.Show("Настройки сохранены. Seed-фраза зашифрована средствами Windows.");
        }
        catch (Exception ex) { LogBox.AppendText($"Ошибка сохранения: {ex.Message}\n"); MessageBox.Show(ex.Message); }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string seed;
            if (!string.IsNullOrWhiteSpace(SeedPhraseBox.Password)) seed = SeedPhraseBox.Password.Trim();
            else if (File.Exists(SeedPath)) seed = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(SeedPath), null, DataProtectionScope.CurrentUser));
            else { MessageBox.Show("Сначала введите seed-фразу и нажмите Сохранить."); return; }

            if (!ReadStoredInputs(out var receiver, out var minDeposit, out var reserve, out var apiKey, out var derivationIndex)) return;
            await _sweeper.ConfigureAsync(seed, receiver, minDeposit, reserve, apiKey, _appDir, derivationIndex);
            AddressText.Text = _sweeper.SourceAddress;
            await _sweeper.StartAsync();
        }
        catch (Exception ex) { LogBox.AppendText($"Ошибка запуска: {ex.Message}\n"); MessageBox.Show(ex.Message); }
    }

    private bool ReadStoredInputs(out string receiver, out decimal minDeposit, out decimal reserve, out string apiKey, out int derivationIndex)
    {
        receiver = ReceiverBox.Text.Trim(); apiKey = ApiKeyBox.Password.Trim(); minDeposit = reserve = 0; derivationIndex = 0;
        if (!TronAddress.IsValidBase58(receiver)) { MessageBox.Show("Адрес получателя TRON указан неверно."); return false; }
        if (!int.TryParse(DerivationIndexBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out derivationIndex) || derivationIndex < 0) { MessageBox.Show("Неверный derivation index."); return false; }
        if (!TryParseTrx(MinDepositBox.Text, out minDeposit) || minDeposit < 0) { MessageBox.Show("Неверный минимальный депозит."); return false; }
        if (!TryParseTrx(ReserveBox.Text, out reserve) || reserve < 0) { MessageBox.Show("Неверный резерв TRX."); return false; }
        return true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _sweeper.Stop();
    protected override void OnClosed(EventArgs e) { _sweeper.Stop(); base.OnClosed(e); }
}
