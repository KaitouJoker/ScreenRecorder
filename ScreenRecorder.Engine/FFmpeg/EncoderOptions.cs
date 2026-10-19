namespace MediaEncoder
{
    /// <summary>Hardware acceleration backend for the video encoder.</summary>
    public enum HwAccel
    {
        Nvenc,
        Qsv,
        Software,
    }

    /// <summary>Rate-control mode.</summary>
    public enum RateControl
    {
        Cbr,
        Vbr,

        /// <summary>
        /// Constant-quality: bitrate follows content complexity (static screens become nearly
        /// free). Maps to CRF on libx264/libx265, CQ (rc=vbr) on NVENC and ICQ on QSV; the
        /// quality value is on the 0–51 QP-ish scale (lower = better, ~18 high / 23 medium / 28 low).
        /// </summary>
        Cq,

        /// <summary>Constant QP (CQP): fixed quantization parameter (OBS CQP mode).</summary>
        Cqp,
    }

    /// <summary>Detailed encoding parameters modeled after OBS Studio NVENC encoder.</summary>
    public sealed class NvencOptions
    {
        public RateControl RateControl { get; set; } = RateControl.Cbr;
        public int Cqp { get; set; } = 20;
        public int BitrateBps { get; set; } = 10000000;
        public int MaxBitrateBps { get; set; } = 10000000;
        public int KeyframeIntervalSec { get; set; } = 0; // 0 = auto
        public string Preset { get; set; } = "p4";
        public string Tuning { get; set; } = "hq";
        public string Multipass { get; set; } = "qres";
        public string Profile { get; set; } = "main";
        public bool LookAhead { get; set; } = false;
        public bool AdaptiveQuantization { get; set; } = true;
        public int BFrames { get; set; } = 0;
        public string BFrameRefMode { get; set; } = "disabled";
    }
}
