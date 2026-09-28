using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tron;
using Tron.Contracts;

namespace TronAutoSweeper;

public sealed class Sweeper
{
    private const string UsdtContract = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    private const decimal UsdtDecimals = 1_000_000m;

    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.trongrid.io") };
    private readonly SemaphoreSlim _sweepLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private IContractClientFactory? _contractClientFactory;
    private IWalletClient? _walletClient;
    private string _privateKey = "";
    private string _sourceAddress = "";
    private string _receiver = "";
    private decimal _triggerTrx;
    private long _usdtFeeLimitSun;
    private string _apiKey = "";

    public event Action<string>? Log;
    public event Action<string>? StateChanged;
    public event Action<decimal, decimal>? BalancesChanged;
    public event Action<string>? TxChanged;

    public Task ConfigureAsync(string privateKey, string receiver, decimal triggerTrx, decimal usdtFeeLimitTrx, string apiKey)
    {
        _privateKey = privateKey;
        _receiver = receiver;
        _triggerTrx = triggerTrx;
        _usdtFeeLimitSun = checked((long)Math.Round(usdtFeeLimitTrx * 1_000_000m, MidpointRounding.ToEven));
        _apiKey = apiKey;

        _http.DefaultRequestHeaders.Remove("TRON-PRO-API-KEY");
        if (!string.IsNullOrWhiteSpace(_apiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("TRON-PRO-API-KEY", _apiKey);

        var services = new ServiceCollection();
        services.AddTronNet(x =>
        {
            x.Network = TronNetwork.MainNet;
            x.Channel = new GrpcChannelOption { Host = "grpc.trongrid.io", Port = 50051 };
            x.SolidityChannel = new GrpcChannelOption { Host = "grpc.trongrid.io", Port = 50052 };
            x.ApiKey = _apiKey;
        });
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        _contractClientFactory = provider.GetRequiredService<IContractClientFactory>();
        _walletClient = provider.GetRequiredService<IWalletClient>();
        var options = provider.GetRequiredService<IOptions<TronNetOptions>>().Value;

        var key = new TronECKey(_privateKey, options.Network);
        _sourceAddress = key.GetPublicAddress();
        if (!TronAddress.IsValidBase58(_sourceAddress))
            throw new InvalidOperationException("Не удалось получить корректный TRON-адрес из приватного ключа.");

        if (!TronAddress.IsValidBase58(_receiver))
            throw new InvalidOperationException("Адрес получателя неверен.");
        if (string.Equals(_sourceAddress, _receiver, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Адрес получателя не должен совпадать с исходным.");
        if (_triggerTrx < 0)
            throw new InvalidOperationException("Порог TRX не может быть отрицательным.");
        if (_usdtFeeLimitSun <= 0)
            throw new InvalidOperationException("Лимит комиссии USDT должен быть больше нуля.");

        Log?.Invoke($"Адрес источника: {_sourceAddress}");
        Log?.Invoke("Режим: при TRX выше порога отправляется весь доступный USDT TRC-20.");
        Log?.Invoke($"Порог запуска: {_triggerTrx:0.######} TRX; лимит комиссии USDT: {usdtFeeLimitTrx:0.######} TRX.");
        return Task.CompletedTask;
    }

    public async Task StartAsync()
    {
        if (_contractClientFactory is null) throw new InvalidOperationException("Сначала настройте программу.");
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        StateChanged?.Invoke("Запущено");
        Log?.Invoke("Мониторинг TRX и USDT запущен.");
        _ = Task.Run(() => LoopAsync(_cts.Token));
        await Task.CompletedTask;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        StateChanged?.Invoke("Остановлено");
        Log?.Invoke("Мониторинг остановлен.");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var trx = await GetTrxBalanceAsync(ct);
                var usdt = await GetUsdtBalanceAsync(ct);
                BalancesChanged?.Invoke(trx, usdt);

                if (trx > _triggerTrx)
                {
                    if (usdt > 0)
                    {
                        await _sweepLock.WaitAsync(ct);
                        try
                        {
                            // Повторно читаем баланс непосредственно перед отправкой.
                            var currentTrx = await GetTrxBalanceAsync(ct);
                            var currentUsdt = await GetUsdtBalanceAsync(ct);
                            BalancesChanged?.Invoke(currentTrx, currentUsdt);

                            if (currentTrx > _triggerTrx && currentUsdt > 0)
                            {
                                var txid = await SweepUsdtAsync(currentUsdt, ct);
                                if (!string.IsNullOrWhiteSpace(txid))
                                    TxChanged?.Invoke(txid);
                            }
                        }
                        finally
                        {
                            _sweepLock.Release();
                        }
                    }
                    else
                    {
                        Log?.Invoke($"TRX выше порога ({trx:0.######} > {_triggerTrx:0.######}), но USDT нет.");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Ошибка мониторинга: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<decimal> GetTrxBalanceAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"/v1/accounts/{Uri.EscapeDataString(_sourceAddress)}", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return 0;

        var item = data[0];
        var sun = item.TryGetProperty("balance", out var bal) && bal.TryGetInt64(out var value) ? value : 0;
        return sun / 1_000_000m;
    }

    private async Task<decimal> GetUsdtBalanceAsync(CancellationToken ct)
    {
        var url = $"/v1/accounts/{Uri.EscapeDataString(_sourceAddress)}/trc20/balance?contract_address={Uri.EscapeDataString(UsdtContract)}&limit=200";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return 0;

        decimal totalRaw = 0;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in item.EnumerateObject())
            {
                if (!string.Equals(prop.Name, UsdtContract, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.String && decimal.TryParse(prop.Value.GetString(), out var raw))
                    totalRaw += raw;
                else if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDecimal(out var rawNumber))
                    totalRaw += rawNumber;
            }
        }

        return totalRaw / UsdtDecimals;
    }

    private async Task<string?> SweepUsdtAsync(decimal usdtAmount, CancellationToken ct)
    {
        if (_contractClientFactory is null)
            throw new InvalidOperationException("TRC-20 client не инициализирован.");

        // TronNet.Wallet 1.0.1 TRC20 client accepts the human-readable token amount (decimal).
        Log?.Invoke($"USDT найдено: {usdtAmount:0.######}. Отправляю весь баланс → {_receiver}");
        var contractClient = _contractClientFactory.CreateClient(ContractProtocol.TRC20);
        var account = _walletClient!.GetAccount(_privateKey);
        var txid = await contractClient.TransferAsync(
            UsdtContract,
            account,
            _receiver,
            usdtAmount,
            string.Empty,
            _usdtFeeLimitSun);

        if (string.IsNullOrWhiteSpace(txid))
            throw new InvalidOperationException("TRON не вернул TXID для USDT-перевода.");

        Log?.Invoke($"USDT-транзакция отправлена. TXID: {txid}");
        Log?.Invoke("Broadcast принят; окончательный успех USDT-перевода нужно проверять по receipt/transaction info.");
        return txid;
    }
}
