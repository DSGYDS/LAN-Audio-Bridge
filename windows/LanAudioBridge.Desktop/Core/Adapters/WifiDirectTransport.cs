using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Core.Adapters;

/// <summary>
/// WifiDirectP2pHelper — WiFi Direct P2P 建链辅助器（Windows 做客户端，连接 Android GO）
///
/// 职责：
/// 1. 显示 QR 码（含 device name + token）
/// 2. 轮询发现 Android P2P 设备（DeviceInformation.FindAllAsync）
/// 3. 连接到 Android 的 P2P Group（WiFiDirectDevice.FromIdAsync）
/// 4. 获取 P2P 适配器 IP → 发 HELLO 到 Android GO → 握手完成
///
/// 角色反转说明：
/// Android 做 GO（自带 DHCP），Windows 做客户端（自动获取 IP）。
/// 解决了 Windows Legacy GO 不提供 DHCP 导致 Android 拿不到 IP 的问题。
/// </summary>
public sealed class WifiDirectP2pHelper : IDisposable
{
    private const string Tag = "WifiDirectP2pHelper";
    private const int DiscoverIntervalMs = 3000;
    private const int DiscoverTimeoutMs = 120_000;  // 2 分钟等待用户扫码
    private const int ProgressReportIntervalMs = 5_000;

    // ── 公开属性（供 UI / QR 码使用） ──
    public string DeviceName { get; }
    public string Token { get; }
    public string? LocalIp { get; private set; }
    public string? GoIp { get; private set; }  // Android GO 的 IP（192.168.49.1）
    public bool IsConnected { get; private set; }

    /// <summary>P2P 连接就绪（已连接到 Android GO），可发起握手</summary>
    public event Action? OnConnected;

    /// <summary>进度状态变化（UI 订阅显示进度文字）</summary>
    public event Action<string>? OnStatusChanged;

    // ── 内部 ──
    private WiFiDirectDevice? _device;
    private CancellationTokenSource? _cts;

    public WifiDirectP2pHelper()
    {
        DeviceName = Environment.MachineName;
        Token = GenerateToken();
    }

    /// <summary>启动 P2P 发现循环，等待 Android 创建 Group 后连接</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_cts != null) return; // 已启动

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        try
        {
            ReportStatus("等待手机扫码... 手机扫码后将自动连接");
            Log.I(Tag, $"P2P discovery started. Device={DeviceName}, Token={Token}");

            // 轮询发现 P2P 设备
            var deviceId = await DiscoverP2pDeviceAsync(token);
            if (deviceId == null)
            {
                ReportStatus("未发现手机 P2P 设备（超时）");
                Log.W(Tag, "P2P device discovery timeout");
                return;
            }

            ReportStatus("发现手机，正在连接...");
            Log.I(Tag, $"Connecting to P2P device: {deviceId}");

            // 连接到 Android P2P Group
            _device = await WiFiDirectDevice.FromIdAsync(deviceId);
            if (_device == null || _device.ConnectionStatus != WiFiDirectConnectionStatus.Connected)
            {
                ReportStatus("P2P 连接失败");
                Log.W(Tag, "WiFiDirectDevice connection failed");
                return;
            }

            Log.I(Tag, "P2P connected to Android GO");
            ReportStatus("P2P 已连接，获取 IP...");

            // 等待 P2P 适配器获取 IP（DHCP 从 Android GO）
            var ip = await PollForP2pIpAsync(token);
            if (ip == null)
            {
                ReportStatus("P2P 适配器未获取 IP");
                Log.W(Tag, "P2P adapter IP not found");
                return;
            }

            LocalIp = ip;
            GoIp = "192.168.49.1";  // Android GO 固定 IP
            IsConnected = true;
            ReportStatus($"P2P 就绪 ✓ local={ip} go={GoIp}");
            Log.I(Tag, $"P2P ready: local={ip}, GO={GoIp}");

            OnConnected?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ReportStatus("已取消");
            Log.I(Tag, "StartAsync cancelled");
        }
        catch (Exception ex)
        {
            ReportStatus($"错误：{ex.Message}");
            Log.E(Tag, $"StartAsync error: {ex}");
        }
    }

    /// <summary>停止 P2P</summary>
    public Task StopAsync()
    {
        _cts?.Cancel();
        _device?.Dispose();
        _device = null;
        IsConnected = false;
        LocalIp = null;
        GoIp = null;
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    // ── 内部方法 ──

    /// <summary>轮询发现 P2P 设备（3s 间隔，最多 2 分钟）</summary>
    private async Task<string?> DiscoverP2pDeviceAsync(CancellationToken ct)
    {
        int elapsed = 0;
        int lastReport = 0;
        var selector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);

        while (elapsed < DiscoverTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var devices = await DeviceInformation.FindAllAsync(selector);
                if (devices.Count > 0)
                {
                    Log.I(Tag, $"Found {devices.Count} P2P device(s): {string.Join(", ", devices.Select(d => d.Name))}");
                    return devices[0].Id;
                }
            }
            catch (Exception ex)
            {
                Log.W(Tag, $"P2P discovery error: {ex.Message}");
            }

            await Task.Delay(DiscoverIntervalMs, ct);
            elapsed += DiscoverIntervalMs;

            if (elapsed - lastReport >= ProgressReportIntervalMs)
            {
                lastReport = elapsed;
                ReportStatus($"等待手机创建 P2P... {elapsed / 1000}s");
            }
        }
        return null;
    }

    /// <summary>轮询 P2P 适配器 IP（500ms 间隔，最多 15s）</summary>
    private async Task<string?> PollForP2pIpAsync(CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ip = GetP2pAdapterIp();
            if (ip != null) return ip;
            await Task.Delay(500, ct);
        }
        return null;
    }

    /// <summary>枚举网络适配器，查找 Wi-Fi Direct / P2P 适配器的 IPv4 地址</summary>
    private static string? GetP2pAdapterIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            var desc = ni.Description;
            if (!desc.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)
                && !desc.Contains("P2P", StringComparison.OrdinalIgnoreCase)
                && !desc.Contains("Microsoft Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ni.OperationalStatus != OperationalStatus.Up) continue;

            foreach (var ip in ni.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork
                    && !ip.Address.ToString().StartsWith("169.254"))
                    return ip.Address.ToString();
            }
        }
        return null;
    }

    /// <summary>生成 8 字节 ASCII token（十六进制字符串）</summary>
    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes); // 8 字符
    }

    private void ReportStatus(string msg)
    {
        Log.I(Tag, msg);
        OnStatusChanged?.Invoke(msg);
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
