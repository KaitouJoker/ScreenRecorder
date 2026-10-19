using System.Collections.Generic;
using System.Windows;
using Res = ScreenRecorder.Properties.Resources;

namespace ScreenRecorder
{
    public sealed class SettingComboItem<T>
    {
        public string Name { get; set; }
        public T Value { get; set; }

        public SettingComboItem(string name, T value)
        {
            Name = name;
            Value = value;
        }
    }

    public partial class EncoderSettingsWindow : Window
    {
        public List<SettingComboItem<string>> RateControlModes { get; }
        public List<SettingComboItem<int>> BitrateOptions { get; }
        public List<SettingComboItem<int>> CqpOptions { get; }
        public List<SettingComboItem<int>> KeyframeIntervals { get; }
        public List<SettingComboItem<string>> Presets { get; }
        public List<SettingComboItem<string>> Tunings { get; }
        public List<SettingComboItem<string>> MultipassModes { get; }
        public List<SettingComboItem<string>> Profiles { get; }
        public List<SettingComboItem<int>> BFrameCounts { get; }
        public List<SettingComboItem<string>> BFrameRefModes { get; }

        public EncoderSettingsWindow()
        {
            RateControlModes = new List<SettingComboItem<string>>
            {
                new SettingComboItem<string>(Res.RateControl_CBR, "CBR"),
                new SettingComboItem<string>(Res.RateControl_VBR, "VBR"),
                new SettingComboItem<string>(Res.RateControl_CQP, "CQP"),
            };

            BitrateOptions = new List<SettingComboItem<int>>
            {
                new SettingComboItem<int>("2,500 Kbps (2.5 Mbps)", 2500000),
                new SettingComboItem<int>("5,000 Kbps (5 Mbps)", 5000000),
                new SettingComboItem<int>("8,000 Kbps (8 Mbps)", 8000000),
                new SettingComboItem<int>("10,000 Kbps (10 Mbps)", 10000000),
                new SettingComboItem<int>("15,000 Kbps (15 Mbps)", 15000000),
                new SettingComboItem<int>("20,000 Kbps (20 Mbps)", 20000000),
                new SettingComboItem<int>("30,000 Kbps (30 Mbps)", 30000000),
                new SettingComboItem<int>("50,000 Kbps (50 Mbps)", 50000000),
            };

            CqpOptions = new List<SettingComboItem<int>>
            {
                new SettingComboItem<int>("14 (초고화질)", 14),
                new SettingComboItem<int>("18 (매우 높음)", 18),
                new SettingComboItem<int>("20 (권장 기본값)", 20),
                new SettingComboItem<int>("23 (좋음)", 23),
                new SettingComboItem<int>("26 (보통)", 26),
                new SettingComboItem<int>("28 (낮음)", 28),
                new SettingComboItem<int>("32 (저용량)", 32),
            };

            KeyframeIntervals = new List<SettingComboItem<int>>
            {
                new SettingComboItem<int>(Res.KeyframeIntervalSec, 0),
                new SettingComboItem<int>("1초", 1),
                new SettingComboItem<int>("2초 (OBS 권장)", 2),
                new SettingComboItem<int>("3초", 3),
                new SettingComboItem<int>("5초", 5),
                new SettingComboItem<int>("10초", 10),
            };

            Presets = new List<SettingComboItem<string>>
            {
                new SettingComboItem<string>(Res.Preset_P1, "p1"),
                new SettingComboItem<string>(Res.Preset_P2, "p2"),
                new SettingComboItem<string>(Res.Preset_P3, "p3"),
                new SettingComboItem<string>(Res.Preset_P4, "p4"),
                new SettingComboItem<string>(Res.Preset_P5, "p5"),
                new SettingComboItem<string>(Res.Preset_P6, "p6"),
                new SettingComboItem<string>(Res.Preset_P7, "p7"),
            };

            Tunings = new List<SettingComboItem<string>>
            {
                new SettingComboItem<string>(Res.Tuning_HQ, "hq"),
                new SettingComboItem<string>(Res.Tuning_LL, "ll"),
                new SettingComboItem<string>(Res.Tuning_ULL, "ull"),
                new SettingComboItem<string>(Res.Tuning_UHQ, "uhq"),
            };

            MultipassModes = new List<SettingComboItem<string>>
            {
                new SettingComboItem<string>(Res.Multipass_Disabled, "disabled"),
                new SettingComboItem<string>(Res.Multipass_Qres, "qres"),
                new SettingComboItem<string>(Res.Multipass_Fullres, "fullres"),
            };

            Profiles = new List<SettingComboItem<string>>
            {
                new SettingComboItem<string>("main (기본 / AV1)", "main"),
                new SettingComboItem<string>("high", "high"),
                new SettingComboItem<string>("baseline", "baseline"),
            };

            BFrameCounts = new List<SettingComboItem<int>>
            {
                new SettingComboItem<int>("0", 0),
                new SettingComboItem<int>("1", 1),
                new SettingComboItem<int>("2", 2),
                new SettingComboItem<int>("3", 3),
                new SettingComboItem<int>("4", 4),
            };

            BFrameRefModes = new List<SettingComboItem<string>>
            {
                new SettingComboItem<string>(Res.BFrameRefMode_Disabled, "disabled"),
                new SettingComboItem<string>(Res.BFrameRefMode_Each, "each"),
                new SettingComboItem<string>(Res.BFrameRefMode_Middle, "middle"),
            };

            InitializeComponent();
            DataContext = this;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
