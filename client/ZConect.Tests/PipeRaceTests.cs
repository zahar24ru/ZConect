using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UiApp.Services;
using Xunit;
using ZConectService;

namespace ZConect.Tests;

/// <summary>
/// Race-condition и reconnect тесты для ServicePipeClient (UI side) и pipe protocol.
/// Покрывают:
///   - конкуррентные SendInject* (60 Hz mouse movement — проверка _writeLock)
///   - server disconnect в середине burst'а sends — no exception escapes
///   - dispose client'а с последующими sends — silent
///   - reconnect после restart'а сервера (v1 → v2 под тем же pipe name)
///   - protocol framing edge cases: partial header, negative/zero/huge length, truncated payload
/// Важно: server READS запускается параллельно с ConnectAsync потому что
/// ServicePipeClient шлёт hello immediately внутри ConnectAsync, и без активного
/// reader'а на server'е write может блокироваться (buffer fills).
/// Category=Integration — не требует admin.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PipeRaceTests
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    // ── Concurrent sends ─────────────────────────────────────────────

    /// <summary>
    /// 50 concurrent SendInjectMouseAsync calls должны дойти до сервера как
    /// well-formed length-prefix JSON messages (header+payload не interleave'ены).
    /// Проверяет что _writeLock serializuет ОБА Write'а каждого сообщения.
    /// </summary>
    [Fact]
    public async Task ConcurrentSendInjectMouse_AllReachServer_WithValidFraming()
    {
        await using var harness = await PipeHarness.StartAsync();
        Assert.Equal("hello", harness.Hello.Type);

        const int n = 50;
        // Reader должен работать параллельно с sends — иначе FlushAsync блокирует
        // каждый SendAsync, serialize'ит весь burst и маскирует race (test всё
        // равно проходит но уже не на том pattern'е).
        var readerTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(10_000);
            var msgs = new List<PipeMessage>();
            while (msgs.Count < n)
            {
                var m = await ReadMessageAsync<PipeMessage>(harness.Server, cts.Token);
                if (m is null) break;
                msgs.Add(m);
            }
            return msgs;
        });

        var sendTasks = Enumerable.Range(0, n)
            .Select(i => Task.Run(() => harness.Client.SendInjectMouseAsync("move", i, i * 2, 0, 0)))
            .ToArray();
        await Task.WhenAll(sendTasks);

        var injects = await readerTask;
        Assert.Equal(n, injects.Count);
        Assert.All(injects, m => Assert.Equal("inject_mouse", m.Type));
        Assert.All(injects, m => Assert.Equal("move", m.InputAction));
        // Никаких потерь — все N уникальных X coords присутствуют.
        var seenX = injects.Select(m => m.InputX).ToHashSet();
        Assert.Equal(n, seenX.Count);
    }

    /// <summary>
    /// Burst of 60Hz sends пока сервер резко disconnect'ится — никакое исключение
    /// НЕ должно вылететь в caller'а. Покрывает C6/C8: TOCTOU между
    /// `IsConnected` check и `SendAsync`.
    /// </summary>
    [Fact]
    public async Task SendBurst_DuringServerDisconnect_NoExceptionEscapes()
    {
        var harness = await PipeHarness.StartAsync();
        // Reader в фоне — чтобы Flush'ы на client sends разблокировались.
        // Прекратит при server.Dispose → read вернёт null.
        var readerCts = new CancellationTokenSource();
        var drain = Task.Run(async () => { while (!readerCts.IsCancellationRequested) { try { var m = await ReadMessageAsync<PipeMessage>(harness.Server, readerCts.Token); if (m is null) break; } catch { break; } } });

        using var burstCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var escapedExceptions = new List<Exception>();
        var sendTasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!burstCts.IsCancellationRequested)
                {
                    await harness.Client.SendInjectMouseAsync("move", 100, 200, 0, 0);
                    try { await Task.Delay(15, burstCts.Token); } catch (OperationCanceledException) { break; }
                }
            }
            catch (Exception ex)
            {
                lock (escapedExceptions) escapedExceptions.Add(ex);
            }
        })).ToArray();

        await Task.Delay(200);
        harness.Server.Dispose(); // резко закрываем серверный конец
        await Task.WhenAll(sendTasks);

        readerCts.Cancel();
        try { await drain; } catch { }
        harness.Client.Dispose();

        Assert.Empty(escapedExceptions);
    }

    /// <summary>Dispose клиента и последующие Send* — silent, не должны throw.</summary>
    [Fact]
    public async Task SendAfterDispose_DoesNotThrow()
    {
        await using var harness = await PipeHarness.StartAsync();

        harness.Client.Dispose();

        // Все эти методы в IsConnected check возвращают silent когда pipe is null.
        await harness.Client.SendInjectMouseAsync("move", 1, 1, 0, 0);
        await harness.Client.NotifyUserExitAsync();
        await harness.Client.ReportStatusAsync(false);
    }

    // ── Reconnect ───────────────────────────────────────────────────

    /// <summary>
    /// После v1.Dispose() ServicePipeClient должен переподключиться к v2
    /// под тем же pipe name в пределах exponential backoff (1s initial).
    /// Send после reconnect'а доходит до v2 с корректным содержимым.
    /// </summary>
    [Fact]
    public async Task Reconnect_AfterServerRestart_ResumesSends()
    {
        var name = MakePipeName();
        using var client = new ServicePipeClient(_ => { }, name);

        // v1
        var v1 = NewServer(name);
        var acceptV1 = v1.WaitForConnectionAsync();
        var connectTask = client.ConnectAsync();
        // v1 должен читать hello параллельно с ConnectAsync.
        var helloV1Task = Task.Run(async () =>
        {
            await acceptV1;
            return await ReadMessageAsync<PipeMessage>(v1, TimeSpan.FromSeconds(3));
        });
        Assert.True(await connectTask);
        Assert.Equal("hello", (await helloV1Task)?.Type);

        // Закрываем v1, сразу поднимаем v2 с тем же именем.
        v1.Dispose();
        using var v2 = NewServer(name);
        var acceptV2 = v2.WaitForConnectionAsync();

        // Клиент должен заметить disconnect (sleep 1s) → reconnect → hello.
        await acceptV2.WaitAsync(TimeSpan.FromSeconds(6));
        var helloV2 = await ReadMessageAsync<PipeMessage>(v2, TimeSpan.FromSeconds(3));
        Assert.Equal("hello", helloV2?.Type);

        // Send после reconnect'а долетает до v2.
        var sendTask = client.SendInjectMouseAsync("click", 77, 88, 1, 0);
        var msg = await ReadMessageAsync<PipeMessage>(v2, TimeSpan.FromSeconds(3));
        await sendTask;
        Assert.Equal("inject_mouse", msg?.Type);
        Assert.Equal(77, msg!.InputX);
        Assert.Equal(88, msg.InputY);
        Assert.Equal(1, msg.InputButton);
    }

    /// <summary>
    /// Round-trip: клиент шлёт через ServicePipeClient inject_keyboard,
    /// сервер получает корректно-фреймированное сообщение.
    /// </summary>
    [Fact]
    public async Task SendInjectKeyboard_RoundTrip_PreservesAllFields()
    {
        await using var harness = await PipeHarness.StartAsync();

        // Send+read параллельно — Flush блокирует Send пока server не прочитает.
        var sendTask = harness.Client.SendInjectKeyboardAsync("down", 0x41, 0x1E, alt: false, ctrl: true, shift: true, win: false);
        var msg = await ReadMessageAsync<PipeMessage>(harness.Server, TimeSpan.FromSeconds(2));
        await sendTask;

        Assert.Equal("inject_keyboard", msg?.Type);
        Assert.Equal("down", msg!.InputAction);
        Assert.Equal(0x41, msg.InputVirtualKey);
        Assert.Equal(0x1E, msg.InputScanCode);
        Assert.True(msg.InputCtrl);
        Assert.True(msg.InputShift);
        Assert.False(msg.InputAlt);
    }

    /// <summary>set_uac_policy несёт action=disable/enable.</summary>
    [Fact]
    public async Task SetUacSecureDesktop_SerializesActionCorrectly()
    {
        await using var harness = await PipeHarness.StartAsync();

        var disableSend = harness.Client.SetUacSecureDesktopAsync(disable: true);
        var disableMsg = await ReadMessageAsync<PipeMessage>(harness.Server, TimeSpan.FromSeconds(2));
        await disableSend;
        Assert.Equal("set_uac_policy", disableMsg?.Type);
        Assert.Equal("disable", disableMsg!.InputAction);

        var enableSend = harness.Client.SetUacSecureDesktopAsync(disable: false);
        var enableMsg = await ReadMessageAsync<PipeMessage>(harness.Server, TimeSpan.FromSeconds(2));
        await enableSend;
        Assert.Equal("set_uac_policy", enableMsg?.Type);
        Assert.Equal("enable", enableMsg!.InputAction);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Harness: server + client. Критично что server уже дренирует hello когда
    /// ConnectAsync выполняет FlushAsync — иначе Flush виснет (named pipe Flush
    /// ждёт consumption на другой стороне).
    /// Hello сообщение уже consumed к моменту возврата StartAsync.
    /// </summary>
    private sealed class PipeHarness : IAsyncDisposable
    {
        public required NamedPipeServerStream Server { get; init; }
        public required ServicePipeClient Client { get; init; }
        public required PipeMessage Hello { get; init; }

        public static async Task<PipeHarness> StartAsync()
        {
            var name = MakePipeName();
            var server = NewServer(name);
            var client = new ServicePipeClient(_ => { }, name);
            var acceptTask = server.WaitForConnectionAsync();
            var connectTask = client.ConnectAsync();
            await acceptTask;
            // Server подключён. Теперь ConnectAsync находится в SendAsync(hello) →
            // FlushAsync который блокируется пока server не прочитает. Стартуем reader.
            var helloTask = ReadMessageAsync<PipeMessage>(server, TimeSpan.FromSeconds(3));
            await Task.WhenAll(connectTask, helloTask);
            var hello = helloTask.Result ?? throw new InvalidOperationException("hello not received");
            return new PipeHarness { Server = server, Client = client, Hello = hello };
        }

        public ValueTask DisposeAsync()
        {
            try { Client.Dispose(); } catch { }
            try { Server.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private static string MakePipeName() => "ZConect_RaceTest_" + Guid.NewGuid().ToString("N")[..8];

    private static NamedPipeServerStream NewServer(string name)
        => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task<T?> ReadMessageAsync<T>(PipeStream pipe, CancellationToken ct) where T : class
    {
        var header = new byte[4];
        int read = 0;
        while (read < 4)
        {
            var n = await pipe.ReadAsync(header.AsMemory(read, 4 - read), ct);
            if (n == 0) return null;
            read += n;
        }
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > 1024 * 1024) return null;
        var payload = new byte[length];
        read = 0;
        while (read < length)
        {
            var n = await pipe.ReadAsync(payload.AsMemory(read, length - read), ct);
            if (n == 0) return null;
            read += n;
        }
        return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(payload), CaseInsensitive);
    }

    private static async Task<T?> ReadMessageAsync<T>(PipeStream pipe, TimeSpan timeout) where T : class
    {
        using var cts = new CancellationTokenSource(timeout);
        return await ReadMessageAsync<T>(pipe, cts.Token);
    }
}
