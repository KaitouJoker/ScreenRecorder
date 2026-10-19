using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ScreenRecorder.DirectX;
using ScreenRecorder.Encoder;

namespace ScreenRecorder
{
    public sealed class AppManager : ObservableObject, IDisposable
    {
        #region Constructors

        private static volatile AppManager _instance;
        private static object _syncRoot = new object();

        public static AppManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_syncRoot)
                    {
                        if (_instance == null)
                        {
                            _instance = new AppManager();
                        }
                    }
                }

                return _instance;
            }
        }

        private AppManager() { }

        #endregion

        #region Bindable Properties

        private bool _isInitialized = false;

        public bool IsInitialized
        {
            get => _isInitialized;
            private set => SetProperty(ref _isInitialized, value);
        }

        private ScreenEncoder _screenEncoder;

        public ScreenEncoder ScreenEncoder
        {
            get => _screenEncoder;
            private set => SetProperty(ref _screenEncoder, value);
        }

        private string _encodeTime;

        public string EncodeTime
        {
            get => _encodeTime;
            private set => SetProperty(ref _encodeTime, value);
        }

        private int _recordCountdown;

        /// <summary>
        /// Seconds remaining in the pre-record countdown (#56); 0 when no countdown is active.
        /// While non-zero the elapsed-time display shows the countdown instead.
        /// </summary>
        public int RecordCountdown
        {
            get => _recordCountdown;
            set
            {
                if (SetProperty(ref _recordCountdown, value))
                {
                    OnPropertyChanged(nameof(IsRecordCountdown));
                }
            }
        }

        /// <summary>True while the pre-record countdown runs (enables the stop button to cancel it).</summary>
        public bool IsRecordCountdown => _recordCountdown > 0;

        private RecordQualityModeItem[] _recordQualityModes;

        public RecordQualityModeItem[] RecordQualityModes
        {
            get => _recordQualityModes;
            private set => SetProperty(ref _recordQualityModes, value);
        }

        private EncoderFormat[] _encoderFormats;

        public EncoderFormat[] EncoderFormats
        {
            get => _encoderFormats;
            private set => SetProperty(ref _encoderFormats, value);
        }

        private EncoderVideoCodec[] _encoderVideoCodecs;

        public EncoderVideoCodec[] EncoderVideoCodecs
        {
            get => _encoderVideoCodecs;
            private set => SetProperty(ref _encoderVideoCodecs, value);
        }

        private EncoderAudioCodec[] _encoderAudioCodecs;

        public EncoderAudioCodec[] EncoderAudioCodecs
        {
            get => _encoderAudioCodecs;
            private set => SetProperty(ref _encoderAudioCodecs, value);
        }

        private ICaptureTarget[] _captureTargets;

        public ICaptureTarget[] CaptureTargets
        {
            get => _captureTargets;
            private set => SetProperty(ref _captureTargets, value);
        }

        private bool _notSupportedHwH264 = true;

        public bool NotSupportedHwH264
        {
            get => _notSupportedHwH264;
            private set => SetProperty(ref _notSupportedHwH264, value);
        }

        private bool _notSupportedHwHevc = true;

        public bool NotSupportedHwHevc
        {
            get => _notSupportedHwHevc;
            private set => SetProperty(ref _notSupportedHwHevc, value);
        }

        private bool _notSupportedHwAV1 = true;

        public bool NotSupportedHwAV1
        {
            get => _notSupportedHwAV1;
            private set => SetProperty(ref _notSupportedHwAV1, value);
        }

        private string _toastMessage;

        public string ToastMessage
        {
            get => _toastMessage;
            set => SetProperty(ref _toastMessage, value);
        }

        private bool _isToastVisible;

        public bool IsToastVisible
        {
            get => _isToastVisible;
            set => SetProperty(ref _isToastVisible, value);
        }

        #endregion

        #region Helpers

        public void Initialize()
        {
            if (IsInitialized)
                return;

            ScreenEncoder = new ScreenEncoder();
            EncoderFormats = new EncoderFormat[]
            {
                // recording
                EncoderFormat.CreateEncoderFormatByFormatString("mp4"),
                EncoderFormat.CreateEncoderFormatByFormatString("avi"),
                EncoderFormat.CreateEncoderFormatByFormatString("matroska"),
                EncoderFormat.CreateEncoderFormatByFormatString("mpegts"),
                EncoderFormat.CreateEncoderFormatByFormatString("mov"),
            };
            EncoderVideoCodecs = new EncoderVideoCodec[]
            {
                new EncoderVideoCodec(MediaEncoder.VideoCodec.H264, "H.264"),
                new EncoderVideoCodec(MediaEncoder.VideoCodec.H265, "H.265 (HEVC)"),
                new EncoderVideoCodec(MediaEncoder.VideoCodec.AV1, "AV1"),
            };
            EncoderAudioCodecs = new EncoderAudioCodec[]
            {
                new EncoderAudioCodec(MediaEncoder.AudioCodec.None, "No Audio"),
                new EncoderAudioCodec(MediaEncoder.AudioCodec.Aac, "AAC (Advanced Audio Coding)"),
                new EncoderAudioCodec(MediaEncoder.AudioCodec.Mp3, "MP3 (MPEG audio layer 3)"),
            };
            RecordQualityModes = new RecordQualityModeItem[]
            {
                new RecordQualityModeItem(RecordQualityMode.Bitrate, Properties.Resources.QualityModeBitrate),
                new RecordQualityModeItem(RecordQualityMode.High, Properties.Resources.QualityModeHigh),
                new RecordQualityModeItem(RecordQualityMode.Medium, Properties.Resources.QualityModeMedium),
                new RecordQualityModeItem(RecordQualityMode.Low, Properties.Resources.QualityModeLow),
            };

            CaptureTargets = new ICaptureTarget[]
            {
                CaptureTarget.ByUserChoiceCaptureTarget,
                CaptureTarget.PrimaryDisplay,
            }.Concat(MonitorInfo.GetActiveMonitorInfos()).ToArray();

            CheckHardwareCodec();

            // Keep the engine's frame-rate provider in sync with config (advanced fps, else 60),
            // replacing the legacy VideoClockEvent.Framerate wiring.
            UpdateFramerate();
            AppConfig.Instance.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppConfig.SelectedRecordFrameRate) ||
                    e.PropertyName == nameof(AppConfig.AdvancedSettings))
                {
                    UpdateFramerate();
                }
            };

            CompositionTarget.Rendering += CompositionTarget_Rendering;

            IsInitialized = true;
        }

        private System.Threading.CancellationTokenSource _toastCts;

        public void ShowToast(string message, int durationMs = 5000)
        {
            // 스텔스 모드에서는 anti-layerd-window 탐지를 회피하기 위해 팝업(WS_EX_LAYERED)을 화면에 띄우지 않음
            if (AppConfig.Instance.EnableStealthMode)
            {
                System.Diagnostics.Debug.WriteLine($"[StealthMode Toast Suppressed]: {message}");
                return;
            }

            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                _toastCts?.Cancel();
                _toastCts = new System.Threading.CancellationTokenSource();
                var token = _toastCts.Token;

                ToastMessage = message;
                IsToastVisible = true;

                Task.Delay(durationMs, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled)
                    {
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            IsToastVisible = false;
                        });
                    }
                }, TaskScheduler.Default);
            });
        }

        public void DismissToast()
        {
            _toastCts?.Cancel();
            IsToastVisible = false;
        }

        private async void CheckHardwareCodec()
        {
            await Task.Run(() =>
            {
                MediaEncoder.MediaWriter.CheckHardwareCodec();
                bool supportedNvencH264 = MediaEncoder.MediaWriter.IsSupportedNvencH264();
                bool supportedQsvH264 = MediaEncoder.MediaWriter.IsSupportedQsvH264();
                bool supportedNvencHevc = MediaEncoder.MediaWriter.IsSupportedNvencHEVC();
                bool supportedQsvHevc = MediaEncoder.MediaWriter.IsSupportedQsvHEVC();
                bool supportedNvencAV1 = MediaEncoder.MediaWriter.IsSupportedNvencAV1();

                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    NotSupportedHwH264 = !supportedNvencH264 && !supportedQsvH264;
                    NotSupportedHwHevc = !supportedNvencHevc && !supportedQsvHevc;
                    NotSupportedHwAV1 = !supportedNvencAV1;

                    if (EncoderVideoCodecs != null)
                    {
                        foreach (var codec in EncoderVideoCodecs)
                        {
                            if (codec.VideoCodec == MediaEncoder.VideoCodec.AV1)
                            {
                                codec.IsSupported = supportedNvencAV1;
                            }
                        }
                    }

                    // If hardware does not support AV1 but config had AV1 selected from previous session,
                    // reset to H.264 default and notify the user with a toast message.
                    if (!supportedNvencAV1 && AppConfig.Instance.SelectedRecordVideoCodec == MediaEncoder.VideoCodec.AV1)
                    {
                        AppConfig.Instance.SelectedRecordVideoCodec = MediaEncoder.VideoCodec.H264;
                        ShowToast(Properties.Resources.Av1NotSupportedResetToH264);
                    }
                });
            });
        }

        private static void UpdateFramerate()
        {
            ScreenRecorder.Encoder.FrameRateProvider.Framerate = AppConfig.Instance.AdvancedSettings
                ? AppConfig.Instance.SelectedRecordFrameRate
                : 60;
        }

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            // Read the live frame count each render (~60fps) so the elapsed-time display ticks
            // smoothly, instead of sampling a value that a background poll only refreshed at 10Hz.
            // During the pre-record countdown the same display shows the remaining seconds.
            EncodeTime = _recordCountdown > 0
                ? $"00:00:{_recordCountdown:00}"
                : Utils.VideoFramesCountToStringTime(_screenEncoder.LiveVideoFrames);
        }

        public void Dispose()
        {
            if (!IsInitialized)
                return;

            _screenEncoder?.Dispose();
            _screenEncoder = null;

            CompositionTarget.Rendering -= CompositionTarget_Rendering;

            IsInitialized = false;
        }

        #endregion
    }
}
