using System.Media;

namespace Bcode.Chat;

/// <summary>Âm báo tự tổng hợp (sóng sin / vuông, không có file âm thanh nào đi kèm): tin mới, bạn vừa online ("cộc cộc"), BUZZ (rè rè), gửi đi.
/// Phát bằng SoundPlayer từ WAV trong bộ nhớ — chạy cả khi cửa sổ đang ẩn dưới khay.</summary>
internal static class Sounds
{
    private const int Rate = 22050;
    private static readonly Dictionary<string, byte[]> Cache = new();
    private static SoundPlayer? _player;
    private static long _lastAt;
    public static bool Enabled { get; set; } = true;

    public static void Play(string kind)
    {
        if (!Enabled) return;
        var now = Environment.TickCount64;
        if (now - _lastAt < 150) return;   // nhiều tin dồn một lúc: không kêu chồng
        _lastAt = now;
        try
        {
            byte[] wav;
            lock (Cache)
                if (!Cache.TryGetValue(kind, out wav!)) Cache[kind] = wav = Build(kind);
            _player?.Stop();
            _player = new SoundPlayer(new MemoryStream(wav));
            _player.Play();
        }
        catch { /* máy không có thiết bị âm thanh */ }
    }

    private static byte[] Build(string kind) => kind switch
    {
        // "ding-dong" 2 nốt trong trẻo
        "msg" => Wav(Tone(1318.5, 0.11, 0.55, decay: 18) .Concat(Tone(987.8, 0.22, 0.5, decay: 9)).ToArray()),
        // tiếng gõ cửa: 2 tiếng "cộc" trầm ngắn
        "online" => Wav(Knock().Concat(Silence(0.09)).Concat(Knock()).ToArray()),
        // BUZZ: sóng vuông 90 Hz rung theo nhịp 12 Hz, ~0,7 s
        "buzz" => Wav(Buzz(0.7)),
        // gửi đi: "píp" ngắn
        "send" => Wav(Tone(880, 0.05, 0.25, decay: 30)),
        // file / thông báo: 3 nốt đi lên
        _ => Wav(Tone(784, 0.08, 0.4, 20).Concat(Tone(988, 0.08, 0.4, 20)).Concat(Tone(1175, 0.14, 0.4, 12)).ToArray()),
    };

    private static float[] Tone(double hz, double sec, double vol, double decay)
    {
        var n = (int)(Rate * sec);
        var s = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var attack = Math.Min(1, i / (Rate * 0.004));
            s[i] = (float)(vol * attack * Math.Exp(-decay * t) * (Math.Sin(2 * Math.PI * hz * t) + 0.25 * Math.Sin(4 * Math.PI * hz * t)));
        }
        return s;
    }

    private static float[] Knock()
    {
        var n = (int)(Rate * 0.07);
        var s = new float[n];
        var rnd = new Random(3);
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var env = Math.Exp(-60 * t);
            s[i] = (float)(env * (0.8 * Math.Sin(2 * Math.PI * 180 * t) + 0.35 * Math.Sin(2 * Math.PI * 95 * t) + 0.15 * (rnd.NextDouble() * 2 - 1)));
        }
        return s;
    }

    private static float[] Buzz(double sec)
    {
        var n = (int)(Rate * sec);
        var s = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var sq = Math.Sign(Math.Sin(2 * Math.PI * 90 * t)) * 0.6 + Math.Sign(Math.Sin(2 * Math.PI * 135 * t)) * 0.25;
            var trem = 0.55 + 0.45 * Math.Sign(Math.Sin(2 * Math.PI * 12 * t));
            var fade = Math.Min(1, Math.Min(t / 0.01, (sec - t) / 0.05));
            s[i] = (float)(0.35 * sq * trem * fade);
        }
        return s;
    }

    private static float[] Silence(double sec) => new float[(int)(Rate * sec)];

    private static byte[] Wav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataLen = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var f in samples) w.Write((short)Math.Clamp(f * 32767, -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }
}
