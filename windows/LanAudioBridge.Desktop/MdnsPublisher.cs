using System;
using System.Threading;
using Makaretu.Dns;
using LanAudioBridge.Core.Infrastructure;

namespace LanAudioBridge.Desktop;

/// <summary>
/// mDNS 服务发布 — 启动时注册 _lan-audio._udp 服务
/// 关闭时注销，供 Android NsdManager 自动发现
/// </summary>
public sealed class MdnsPublisher : IDisposable
{
    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _sd;
    private readonly ServiceProfile _profile;
    private bool _running;
    private readonly object _lock = new();

    /// <param name="hostname">电脑名称，Android 端以此识别设备</param>
    /// <param name="port">音频数据端口，与 AudioEngine.Port 一致</param>
    public MdnsPublisher(string hostname, int port)
    {
        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);
        _profile = new ServiceProfile(hostname, "_lan-audio._udp", (ushort)port);
    }

    /// <summary>工厂方法，自动使用当前机器名</summary>
    public static MdnsPublisher Create(string hostname, int port) => new(hostname, port);

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
        }
        try
        {
            _mdns.Start();
            _sd.Advertise(_profile);
        }
        catch (Exception ex)
        {
            Log.E("MdnsPublisher", $"Start failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
        }
        try
        {
            _sd.Unadvertise(_profile);
            _mdns.Stop();
        }
        catch (Exception ex)
        {
            Log.E("MdnsPublisher", $"Stop failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        try
        {
            _sd.Dispose();
            _mdns.Dispose();
        }
        catch (Exception ex)
        {
            Log.E("MdnsPublisher", $"Dispose failed: {ex.Message}");
        }
        GC.SuppressFinalize(this);
    }
}
