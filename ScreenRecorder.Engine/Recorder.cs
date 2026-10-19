using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using MediaEncoder;

namespace ScreenRecorder.Encoder
{
    /// <summary>Video stream parameters for a <see cref="Recorder"/>.</summary>
    public sealed class VideoParams
    {
        public VideoCodec Codec;
        public HwAccel Hw;
        public int Width;
        public int Height;
        public int FpsNumerator;
        public int FpsDenominator = 1;
        public int Bitrate;
        public RateControl RateControl = RateControl.Cbr;

        /// <summary>Constant-quality value for <see cref="RateControl.Cq"/> (0–51, lower = better).</summary>
        public int Quality = 23;

        /// <summary>OBS-based detailed NVENC options.</summary>
        public NvencOptions NvencOptions;
    }

    /// <summary>Audio stream parameters for a <see cref="Recorder"/> (null on the recorder = no audio).</summary>
    public sealed class AudioParams
    {
        public AudioCodec Codec;
        public int SampleRate;
        public int Channels;
        public int Bitrate;
        public SampleFormat Format;
    }

    /// <summary>
    /// File recorder: a video-encode thread and (optional) audio-encode thread feed encoded
    /// packets into one queue that a mux thread drains to the container, ordered by PTS/DTS.
    /// Producers call <see cref="PushVideoFrame"/>/<see cref="PushAudioFrame"/>; the raw data is
    /// copied into a pool on push, so producers may reuse their buffers immediately. CFR: video
    /// PTS is the ideal-grid frame index derived from each frame's QPC timestamp and a single t0.
    /// </summary>
    public sealed unsafe class Recorder : IDisposable
    {
        private readonly struct QueuedPacket
        {
            public readonly bool IsVideo;
            public readonly long Pts;
            public readonly long Dts;
            public readonly nint Data;
            public readonly int Size;
            public readonly bool IsKey;

            public QueuedPacket(bool isVideo, long pts, long dts, nint data, int size, bool isKey)
            {
                IsVideo = isVideo;
                Pts = pts;
                Dts = dts;
                Data = data;
                Size = size;
                IsKey = isKey;
            }
        }

        private readonly string _url;
        private readonly string _format;
        private readonly VideoParams _videoParams;
        private readonly AudioParams _audioParams;

        private readonly BlockingCollection<RawVideoFrame> _videoIn = new BlockingCollection<RawVideoFrame>();
        private readonly BlockingCollection<RawAudioFrame> _audioIn = new BlockingCollection<RawAudioFrame>();
        private readonly BlockingCollection<QueuedPacket> _encodedOut = new BlockingCollection<QueuedPacket>();

        private readonly UnmanagedBufferPool _videoFramePool = new UnmanagedBufferPool();
        private readonly UnmanagedBufferPool _audioFramePool = new UnmanagedBufferPool();
        private readonly UnmanagedBufferPool _packetPool = new UnmanagedBufferPool();

        private FFmpegVideoEncoder _videoEncoder;
        private FFmpegAudioEncoder _audioEncoder;
        private FFmpegFileContainer _container;

        private Thread _videoThread;
        private Thread _audioThread;
        private Thread _muxThread;

        private readonly ManualResetEventSlim _vStartEvent = new ManualResetEventSlim(false);
        private long _vStartTs = long.MinValue;

        private long _droppedFrames;
        private long _recordedVideoFrames;
        private bool _started;
        private bool _disposed;

        public Recorder(string url, string formatShortName, VideoParams video, AudioParams audio)
        {
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _format = formatShortName ?? throw new ArgumentNullException(nameof(formatShortName));
            _videoParams = video ?? throw new ArgumentNullException(nameof(video));
            _audioParams = audio;
        }

        public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

        /// <summary>Video frames accepted so far (drives the recording-time display). Advances at fps.</summary>
        public long RecordedVideoFrames => Interlocked.Read(ref _recordedVideoFrames);

        public void Start()
        {
            if (_started)
                return;
            _started = true;

            _container = new FFmpegFileContainer(_format);

            _videoEncoder = new FFmpegVideoEncoder(
                _videoParams.Codec, _videoParams.Hw, _videoParams.Width, _videoParams.Height,
                _videoParams.FpsNumerator, _videoParams.FpsDenominator, _videoParams.Bitrate,
                _videoParams.RateControl, _container.GlobalHeader, quality: _videoParams.Quality,
                nvencOptions: _videoParams.NvencOptions);

            if (_audioParams != null)
            {
                _audioEncoder = new FFmpegAudioEncoder(
                    _audioParams.Codec, _audioParams.Channels, _audioParams.SampleRate,
                    _audioParams.Format, _audioParams.Bitrate, _container.GlobalHeader);
            }

            _container.Initialize(_url, _videoEncoder, _audioEncoder);

            _videoThread = new Thread(VideoLoop) { Name = "RecorderVideo", IsBackground = true };
            _videoThread.Start();

            if (_audioEncoder != null)
            {
                _audioThread = new Thread(AudioLoop) { Name = "RecorderAudio", IsBackground = true };
                _audioThread.Start();
            }

            _muxThread = new Thread(MuxLoop) { Name = "RecorderMux", IsBackground = true };
            _muxThread.Start();
        }

        public void PushVideoFrame(in RawVideoFrame frame)
        {
            if (!_started || _videoIn.IsAddingCompleted)
                return;

            // Live-capture backpressure: if encoder is lagging behind by more than 10 frames (~160ms),
            // drop the incoming frame to prevent memory ballooning and queue stalls.
            if (_videoIn.Count >= 10)
            {
                Interlocked.Increment(ref _droppedFrames);
                return;
            }

            int frameBytes = frame.Stride * frame.Height + frame.Stride * (frame.Height / 2); // NV12
            nint buffer = _videoFramePool.Rent(frameBytes);
            Unsafe.CopyBlockUnaligned((void*)buffer, (void*)frame.Data, (uint)frameBytes);

            _videoIn.Add(new RawVideoFrame(buffer, frame.Stride, frame.Width, frame.Height, frame.Format, frame.TimestampQpc));
            Interlocked.Increment(ref _recordedVideoFrames);
        }

        public void PushAudioFrame(in RawAudioFrame frame)
        {
            if (!_started || _audioEncoder == null || _audioIn.IsAddingCompleted)
                return;

            int bytesPerSample = frame.Format == SampleFormat.S16 ? 2 : frame.Format == SampleFormat.S32 || frame.Format == SampleFormat.FLT ? 4 : 1;
            int frameBytes = frame.Samples * frame.Channels * bytesPerSample;
            nint buffer = _audioFramePool.Rent(frameBytes);
            Unsafe.CopyBlockUnaligned((void*)buffer, (void*)frame.Data, (uint)frameBytes);

            _audioIn.Add(new RawAudioFrame(buffer, frame.Samples, frame.SampleRate, frame.Channels, frame.Format, frame.TimestampQpc));
        }

        private void VideoLoop()
        {
            try
            {
                long fpsNum = _videoParams.FpsNumerator;
                long fpsDen = _videoParams.FpsDenominator;
                long freq = Stopwatch.Frequency;

                byte** planes = stackalloc byte*[2];
                int* linesizes = stackalloc int[2];

                foreach (var frame in _videoIn.GetConsumingEnumerable())
                {
                    if (_vStartTs == long.MinValue)
                    {
                        _vStartTs = frame.TimestampQpc;
                        _vStartEvent.Set();
                    }

                    long gridIndex = (long)Math.Round((double)(frame.TimestampQpc - _vStartTs) * fpsNum / (fpsDen * freq));

                    planes[0] = (byte*)frame.Data;
                    planes[1] = (byte*)(frame.Data + (nint)frame.Stride * frame.Height);
                    linesizes[0] = frame.Stride;
                    linesizes[1] = frame.Stride;

                    _videoEncoder.SendFrame(planes, linesizes, 2, gridIndex);
                    DrainVideo();

                    _videoFramePool.Return(frame.Data);
                }

                _videoEncoder.FlushSend();
                DrainVideo();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[VideoLoop EXCEPTION] {ex}");
            }
        }

        private void DrainVideo()
        {
            EncodedVideoPacket? packet;
            while ((packet = _videoEncoder.ReceivePacket()) != null)
            {
                var p = packet.Value;
                nint buffer = _packetPool.Rent(p.Size);
                Unsafe.CopyBlockUnaligned((void*)buffer, (void*)p.Data, (uint)p.Size);
                _encodedOut.Add(new QueuedPacket(true, p.PresentationTimeStamp, p.DecodingTimeStamp, buffer, p.Size, p.IsKeyFrame));
            }
        }

        private void AudioLoop()
        {
            try
            {
                byte** planes = stackalloc byte*[1];
                long freq = Stopwatch.Frequency;
                bool audioStarted = false;

                // Wait until video has captured its first frame and established _vStartTs
                while (_vStartTs == long.MinValue && !_videoIn.IsAddingCompleted && !_audioIn.IsAddingCompleted)
                {
                    _vStartEvent.Wait(10);
                }

                long vStart = _vStartTs;

                foreach (var frame in _audioIn.GetConsumingEnumerable())
                {
                    if (vStart != long.MinValue && !audioStarted)
                    {
                        long frameDurTicks = (long)frame.Samples * freq / frame.SampleRate;
                        long frameEndTicks = frame.TimestampQpc + frameDurTicks;

                        // Entire audio block occurred before video started: discard.
                        if (frameEndTicks <= vStart)
                        {
                            _audioFramePool.Return(frame.Data);
                            continue;
                        }

                        // Audio block straddles vStart: trim the pre-video portion.
                        if (frame.TimestampQpc < vStart)
                        {
                            long skipTicks = vStart - frame.TimestampQpc;
                            int skipSamples = (int)Math.Round((double)skipTicks * frame.SampleRate / freq);
                            skipSamples = Math.Clamp(skipSamples, 0, frame.Samples - 1);
                            int keepSamples = frame.Samples - skipSamples;

                            int bytesPerSample = frame.Format == SampleFormat.S16 ? 2 : frame.Format == SampleFormat.S32 || frame.Format == SampleFormat.FLT ? 4 : 1;
                            int offsetBytes = skipSamples * frame.Channels * bytesPerSample;

                            planes[0] = (byte*)(frame.Data + offsetBytes);
                            _audioEncoder.Accept(planes, 1, keepSamples);

                            while (_audioEncoder.TrySendNextFrame())
                                DrainAudio();

                            audioStarted = true;
                            _audioFramePool.Return(frame.Data);
                            continue;
                        }
                        else
                        {
                            // First audio block arrived after vStart: pad silence for the gap so audio PTS 0 matches vStart.
                            long gapTicks = frame.TimestampQpc - vStart;
                            int gapSamples = (int)Math.Round((double)gapTicks * frame.SampleRate / freq);
                            if (gapSamples > 0)
                            {
                                FeedSilence(gapSamples);
                            }
                            audioStarted = true;
                        }
                    }

                    planes[0] = (byte*)frame.Data;
                    _audioEncoder.Accept(planes, 1, frame.Samples);

                    while (_audioEncoder.TrySendNextFrame())
                        DrainAudio();

                    _audioFramePool.Return(frame.Data);
                }

                _audioEncoder.Flush();
                DrainAudio();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AudioLoop EXCEPTION] {ex}");
            }
        }

        private void FeedSilence(int samples)
        {
            if (_audioParams == null || _audioEncoder == null || samples <= 0)
                return;

            int bytesPerSample = _audioParams.Format == SampleFormat.S16 ? 2 : _audioParams.Format == SampleFormat.S32 || _audioParams.Format == SampleFormat.FLT ? 4 : 1;
            int chunk = Math.Min(samples, 1024);
            byte[] zeroBuf = new byte[chunk * _audioParams.Channels * bytesPerSample];
            fixed (byte* p = zeroBuf)
            {
                byte** planes = stackalloc byte*[1];
                planes[0] = p;
                int remaining = samples;
                while (remaining > 0)
                {
                    int toSend = Math.Min(remaining, chunk);
                    _audioEncoder.Accept(planes, 1, toSend);
                    while (_audioEncoder.TrySendNextFrame())
                        DrainAudio();
                    remaining -= toSend;
                }
            }
        }

        private void DrainAudio()
        {
            EncodedAudioPacket? packet;
            while ((packet = _audioEncoder.ReceivePacket()) != null)
            {
                var p = packet.Value;
                nint buffer = _packetPool.Rent(p.Size);
                Unsafe.CopyBlockUnaligned((void*)buffer, (void*)p.Data, (uint)p.Size);
                _encodedOut.Add(new QueuedPacket(false, p.PresentationTimeStamp, p.DecodingTimeStamp, buffer, p.Size, false));
            }
        }

        private void MuxLoop()
        {
            try
            {
                foreach (var q in _encodedOut.GetConsumingEnumerable())
                {
                    if (q.IsVideo)
                        _container.WriteVideoPacket(new EncodedVideoPacket(q.Pts, q.Dts, q.Data, q.Size, q.IsKey));
                    else
                        _container.WriteAudioPacket(new EncodedAudioPacket(q.Pts, q.Dts, q.Data, q.Size));

                    _packetPool.Return(q.Data);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MuxLoop EXCEPTION] {ex}");
            }
        }

        public void Stop()
        {
            if (!_started)
                return;

            _vStartEvent.Set();

            _videoIn.CompleteAdding();
            _audioIn.CompleteAdding();

            _videoThread?.Join();
            _audioThread?.Join();

            _encodedOut.CompleteAdding();
            _muxThread?.Join();

            _container?.Dispose();
            _videoEncoder?.Dispose();
            _audioEncoder?.Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Stop();

            _vStartEvent.Dispose();
            _videoIn.Dispose();
            _audioIn.Dispose();
            _encodedOut.Dispose();
            _videoFramePool.Dispose();
            _audioFramePool.Dispose();
            _packetPool.Dispose();
        }
    }
}
