namespace ClassShout.Core.Audio;

/// <summary>从 16 位 PCM 计算音量电平。教室端画波形、教师端画录音波形都用它。</summary>
public static class PcmLevel
{
    /// <summary>
    /// 返回 0~1 的电平。
    ///
    /// 为什么要乘增益：人声的 RMS 通常只占满量程的一小部分，
    /// 直接线性映射的话波形会一直贴着底部，看不出说话与安静的区别。
    /// </summary>
    public static double Compute(ReadOnlySpan<byte> pcm, double gain = 3.0)
    {
        if (pcm.Length < 2)
        {
            return 0;
        }

        var sampleCount = pcm.Length / 2;
        double sumOfSquares = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            // 小端 16 位有符号
            var sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            sumOfSquares += (double)sample * sample;
        }

        var rms = Math.Sqrt(sumOfSquares / sampleCount) / 32768d;
        return Math.Clamp(rms * gain, 0d, 1d);
    }
}
