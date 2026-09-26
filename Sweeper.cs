using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TronNet;

namespace TronAutoSweeper;

public sealed class Sweeper
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.trongrid.io") };
    private CancellationTokenSource? _cts;
    private ITransactionClient? _txClient;
    private TronNetOptions? _options;
    private string _privateKey = "";
    private string _sourceAddress = "";
    private string _receiver = "";
    private decimal _minDeposit;
    private decimal _reserve;
    private string _apiKey = "";
    private readonly HashSet<string> _seen = new();
    private bool _baselineReady;

    public event Action<string>? Log;
    public event Action<string>? StateChanged;

    public async Task ConfigureAsync(string privateKey, string receiver, decimal minDeposit, decimal reserve, string apiKey)
    {
        _privateKey = privateKey;
        _receiver = receiver;
        _minDeposit = minDeposit;
        _reserve = reserve;
        _apiKey = apiKey;
        _http.DefaultRequestHeaders.Remove("TRON-PRO-API-KEY");
        if (!string.IsNullOrWhiteSpace(_apiKey)) _http.DefaultRequestHeaders.Add("TRON-PRO-API-KEY", _apiKey);

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
        _txClient = provider.GetRequiredService<ITransactionClient>();
        _options = provider.GetRequiredService<IOptions<TronNetOptions>>().Value;

        var key = new TronECKey(_privateKey, _options.Network);
        _sourceAddress = key.GetPublicAddress();
        if (!TronAddress.IsValidBase58(_sourceAddress)) throw new InvalidOperationException("Не удалось получить корректный TRON-адрес из приватного ключа.");
        Log?.Invoke($"Адрес из ключа: {_sourceAddress}");
        if (!TronAddress.IsValidBase58(_receiver)) throw new InvalidOperationException("Адрес получателя неверен.");
        if (_sourceAddress == _receiver) throw new InvalidOperationException("Адрес получателя не должен совпадать с исходным.");
        Log?.Invoke("Приватный ключ локально проверен; ключ не отправляется в TronGrid.");
    }

    public async Task StartAsync()
    {
        if (_txClient is null) throw new InvalidOperationException("Сначала настройте программу.");
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        StateChanged?.Invoke("Запущено");
        Log?.Invoke("Мониторинг входящих TRX запущен.");

        await EstablishBaselineAsync(_cts.Token);
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        StateChanged?.Invoke("Остановлено");
        Log?.Invoke("Мониторинг остановлен.");
    }

    private async Task EstablishBaselineAsync(CancellationToken ct)
    {
        foreach (var tx in await GetTransactionsAsync(ct)) _seen.Add(tx.Id);
        _baselineReady = true;
        Log?.Invoke("Базовая история зафиксирована; старые депозиты повторно отправляться не будут.");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var txs = await GetTransactionsAsync(ct);
                foreach (var tx in txs.OrderBy(x => x.Timestamp))
                {
                    if (_seen.Contains(tx.Id)) continue;
                    _seen.Add(tx.Id);
                    if (!tx.Incoming || tx.AmountSun <= 0) continue;
                    Log?.Invoke($"Обнаружен входящий TRX: {tx.AmountSun / 1_000_000m:0.######} TRX, TX {tx.Id}");
                    if (tx.AmountSun / 1_000_000m < _minDeposit) { Log?.Invoke("Сумма ниже минимального порога."); continue; }
                    await SweepAsync(ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log?.Invoke($"Ошибка мониторинга: {ex.Message}"); await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        }
    }

    private sealed record Tx(string Id, long Timestamp, bool Incoming, long AmountSun);

    private async Task<List<Tx>> GetTransactionsAsync(CancellationToken ct)
    {
        var url = $"/v1/accounts/{Uri.EscapeDataString(_sourceAddress)}/transactions?only_confirmed=true&limit=50&order_by=block_timestamp,desc";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var list = new List<Tx>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("txID", out var idEl)) continue;
            var id = idEl.GetString() ?? "";
            var ts = item.TryGetProperty("block_timestamp", out var tsEl) ? tsEl.GetInt64() : 0;
            var contracts = item.TryGetProperty("raw_data", out var raw) && raw.TryGetProperty("contract", out var c) ? c : default;
            if (contracts.ValueKind != JsonValueKind.Array) continue;
            foreach (var contract in contracts.EnumerateArray())
            {
                if (!contract.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "TransferContract") continue;
                if (!contract.TryGetProperty("parameter", out var param) || !param.TryGetProperty("value", out var value)) continue;
                var owner = value.TryGetProperty("owner_address", out var ownerEl) ? ownerEl.GetString() : null;
                var to = value.TryGetProperty("to_address", out var toEl) ? toEl.GetString() : null;
                var amount = value.TryGetProperty("amount", out var amountEl) ? amountEl.GetInt64() : 0;
                if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(to)) continue;
                var incoming = string.Equals(to, _sourceAddress, StringComparison.OrdinalIgnoreCase);
                if (incoming) list.Add(new Tx(id, ts, true, amount));
            }
        }
        return list;
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var balance = await GetBalanceAsync(ct);
        var amount = balance - _reserve;
        if (amount < _minDeposit) { Log?.Invoke($"Баланс {balance:0.######} TRX; после резерва недостаточно для sweep."); return; }
        var sun = checked((long)Math.Floor(amount * 1_000_000m));
        if (sun <= 0) return;

        Log?.Invoke($"Создаю перевод {sun / 1_000_000m:0.######} TRX → {_receiver}");
        var ext = await _txClient!.CreateTransactionAsync(_sourceAddress, _receiver, sun);
        if (ext is null || ext.Transaction is null || !ext.Result.Result) throw new InvalidOperationException("TRON не смог создать транзакцию.");
        var signed = _txClient.GetTransactionSign(ext.Transaction, _privateKey);
        var result = await _txClient.BroadcastTransactionAsync(signed);
        if (!result.Result) throw new InvalidOperationException("TRON отклонил broadcast транзакции.");
        var txid = signed.GetTxid();
        Log?.Invoke($"Транзакция отправлена. TXID: {txid}");
        Log?.Invoke("Важно: broadcast ещё не означает solidified confirmation; программа не считает операцию окончательно подтверждённой до проверки цепочки.");
    }

    private async Task<decimal> GetBalanceAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"/v1/accounts/{Uri.EscapeDataString(_sourceAddress)}", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return 0;
        var item = data[0];
        var sun = item.TryGetProperty("balance", out var bal) ? bal.GetInt64() : 0;
        return sun / 1_000_000m;
    }
}
