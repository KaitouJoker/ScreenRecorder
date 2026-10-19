using System;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using ScreenRecorder.DirectX;

namespace ScreenRecorder.VideoSource
{
    /// <summary>
    /// Windows.Graphics.Capture session. Frames are polled directly by the paced capture thread
    /// on each tick via <see cref="TryCopyLatest"/>, ensuring all Direct3D context operations
    /// occur exclusively on that single thread without cross-thread serialization or deadlocks.
    /// </summary>
    internal sealed class WgcCapture : IDisposable
    {
        private readonly Direct3D11Device _device;
        private readonly bool _drawCursor;

        private IDirect3DDevice _winrtDevice;
        private Direct3D11CaptureFramePool _framePool;
        private GraphicsCaptureSession _session;
        private GraphicsCaptureItem _item;
        private ID3D11Texture2D _latestBgra;
        private SizeInt32 _size;
        private bool _hasFrame;
        private bool _disposed;

        public event Action Closed;

        public SizeInt32 ItemSize => _size;

        public WgcCapture(Direct3D11Device device, GraphicsCaptureItem item, bool drawCursor)
        {
            _device = device;
            _item = item;
            _drawCursor = drawCursor;
            _size = item.Size;

            _latestBgra = CreateBgraTexture(_size);

            _winrtDevice = WgcInterop.CreateWinRtDevice(device.Device);
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _session = _framePool.CreateCaptureSession(item);

            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                _session.IsCursorCaptureEnabled = drawCursor;
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                _session.IsBorderRequired = false;

            item.Closed += (s, e) => Closed?.Invoke();
        }

        public void Start()
        {
            _session.StartCapture();
        }

        /// <summary>
        /// Pulls the latest frame from the capture pool (if available), copies the crop region into
        /// <paramref name="dstRegion"/>, and returns true if a frame was copied. If no new frame
        /// arrived, re-uses the held latest frame.
        /// Must be called exclusively from the capture clock thread.
        /// </summary>
        public bool TryCopyLatest(ID3D11DeviceContext context, ID3D11Texture2D dstRegion, in Box crop)
        {
            if (_disposed)
                return false;

            // Drain any newly arrived frame from the pool.
            Direct3D11CaptureFrame latestFrame = null;
            while (true)
            {
                var next = _framePool.TryGetNextFrame();
                if (next == null)
                    break;

                latestFrame?.Dispose();
                latestFrame = next;
            }

            if (latestFrame != null)
            {
                using (latestFrame)
                {
                    if (latestFrame.ContentSize.Width != _size.Width || latestFrame.ContentSize.Height != _size.Height)
                    {
                        _size = latestFrame.ContentSize;
                        _latestBgra?.Dispose();
                        _latestBgra = CreateBgraTexture(_size);
                        _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                    }

                    using var tex = WgcInterop.GetTexture(latestFrame.Surface);
                    context.CopyResource(_latestBgra, tex);
                    _hasFrame = true;
                }
            }

            if (!_hasFrame || _latestBgra == null)
                return false;

            context.CopySubresourceRegion(dstRegion, 0, 0, 0, 0, _latestBgra, 0, crop);
            return true;
        }

        private ID3D11Texture2D CreateBgraTexture(SizeInt32 size)
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)size.Width,
                Height = (uint)size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };
            return _device.Device.CreateTexture2D(desc);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _session?.Dispose();
            _framePool?.Dispose();
            _latestBgra?.Dispose();
            _winrtDevice?.Dispose();
            _item = null;
        }
    }
}
