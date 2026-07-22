using System.IO;
using Avalonia.Media.Imaging;
using QRCoder;

namespace LanAudioBridge.Desktop;

/// <summary>
/// QrCodeHelper — QR 码生成工具
///
/// 使用 QRCoder 生成 PNG 字节流，转为 Avalonia Bitmap 供 Image 控件显示。
/// </summary>
public static class QrCodeHelper
{
    /// <summary>
    /// 生成 QR 码 Bitmap
    /// </summary>
    /// <param name="content">QR 码内容（如 LABRIDGE://...）</param>
    /// <param name="pixelsPerModule">每个模块的像素大小（默认 8）</param>
    public static Bitmap Generate(string content, int pixelsPerModule = 8)
    {
        using var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var bytes = new BitmapByteQRCode(data).GetGraphic(pixelsPerModule);
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }

    /// <summary>
    /// 构建 LABRIDGE:// QR 码文本（WiFi Direct P2P 模式）
    /// </summary>
    /// <param name="deviceName">Windows 设备名</param>
    /// <param name="token">认证 token</param>
    public static string BuildQrPayload(string deviceName, string token)
    {
        return $"LABRIDGE://version=1&transport=wifidirect&device={deviceName}&token={token}";
    }
}
