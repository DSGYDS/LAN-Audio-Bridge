namespace LanAudioBridge.Core.Infrastructure;

/// <summary>
/// Config — 统一配置容器
///
/// ## 设计原则
/// - 不替代现有 AudioConfig，而是作为"统一视图"提供过渡期兼容
/// - 所有配置值硬编码默认值，与现有 AudioConfig 默认值对齐
/// - 后续逐步将引用从 AudioConfig 迁移到 Config.Audio / Config.Network
///
/// ## 用法
///   var cfg = Config.Default;
///   cfg.Audio.SampleRate  → 48000
///   cfg.Network.AudioPort → 12345
/// </summary>
public sealed class Config
{
    public AudioSection Audio { get; init; } = new();
    public NetworkSection Network { get; init; } = new();

    public sealed class AudioSection
    {
        public int SampleRate { get; set; } = 48000;
        public int Channels { get; set; } = 1;
        public int FrameMs { get; set; } = 20;
        public int BufferMs { get; set; } = 100;
        public int WaveOutLatency { get; set; } = 100;
        public int WasapiLatency { get; set; } = 50;
        public int BitRate { get; set; } = 128000;
        public int Complexity { get; set; } = 10;
        public bool Fec { get; set; } = true;
        public float MicBufferMultiplier { get; set; } = 2.0f;
        public float SysAudioBufferMultiplier { get; set; } = 4.0f;
    }

    public sealed class NetworkSection
    {
        public int AudioPort { get; set; } = 12345;
        public int HandshakePort { get; set; } = 12347;
        public int AudioTimeoutMs { get; set; } = 3000;
        public int MaxRetries { get; set; } = 5;
        public int RetryDelayMs { get; set; } = 1000;
    }

    public static Config Default { get; } = new();
}
