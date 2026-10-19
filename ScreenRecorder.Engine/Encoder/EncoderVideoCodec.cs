using MediaEncoder;

namespace ScreenRecorder.Encoder
{
    public class EncoderVideoCodec : EncoderCodec
    {
        private VideoCodec _videoCodec;

        public VideoCodec VideoCodec
        {
            get => _videoCodec;
            set => SetProperty(ref _videoCodec, value);
        }

        private bool _isSupported = true;

        public bool IsSupported
        {
            get => _isSupported;
            set => SetProperty(ref _isSupported, value);
        }

        public EncoderVideoCodec(VideoCodec videoCodec, string name, bool isSupported = true)
        {
            _videoCodec = videoCodec;
            Name = name;
            _isSupported = isSupported;
        }
    }
}
