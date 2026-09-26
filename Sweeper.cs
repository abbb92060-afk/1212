using System.IO;
using System.Net.Http;
using System.Text.Json;
using NBitcoin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TronNet;

namespace TronAutoSweeper;

public sealed class Sweeper
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.trongrid.io") };
    private readonly SemaphoreSlim _sweepLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private ITransactionClient? _txClient;
    private TronNetOptions? _options;
    private string _privateKey = "";
    private int _derivationIndex;
    private string _sourceAddress = "";
    private string _sourceHexAddress = "";
    private string _receiver = "";
    private decimal _minDeposit;
    private decimal _reserve;
    private string _apiKey = "";
    private string _statePath = "";
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Log;
    public event Action<string>? StateChanged;
    public string SourceAddress => _sourceAddress;

    public Task ConfigureAsync(string seedPhrase, string receiver, decimal minDeposit, decimal reserve, string apiKey, string stateDirectory, int derivationIndex = 0)
    {
        _derivationIndex = derivationIndex;
        _privateKey = DerivePrivateKeyFromSeed(seedPhrase, derivationIndex);
        _receiver = receiver;
        _minDeposit = minDeposit;
        _reserve = reserve;
        _apiKey = apiKey;
        _statePath = Path.Combine(stateDirectory, "processed-txids.json");

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
        _txClient = provider.GetRequiredService<ITransactionClient>();
        _options = provider.GetRequiredService<IOptions<TronNetOptions>>().Value;

        var key = new TronECKey(_privateKey, _options.Network);
        _sourceAddress = key.GetPublicAddress();
        if (!TronAddress.IsValidBase58(_sourceAddress))
            throw new InvalidOperationException("Не удалось получить корректный TRON-адрес из приватного ключа.");

        _sourceHexAddress = Base58ToHexAddress(_sourceAddress);

        if (!TronAddress.IsValidBase58(_receiver))
            throw new InvalidOperationException("Адрес получателя неверен.");
        if (string.Equals(_sourceAddress, _receiver, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Адрес получателя не должен совпадать с исходным.");
        if (_minDeposit < 0 || _reserve < 0)
            throw new InvalidOperationException("Минимальный депозит и резерв не могут быть отрицательными.");

        LoadState();
        Log?.Invoke($"Адрес из seed-фразы: {_sourceAddress}");
        Log?.Invoke($"Derivation path: m/44'/195'/0'/0/{_derivationIndex}");
        Log?.Invoke($"HEX-адрес источника: {_sourceHexAddress}");
        Log?.Invoke("Seed-фраза обработана локально; секретный ключ не отправляется в TronGrid.");
        return Task.CompletedTask;
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
        _cts?.Dispose();
        _cts = null;
        StateChanged?.Invoke("Остановлено");
        Log?.Invoke("Мониторинг остановлен.");
    }

    private async Task EstablishBaselineAsync(CancellationToken ct)
    {
        foreach (var tx in await GetTransactionsAsync(ct))
            _seen.Add(tx.Id);

        SaveState();
        Log?.Invoke("Базовая история зафиксирована; уже существующие депозиты повторно отправляться не будут.");
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

                    if (!tx.Incoming || tx.AmountSun <= 0)
                    {
                        _seen.Add(tx.Id);
                        SaveState();
                        continue;
                    }

                    Log?.Invoke($"Обнаружен входящий TRX: {tx.AmountSun / 1_000_000m:0.######} TRX, TX {tx.Id}");

                    if (tx.AmountSun / 1_000_000m < _minDeposit)
                    {
                        _seen.Add(tx.Id);
                        SaveState();
                        Log?.Invoke("Сумма ниже минимального порога.");
                        continue;
                    }

                    await _sweepLock.WaitAsync(ct);
                    try
                    {
                        // TXID добавляется в историю только после успешного broadcast.
                        // При временной ошибке следующая итерация попробует снова.
                        var sweepTxid = await SweepAsync(ct);
                        if (!string.IsNullOrWhiteSpace(sweepTxid))
                        {
                            _seen.Add(tx.Id);
                            SaveState();
                        }
                    }
                    finally
                    {
                        _sweepLock.Release();
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

    private sealed record Tx(string Id, long Timestamp, bool Incoming, long AmountSun);

    private async Task<List<Tx>> GetTransactionsAsync(CancellationToken ct)
    {
        var url = $"/v1/accounts/{Uri.EscapeDataString(_sourceAddress)}/transactions?only_confirmed=true&limit=50&order_by=block_timestamp,desc";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var list = new List<Tx>();

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("txID", out var idEl)) continue;
            var id = idEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(id)) continue;

            long ts = 0;
            if (item.TryGetProperty("block_timestamp", out var tsEl) &&
                tsEl.ValueKind == JsonValueKind.Number &&
                tsEl.TryGetInt64(out var parsedTimestamp))
            {
                ts = parsedTimestamp;
            }

            if (!item.TryGetProperty("raw_data", out var raw) ||
                !raw.TryGetProperty("contract", out var contracts) ||
                contracts.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var contract in contracts.EnumerateArray())
            {
                if (!contract.TryGetProperty("type", out var typeEl) ||
                    typeEl.GetString() != "TransferContract") continue;

                if (!contract.TryGetProperty("parameter", out var param) ||
                    !param.TryGetProperty("value", out var value)) continue;

                var owner = value.TryGetProperty("owner_address", out var ownerEl) ? ownerEl.GetString() : null;
                var to = value.TryGetProperty("to_address", out var toEl) ? toEl.GetString() : null;
                var amount = value.TryGetProperty("amount", out var amountEl) && amountEl.TryGetInt64(out var amountValue)
                    ? amountValue : 0;

                if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(to)) continue;

                // TronGrid raw_data содержит TRON-адреса в hex-виде (41...).
                // Сравниваем именно с HEX-адресом нашего кошелька.
                var incoming = string.Equals(to, _sourceHexAddress, StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(owner, _sourceHexAddress, StringComparison.OrdinalIgnoreCase);

                if (incoming)
                    list.Add(new Tx(id, ts, true, amount));
            }
        }

        return list;
    }

    private static string DerivePrivateKeyFromSeed(string seedPhrase, int derivationIndex)
    {
        var normalized = string.Join(' ', seedPhrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        try
        {
            var mnemonic = new Mnemonic(normalized);
            var path = new KeyPath($"m/44'/195'/0'/0/{derivationIndex}");
            var extKey = mnemonic.DeriveExtKey();
            var child = extKey.Derive(path);
            return Convert.ToHexString(child.PrivateKey.ToBytes());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Не удалось получить TRON-ключ из seed-фразы. Проверьте слова и derivation index.", ex);
        }
    }

    private async Task<string?> SweepAsync(CancellationToken ct)
    {
        var balance = await GetBalanceAsync(ct);
        var amount = balance - _reserve;
        if (amount < _minDeposit)
        {
            Log?.Invoke($"Баланс {balance:0.######} TRX; после резерва недостаточно для sweep.");
            return null;
        }

        var sun = checked((long)Math.Floor(amount * 1_000_000m));
        if (sun <= 0) return null;

        Log?.Invoke($"Создаю перевод {sun / 1_000_000m:0.######} TRX → {_receiver}");
        var ext = await _txClient!.CreateTransactionAsync(_sourceAddress, _receiver, sun);
        if (ext is null || ext.Transaction is null || !ext.Result.Result)
            throw new InvalidOperationException("TRON не смог создать транзакцию.");

        var signed = _txClient.GetTransactionSign(ext.Transaction, _privateKey);
        var result = await _txClient.BroadcastTransactionAsync(signed);
        if (!result.Result)
            throw new InvalidOperationException("TRON отклонил broadcast транзакции.");

        var txid = signed.GetTxid();
        Log?.Invoke($"Транзакция отправлена. TXID: {txid}");
        Log?.Invoke("Broadcast принят узлом. Это ещё не равно окончательному подтверждению в сети.");
        return txid;
    }

    private async Task<decimal> GetBalanceAsync(CancellationToken ct)
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

    private static string Base58ToHexAddress(string address)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("Пустой TRON-адрес.");

        var bytes = new List<byte> { 0 };
        foreach (var c in address)
        {
            var carry = alphabet.IndexOf(c);
            if (carry < 0)
                throw new FormatException("Некорректный Base58-адрес TRON.");

            for (var i = 0; i < bytes.Count; i++)
            {
                var value = bytes[i] * 58 + carry;
                bytes[i] = (byte)(value & 0xff);
                carry = value >> 8;
            }

            while (carry > 0)
            {
                bytes.Add((byte)(carry & 0xff));
                carry >>= 8;
            }
        }

        var leadingZeros = address.TakeWhile(c => c == '1').Count();
        bytes.Reverse();

        var decoded = Enumerable.Repeat((byte)0, leadingZeros)
            .Concat(bytes.SkipWhile((b, i) => i == 0 && b == 0))
            .ToArray();

        if (decoded.Length != 25 || decoded[0] != 0x41)
            throw new FormatException("Некорректный размер TRON-адреса.");

        var payload = decoded[..21];
        var checksum = decoded[21..];
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Security.Cryptography.SHA256.HashData(payload));

        if (!checksum.SequenceEqual(hash[..4]))
            throw new FormatException("Неверная контрольная сумма TRON-адреса.");

        return Convert.ToHexString(payload).ToLowerInvariant();
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_statePath));
            if (ids is null) return;
            foreach (var id in ids.Where(x => !string.IsNullOrWhiteSpace(x)))
                _seen.Add(id);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Не удалось загрузить историю TXID: {ex.Message}");
        }
    }

    private void SaveState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_seen.OrderBy(x => x).ToList()));
            File.Move(temp, _statePath, true);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Не удалось сохранить историю TXID: {ex.Message}");
        }
    }
}
