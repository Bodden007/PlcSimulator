using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CoreLink.PlcSimulator;

internal static class Program
{
    private const int Port = 1502;
    private const byte UnitId = 255;

    private const int OnlineSeconds = 10;
    private const int OfflineSeconds = 10;
    private const int RecoverySeconds = 10;
    private const int HangSeconds = 10;

    private static readonly object ConsoleLock = new();

    private static async Task Main(string[] args)
    {
        string mode = args.Length == 0
            ? "--cycle"
            : args[0].ToLowerInvariant();

        if (args.Length > 1 ||
            (mode != "--cycle" && mode != "--stable"))
        {
            Console.WriteLine(
                "Usage: CoreLink.PlcSimulator [--cycle|--stable]");

            return;
        }

        await using var server = new PlcServer(
            IPAddress.Loopback,
            Port,
            UnitId);

        using var cts = new CancellationTokenSource();

        Log("CORELINK PLC SIMULATOR");
        Log($"MODE: {mode}");
        Log("Input values: random every 1 second; F = fixed test values");
        Log("Supported: FC03 / FC04 / FC06 / FC16");
        Log("M = map | V = verbose | H = hang FC03/FC04 | F = fixed/random | Q = quit");
        Log("0..9 = Measurme | 0 = NONE | 1 = US | 2 = METRIC");
        Log("Measurme initially 1 (US), changed only by keyboard");

        Task? autoTask = null;

        if (mode == "--cycle")
        {
            Log("AUTO: 10s ONLINE / 10s OFFLINE / 10s RECOVERY / 10s HANG FC03/FC04 / 10s RECOVERY");

            autoTask = RunAutomaticAsync(
                server,
                cts.Token);
        }
        else
        {
            server.Start();
            Log("STABLE: PLC continuously ONLINE");
        }

        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(100, cts.Token);
                    continue;
                }


                ConsoleKey key = Console.ReadKey(true).Key;

                if (key is >= ConsoleKey.D0 and <= ConsoleKey.D9)
                {
                    server.SetMeasurme(
                        (ushort)(key - ConsoleKey.D0));
                }
                else if (key is >= ConsoleKey.NumPad0 and <= ConsoleKey.NumPad9)
                {
                    server.SetMeasurme(
                        (ushort)(key - ConsoleKey.NumPad0));
                }
                else
                {
                    switch (key)
                    {
                        case ConsoleKey.M:
                            server.PrintRegisterMap();
                            break;

                        case ConsoleKey.V:
                            server.ToggleVerbose();
                            break;

                        case ConsoleKey.H:
                            server.ToggleHang();
                            break;

                        case ConsoleKey.F:
                            server.ToggleFixedValues();
                            break;

                        case ConsoleKey.Q:
                            cts.Cancel();
                            break;
                    }
                }

            }
        }
        catch (OperationCanceledException)
            when (cts.IsCancellationRequested)
        {
            // Штатное завершение.
        }
        finally
        {
            cts.Cancel();

            if (autoTask is not null)
            {
                try
                {
                    await autoTask;
                }
                catch (OperationCanceledException)
                    when (cts.IsCancellationRequested)
                {
                    // Штатная остановка.
                }
            }

            await server.StopAsync();

            ClearStatus();
            Log("SIMULATOR STOPPED");
        }
    }

    /// <summary>
    /// Автоматический цикл отказоустойчивости: ONLINE -> OFFLINE ->
    /// RECOVERY -> HANG FC03/FC04 с открытым TCP -> RECOVERY.
    /// Повторяется до завершения приложения.
    /// </summary>
    private static async Task RunAutomaticAsync(
        PlcServer server,
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                server.SetHang(false);
                server.Start();
                Log("AUTO PHASE: ONLINE");
                await RunStateIntervalAsync(
                    server, OnlineSeconds, true, token);

                token.ThrowIfCancellationRequested();
                await server.StopAsync();
                Log("AUTO PHASE: OFFLINE");
                await RunStateIntervalAsync(
                    server, OfflineSeconds, false, token);

                token.ThrowIfCancellationRequested();
                server.Start();
                Log("AUTO PHASE: RECOVERY AFTER OFFLINE");
                await RunStateIntervalAsync(
                    server, RecoverySeconds, true, token);

                token.ThrowIfCancellationRequested();
                server.SetHang(true);
                Log("AUTO PHASE: HANG FC03/FC04 (TCP CONNECTED)");
                await RunStateIntervalAsync(
                    server, HangSeconds, true, token);

                token.ThrowIfCancellationRequested();
                server.SetHang(false);
                Log("AUTO PHASE: RECOVERY AFTER HANG");
                await RunStateIntervalAsync(
                    server, RecoverySeconds, true, token);
            }
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            // Штатное завершение.
        }
        finally
        {
            // Не оставлять HANG активным при повторном запуске.
            server.SetHang(false);
        }
    }

    /// <summary>
    /// Показывает текущее состояние PLC и значения.
    /// </summary>
    private static async Task RunStateIntervalAsync(
        PlcServer server,
        int seconds,
        bool online,
        CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed.TotalSeconds < seconds)
        {
            token.ThrowIfCancellationRequested();

            int remaining = Math.Max(
                0,
                (int)Math.Ceiling(
                    seconds - stopwatch.Elapsed.TotalSeconds));

            string values = online
                ? server.GetLiveValues()
                : "NO DATA";

            ShowStatus(
                online ? "ONLINE " : "OFFLINE",
                remaining,
                values);

            double nextSecond =
                Math.Floor(stopwatch.Elapsed.TotalSeconds) + 1;

            double delayMs = Math.Min(
                1000,
                Math.Max(
                    1,
                    (nextSecond - stopwatch.Elapsed.TotalSeconds)
                    * 1000));

            await Task.Delay(
                TimeSpan.FromMilliseconds(delayMs),
                token);
        }

        ClearStatus();
    }

    private static void ShowStatus(
        string state,
        int remaining,
        string values)
    {
        lock (ConsoleLock)
        {
            ClearStatusUnsafe();

            Console.Write(
                $"[{state}] Next: {remaining,2}s | {values}");
        }
    }

    private static void ClearStatusUnsafe()
    {
        Console.Write(
            "\r" + new string(' ', 160) + "\r");
    }

    private static void ClearStatus()
    {
        lock (ConsoleLock)
        {
            ClearStatusUnsafe();
        }
    }

    internal static void Log(string message)
    {
        lock (ConsoleLock)
        {
            ClearStatusUnsafe();

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }

    internal static void PrintLocked(Action print)
    {
        lock (ConsoleLock)
        {
            ClearStatusUnsafe();
            print();
        }
    }
}

/// <summary>
/// Локальный Modbus TCP PLC Simulator.
///
/// Поддерживает:
/// FC03 Read Holding Registers;
/// FC04 Read Input Registers;
/// FC06 Write Single Holding Register;
/// FC16 Write Multiple Holding Registers;
/// случайные Word и Float32;
/// отключение TCP;
/// зависание FC03/FC04 при открытом TCP;
/// физическую карту регистров;
/// ручной выбор Measurme (UA/EU/invalid).
/// </summary>
internal sealed class PlcServer : IAsyncDisposable
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly byte _unitId;

    private readonly object _registerLock = new();

    // Input Registers (FC04) и Holding Registers (FC03/06/16)
    // — независимые области Modbus.
    private readonly ushort[] _registers = new ushort[44];
    // Holding: 0..23, адреса 20..23 — отдельные тестовые регистры.
    private readonly ushort[] _holdingRegisters = new ushort[24];

    private static readonly RegisterInfo[] RegisterMap =
    [
        new("Measurme", 1, RegisterKind.Word),
        new("Pressure_1", 8, RegisterKind.Float),
        new("Opko_1", 12, RegisterKind.Float),
        new("StatOpko_1", 14, RegisterKind.Word),
        new("TemperatureOutlet", 20, RegisterKind.Float),
        new("TemperatureVaporizer", 22, RegisterKind.Float),
        new("TemperatureBath", 24, RegisterKind.Float),
        new("PUMP_1RPM", 30, RegisterKind.Float),
        new("PUMP_1SCFStage", 34, RegisterKind.Float),
        new("PUMP_1TotalSCFM", 42, RegisterKind.Float)
    ];

    private readonly ConcurrentDictionary<int, TcpClient>
        _clients = new();

    private readonly ConcurrentDictionary<int, Task>
        _handlers = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _generatorTask;

    private int _nextClientId;
    private int _verbose;
    private int _fixedValues;

    // 0 = ответы разрешены.
    // 1 = FC03/FC04 не получают ответ, TCP остаётся открытым.
    private int _hangResponses;

    private long _generation;

    public PlcServer(
        IPAddress address,
        int port,
        byte unitId)
    {
        _address = address;
        _port = port;
        _unitId = unitId;

        // Состояние устройства, а не случайный сигнал.
        // Сохраняется при Stop/Start в режиме --cycle.
        _registers[1] = 1;

        // Контрольный Holding-блок для FC03/06/16.
        // Порядок Float: младшее WORD, затем старшее.
        _holdingRegisters[0] = 1;
        _holdingRegisters[1] = 17;
        _holdingRegisters[3] = 42;
        WriteFloat(_holdingRegisters, 5, 1234.5f);
        for (int registerAddress = 7; registerAddress <= 15; registerAddress += 2)
            WriteFloat(_holdingRegisters, registerAddress, 10.5f + registerAddress);
    }

    /// <summary>
    /// Запускает TCP listener и генератор регистров.
    /// </summary>
    public void Start()
    {
        if (_cts is not null)
            return;

        var cts = new CancellationTokenSource();

        var listener = new TcpListener(
            _address,
            _port);

        try
        {
            listener.Start();
        }
        catch
        {
            cts.Dispose();
            throw;
        }

        _cts = cts;
        _listener = listener;

        GenerateRegisters();

        _acceptTask = AcceptLoopAsync(
            listener,
            cts.Token);

        _generatorTask = GenerateLoopAsync(
            cts.Token);

        Program.Log(
            $"PLC ONLINE {_address}:{_port}, UnitId={_unitId}");
    }

    /// <summary>
    /// Останавливает PLC и закрывает TCP-соединения.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        CancellationTokenSource cts = _cts;

        cts.Cancel();

        _listener?.Stop();

        foreach (TcpClient client in _clients.Values)
        {
            client.Dispose();
        }

        if (_acceptTask is not null)
            await _acceptTask;

        if (_generatorTask is not null)
            await _generatorTask;

        await Task.WhenAll(_handlers.Values.ToArray());

        _clients.Clear();
        _handlers.Clear();

        _listener = null;
        _acceptTask = null;
        _generatorTask = null;
        _cts = null;

        cts.Dispose();

        Program.Log("PLC OFFLINE");
    }

    /// <summary>
    /// Ручная установка системы единиц измерения.
    /// <summary>
    /// Ручная установка Measurme от 0 до 9.
    ///
    /// 0 = отсутствие системы единиц.
    /// 1 = UA.
    /// 2 = EU.
    /// 3..9 = дополнительные или неизвестные системы.
    ///
    /// Значение сохраняется до следующего ручного изменения.
    /// Генератор случайных значений его не изменяет.
    /// </summary>
    public void SetMeasurme(ushort value)
    {
        if (value > 9)
            throw new ArgumentOutOfRangeException(nameof(value));

        ushort previous;

        lock (_registerLock)
        {
            previous = _registers[1];
            _registers[1] = value;
            GenerateRegistersLocked();
        }

        if (previous == value)
        {
            Program.Log($"MEASURME unchanged: {value}");
            return;
        }

        string description = value switch
        {
            0 => "NONE",
            1 => "US",
            2 => "METRIC",
            _ => "UNREGISTERED / TEST"
        };

        Program.Log(
            $"MEASURME: {previous} -> {value} ({description})");
    }


    /// <summary>
    /// Включает или выключает симуляцию зависания.
    ///
    /// При HANG ответы FC03/FC04 не отправляются.
    /// TCP listener продолжает работать.
    /// </summary>
    public void ToggleHang()
    {
        SetHang(Volatile.Read(ref _hangResponses) == 0);
    }

    /// <summary>
    /// Явно задаёт режим HANG, чтобы автоматический сценарий
    /// не зависел от предыдущего состояния переключателя.
    /// </summary>
    public void SetHang(bool enabled)
    {
        int next = enabled ? 1 : 0;
        int previous = Interlocked.Exchange(ref _hangResponses, next);

        if (previous == next)
            return;

        Program.Log(enabled
            ? "PLC HANG: FC03/FC04 responses suspended, TCP remains open"
            : "PLC RECOVERED: FC03/FC04 responses enabled");
    }

    /// <summary>
    /// Включает или выключает подробный Modbus-журнал.
    /// </summary>
    public void ToggleVerbose()
    {
        int next =
            Volatile.Read(ref _verbose) == 0
                ? 1
                : 0;

        Volatile.Write(ref _verbose, next);

        Program.Log(
            $"MODBUS VERBOSE {(next == 1 ? "ON" : "OFF")}");
    }

    /// <summary>
    /// Переключает входные регистры между случайными и фиксированными
    /// значениями для проверки точности декодирования ReadAsync.
    /// Holding Registers сохраняют записанные клиентом значения.
    /// </summary>
    public void ToggleFixedValues()
    {
        bool fixedMode;
        lock (_registerLock)
        {
            fixedMode = _fixedValues == 0;
            _fixedValues = fixedMode ? 1 : 0;
            GenerateRegistersLocked();
        }

        Program.Log(fixedMode
            ? "INPUT FIXED: P1=1500 psi / 103.42 bar, OPKO=1800 psi / 124.11 bar, RPM=1200"
            : "INPUT RANDOM: values update every second");
    }

    /// <summary>
    /// Возвращает актуальные значения для консоли.
    /// </summary>
    public string GetLiveValues()
    {
        lock (_registerLock)
        {
            float pressure = ReadFloat(8);
            float opko = ReadFloat(12);
            float rpm = ReadFloat(30);

            return
                $"Gen={_generation} " +
                $"Mode={(_fixedValues == 1 ? "FIXED" : "RANDOM")} " +
                $"Measurme={_registers[1]} " +
                $"P1={pressure:F1} " +
                $"OPKO={opko:F1} " +
                $"RPM={rpm:F1}";
        }
    }

    private float ReadFloat(int address)
    {
        uint bits =
            _registers[address] |
            ((uint)_registers[address + 1] << 16);

        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>
    /// Полная физическая карта Modbus-регистров.
    /// </summary>
    public void PrintRegisterMap()
    {
        ushort[] snapshot;
        ushort[] holdingSnapshot;
        long generation;

        lock (_registerLock)
        {
            snapshot = (ushort[])_registers.Clone();
            holdingSnapshot = (ushort[])_holdingRegisters.Clone();
            generation = _generation;
        }

        Program.PrintLocked(() =>
        {
            Console.WriteLine();

            Console.WriteLine(
                "==============================================================");

            Console.WriteLine("PLC PHYSICAL REGISTER MAP");

            Console.WriteLine(
                "==============================================================");

            Console.WriteLine(
                $"Endpoint    : {_address}:{_port}");

            Console.WriteLine(
                $"UnitId      : {_unitId}");

            Console.WriteLine(
                "Function    : 04 - Read Input Registers");

            Console.WriteLine(
                "Word order  : LOW WORD -> HIGH WORD");

            Console.WriteLine(
                $"Generation  : {generation}");

            Console.WriteLine(
                $"Registers   : {snapshot.Length}");

            Console.WriteLine();

            Console.WriteLine(
                $"{"ADDR",4}  {"NAME",-15} " +
                $"{"TYPE",-8} {"DEC",6}  {"HEX",4}  BIN");

            Console.WriteLine(new string('-', 65));

            for (int address = 0;
                 address < snapshot.Length;
                 address++)
            {
                var (name, kind) =
                    DescribeAddress(address);

                ushort word = snapshot[address];

                Console.WriteLine(
                    $"{address,4}  {name,-15} " +
                    $"{kind,-8} {word,6}  {word:X4}  " +
                    Convert.ToString(word, 2)
                        .PadLeft(16, '0'));
            }

            Console.WriteLine();

            Console.WriteLine("DECODED VALUES");
            Console.WriteLine(new string('-', 65));

            foreach (RegisterInfo register in RegisterMap)
            {
                if (register.Kind == RegisterKind.Word)
                {
                    Console.WriteLine(
                        $"{register.Name,-15} = " +
                        $"{snapshot[register.Address]} (Word)");
                }
                else
                {
                    uint bits =
                        snapshot[register.Address] |
                        ((uint)snapshot[register.Address + 1] << 16);

                    float value =
                        BitConverter.UInt32BitsToSingle(bits);

                    Console.WriteLine(
                        $"{register.Name,-15} = " +
                        $"{value,12:F3} [0x{bits:X8}]");
                }
            }

            Console.WriteLine(
                "==============================================================");

            Console.WriteLine("HOLDING REGISTERS (FC03 / FC06 / FC16)");
            Console.WriteLine("Addresses 20..23: diagnostic write test block");
            Console.WriteLine(new string('-', 65));
            for (int address = 0; address < holdingSnapshot.Length; address++)
            {
                var (name, kind) = address is >= 20 and <= 23
                    ? ($"TestWrite_{address - 20}", "WORD")
                    : DescribeAddress(address);
                Console.WriteLine(
                    $"{address,4}  {name,-15} {kind,-8} " +
                    $"{holdingSnapshot[address],6}  " +
                    $"{holdingSnapshot[address]:X4}");
            }
            Console.WriteLine(
                "==============================================================");

            Console.WriteLine();
        });
    }

    private static (string Name, string Kind)
        DescribeAddress(int address)
    {
        foreach (RegisterInfo register in RegisterMap)
        {
            if (address == register.Address)
            {
                return register.Kind == RegisterKind.Word
                    ? (register.Name, "WORD")
                    : (register.Name + ".Lo", "FLOAT");
            }

            if (register.Kind == RegisterKind.Float &&
                address == register.Address + 1)
            {
                return (
                    register.Name + ".Hi",
                    "FLOAT");
            }
        }

        return ("-- unused --", "--");
    }

    /// <summary>
    /// Принимает TCP-подключения клиентов.
    /// </summary>
    private async Task AcceptLoopAsync(
        TcpListener listener,
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client =
                    await listener.AcceptTcpClientAsync(token);

                int id =
                    Interlocked.Increment(ref _nextClientId);

                _clients[id] = client;

                Program.Log(
                    $"CLIENT #{id} CONNECTED: " +
                    $"{client.Client.RemoteEndPoint}");

                _handlers[id] =
                    HandleClientAsync(id, client, token);
            }
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            // Штатная остановка.
        }
        catch (ObjectDisposedException)
            when (token.IsCancellationRequested)
        {
            // Listener остановлен.
        }
        catch (SocketException)
            when (token.IsCancellationRequested)
        {
            // Listener остановлен.
        }
    }

    /// <summary>
    /// Обрабатывает Modbus TCP FC03, FC04, FC06 и FC16.
    /// FC03/FC04 — чтение непрерывного блока;
    /// FC06/FC16 — запись в отдельную область Holding Registers.
    /// HANG действует на FC03 и FC04.
    /// </summary>
    private async Task HandleClientAsync(
        int id,
        TcpClient client,
        CancellationToken token)
    {
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();

                while (!token.IsCancellationRequested)
                {
                    byte[] header = new byte[7];
                    await stream.ReadExactlyAsync(header, token);

                    ushort transactionId =
                        BinaryPrimitives.ReadUInt16BigEndian(
                            header.AsSpan(0, 2));

                    ushort protocolId =
                        BinaryPrimitives.ReadUInt16BigEndian(
                            header.AsSpan(2, 2));

                    ushort length =
                        BinaryPrimitives.ReadUInt16BigEndian(
                            header.AsSpan(4, 2));

                    if (protocolId != 0 || length < 2 || length > 254)
                    {
                        Program.Log($"CLIENT #{id}: INVALID MBAP");
                        break;
                    }

                    byte[] pdu = new byte[length - 1];
                    await stream.ReadExactlyAsync(pdu, token);

                    if (header[6] != _unitId)
                    {
                        Program.Log(
                            $"CLIENT #{id}: INVALID UNIT ID {header[6]}");
                        break;
                    }

                    byte fc = pdu[0];
                    ushort[] target = fc == 4 ? _registers : _holdingRegisters;

                    if (fc == 3 || fc == 4)
                    {
                        if (pdu.Length != 5)
                        {
                            await SendExceptionAsync(stream, transactionId,
                                fc, 3, token);
                            continue;
                        }

                        ushort address = BinaryPrimitives.ReadUInt16BigEndian(
                            pdu.AsSpan(1, 2));
                        ushort count = BinaryPrimitives.ReadUInt16BigEndian(
                            pdu.AsSpan(3, 2));

                        if (count == 0 || count > 125 ||
                            (int)address + count > target.Length)
                        {
                            await SendExceptionAsync(stream, transactionId,
                                fc, 2, token);
                            continue;
                        }

                        if ((fc == 3 || fc == 4) && Volatile.Read(ref _hangResponses) == 1)
                        {
                            Program.Log(
                                $"CLIENT #{id}: FC{fc:D2} RESPONSE SUSPENDED " +
                                $"Tx={transactionId}, TCP CONNECTED");

                            while (Volatile.Read(ref _hangResponses) == 1)
                            {
                                token.ThrowIfCancellationRequested();

                                if (client.Client.Poll(0, SelectMode.SelectRead) &&
                                    client.Client.Available == 0)
                                {
                                    Program.Log(
                                        $"CLIENT #{id}: TCP CLOSED BY CORE");
                                    return;
                                }

                                await Task.Delay(100, token);
                            }

                            // Запрос чтения, зависший в HANG, не отвечаем после восстановления.
                            continue;
                        }

                        byte[] response = CreateResponse(
                            transactionId, fc, 1 + count * 2);
                        response[8] = checked((byte)(count * 2));

                        lock (_registerLock)
                        {
                            for (int i = 0; i < count; i++)
                                BinaryPrimitives.WriteUInt16BigEndian(
                                    response.AsSpan(9 + i * 2, 2),
                                    target[address + i]);
                        }

                        await stream.WriteAsync(response, token);
                        LogRequest(id, fc, transactionId, address, count,
                            pdu, response);
                        continue;
                    }

                    if (fc == 6)
                    {
                        if (pdu.Length != 5)
                        {
                            await SendExceptionAsync(stream, transactionId,
                                fc, 3, token);
                            continue;
                        }

                        ushort address = BinaryPrimitives.ReadUInt16BigEndian(
                            pdu.AsSpan(1, 2));
                        ushort value = BinaryPrimitives.ReadUInt16BigEndian(
                            pdu.AsSpan(3, 2));

                        if (address >= _holdingRegisters.Length)
                        {
                            await SendExceptionAsync(stream, transactionId,
                                fc, 2, token);
                            continue;
                        }

                        lock (_registerLock)
                            _holdingRegisters[address] = value;

                        // FC06 подтверждает адрес и записанное значение.
                        byte[] response = CreateResponse(transactionId, fc, 4);
                        pdu.AsSpan(1, 4).CopyTo(response.AsSpan(8, 4));
                        await stream.WriteAsync(response, token);
                        LogRequest(id, fc, transactionId, address, 1,
                            pdu, response);
                        continue;
                    }

                    if (fc == 16)
                    {
                        if (pdu.Length < 6)
                        {
                            await SendExceptionAsync(stream, transactionId,
                                fc, 3, token);
                            continue;
                        }

                        ushort address = BinaryPrimitives.ReadUInt16BigEndian(
                            pdu.AsSpan(1, 2));
                        ushort count = BinaryPrimitives.ReadUInt16BigEndian(
                            pdu.AsSpan(3, 2));
                        int byteCount = pdu[5];

                        if (count == 0 || count > 123 ||
                            (int)address + count > _holdingRegisters.Length)
                        {
                            await SendExceptionAsync(stream, transactionId,
                                fc, 2, token);
                            continue;
                        }

                        if (byteCount != count * 2 ||
                            pdu.Length != 6 + byteCount)
                        {
                            await SendExceptionAsync(stream, transactionId,
                                fc, 3, token);
                            continue;
                        }

                        // Весь блок записывается под одной блокировкой.
                        lock (_registerLock)
                        {
                            for (int i = 0; i < count; i++)
                                _holdingRegisters[address + i] =
                                    BinaryPrimitives.ReadUInt16BigEndian(
                                        pdu.AsSpan(6 + i * 2, 2));
                        }

                        // FC16 подтверждает стартовый адрес и количество.
                        byte[] response = CreateResponse(transactionId, fc, 4);
                        pdu.AsSpan(1, 4).CopyTo(response.AsSpan(8, 4));
                        await stream.WriteAsync(response, token);
                        LogRequest(id, fc, transactionId, address, count,
                            pdu, response);
                        continue;
                    }

                    await SendExceptionAsync(stream, transactionId,
                        fc, 1, token);
                }
            }
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            // Штатное отключение PLC.
        }
        catch (EndOfStreamException)
        {
            // Клиент закрыл соединение.
        }
        catch (IOException ex)
        {
            if (!token.IsCancellationRequested)
            {
                Program.Log(
                    $"CLIENT #{id}: I/O ERROR {ex.Message}");
            }
        }
        catch (ObjectDisposedException)
            when (token.IsCancellationRequested)
        {
            // Соединение закрыто при Stop.
        }
        catch (SocketException ex)
        {
            if (!token.IsCancellationRequested)
            {
                Program.Log(
                    $"CLIENT #{id}: SOCKET ERROR {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Program.Log(
                $"CLIENT #{id}: UNEXPECTED ERROR " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _handlers.TryRemove(id, out _);

            Program.Log(
                $"CLIENT #{id} DISCONNECTED");
        }
    }

    /// <summary>
    /// Формирует Modbus TCP ADU: MBAP (7) + FC (1) + данные PDU.
    /// </summary>
    private byte[] CreateResponse(
        ushort transactionId,
        byte functionCode,
        int dataLength)
    {
        byte[] response = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt16BigEndian(
            response.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(
            response.AsSpan(4, 2), checked((ushort)(2 + dataLength)));
        response[6] = _unitId;
        response[7] = functionCode;
        return response;
    }

    private void LogRequest(
        int clientId,
        byte fc,
        ushort transactionId,
        ushort address,
        ushort count,
        byte[] pdu,
        byte[] response)
    {
        if (Volatile.Read(ref _verbose) != 1)
            return;

        Program.Log(
            $"CLIENT #{clientId} FC{fc:D2} Tx={transactionId} " +
            $"Address={address} Count={count}");
        Program.Log($"RX PDU: {Convert.ToHexString(pdu)}");
        Program.Log($"TX ADU: {Convert.ToHexString(response)}");
    }

    private async Task SendExceptionAsync(
        NetworkStream stream,
        ushort transactionId,
        byte functionCode,
        byte exceptionCode,
        CancellationToken token)
    {
        byte[] response = new byte[9];

        BinaryPrimitives.WriteUInt16BigEndian(
            response.AsSpan(0, 2),
            transactionId);

        BinaryPrimitives.WriteUInt16BigEndian(
            response.AsSpan(4, 2),
            3);

        response[6] = _unitId;
        response[7] =
            (byte)(functionCode | 0x80);
        response[8] = exceptionCode;

        await stream.WriteAsync(
            response,
            token);

        Program.Log(
            $"MODBUS EXCEPTION FC={functionCode} " +
            $"CODE={exceptionCode}");
    }

    /// <summary>
    /// Генерирует новые значения каждую секунду.
    /// </summary>
    private async Task GenerateLoopAsync(
        CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(
                TimeSpan.FromSeconds(1));

            while (await timer.WaitForNextTickAsync(token))
            {
                GenerateRegisters();
            }
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            // Штатная остановка.
        }
    }

    /// <summary>
    /// Генерирует случайные значения регистров.
    /// </summary>
    private void GenerateRegisters()
    {
        lock (_registerLock)
            GenerateRegistersLocked();
    }

    private void GenerateRegistersLocked()
    {
        // Measurme (адрес 1) изменяется только с клавиатуры.
        // Для проверки UI: готовые значения в системе 1 или 2.
        // Симулятор не задаёт физические коэффициенты SCF -> нм³.
        bool metric = _registers[1] == 2;

        float pressure = _fixedValues == 1 ? 1500f : 1300f + Random.Shared.NextSingle() * 400f;
        float opko = 1800f;

        if (metric)
        {
            pressure = _fixedValues == 1 ? 103.42f : pressure * 0.0689476f;
            opko = 124.11f;
        }

        WriteFloat(_registers, 8, pressure);
        WriteFloat(_registers, 12, opko);
        _registers[14] = (ushort)(_fixedValues == 1 ? 0 : Random.Shared.Next(0, 2));

        WriteFloat(_registers, 20, _fixedValues == 1 ? 96f : 85f + Random.Shared.NextSingle() * 20f);
        WriteFloat(_registers, 22, _fixedValues == 1 ? 85f : 75f + Random.Shared.NextSingle() * 20f);
        WriteFloat(_registers, 24, _fixedValues == 1 ? 75f : 65f + Random.Shared.NextSingle() * 20f);
        WriteFloat(_registers, 30, _fixedValues == 1 ? 1200f : Random.Shared.NextSingle() * 2000f);
        WriteFloat(_registers, 34, metric
            ? (_fixedValues == 1 ? 100f : Random.Shared.NextSingle() * 200f)
            : (_fixedValues == 1 ? 3500f : Random.Shared.NextSingle() * 7000f));
        WriteFloat(_registers, 42, metric
            ? (_fixedValues == 1 ? 40f : Random.Shared.NextSingle() * 100f)
            : (_fixedValues == 1 ? 1500f : Random.Shared.NextSingle() * 3500f));

        _generation++;
    }

    /// <summary>
    /// IEEE-754 Float32, младшее WORD, затем старшее WORD.
    /// </summary>
    private static void WriteFloat(
        ushort[] registers,
        int address,
        float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        registers[address] = (ushort)bits;
        registers[address + 1] = (ushort)(bits >> 16);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private readonly record struct RegisterInfo(
        string Name,
        int Address,
        RegisterKind Kind);

    private enum RegisterKind
    {
        Word,
        Float
    }
}
