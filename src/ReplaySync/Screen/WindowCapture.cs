using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

/// <summary>
/// A part of a window's image: Origin is where it starts in the window. At is the Stopwatch time in seconds at
/// which it was composed.
/// </summary>
/// <remarks>The pixels are only valid until the next one is taken from the same capture.</remarks>
sealed record Frame(byte[] Bgra, int Width, int Height, Point Origin, double At)
{
    public int Stride => Width * 4;

    public float Luminance(int x, int y)
    {
        int o = y * Stride + x * 4;
        return (0.114f * Bgra[o] + 0.587f * Bgra[o + 1] + 0.299f * Bgra[o + 2]) / 255f;
    }

    /// <summary>The difference between the strongest and the weakest colour channel.</summary>
    public int Saturation(int x, int y)
    {
        int o = y * Stride + x * 4;
        byte b = Bgra[o], g = Bgra[o + 1], r = Bgra[o + 2];
        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
    }
}

/// <summary>
/// The pixels of one window through Windows Graphics Capture. They come from the compositor: nothing is sent to
/// the window, it is never activated and its thread does no work (PrintWindow took 135 ms of Deepdom's each time).
/// Works while the window is covered, not while it is minimized.
///
/// Only the region which is read gets copied back from the GPU: copying whole windows took a quarter of a CPU core
/// per window.
/// </summary>
sealed class WindowCapture : IDisposable
{
    const int MinUpdateMs = 200;

    readonly object _lock = new();
    readonly Gpu _gpu;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;

    Direct3D11CaptureFrame? _newest;
    byte[] _pixels = [];
    SizeInt32 _size;
    volatile bool _closed;

    public WindowCapture(IntPtr window)
    {
        var item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId { Value = (ulong)window.ToInt64() })
                   ?? throw new InvalidOperationException("the window can not be captured");
        _gpu = new Gpu();
        item.Closed += (_, _) => _closed = true;
        _size = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_gpu.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        try
        {
            _ = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
            _session.IsBorderRequired = false;
        }
        catch (Exception)
        {
            // Windows then draws its yellow border around the window, the capture works all the same
        }
        // Less work for the compositor on a slow machine
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) _session.MinUpdateInterval = TimeSpan.FromMilliseconds(MinUpdateMs);
        _session.StartCapture();
    }

    public bool IsClosed => _closed;

    void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        lock (_lock)
        {
            if (_closed) return;
            // Keep only the newest, so the pool never runs out and hands out old frames
            while (pool.TryGetNextFrame() is { } frame)
            {
                _newest?.Dispose();
                _newest = frame;
            }
        }
    }

    /// <summary>The region of the newest image, or null when the window has not changed since the last call.</summary>
    public Frame? TakeNewest(Rectangle region)
    {
        Direct3D11CaptureFrame? frame;
        lock (_lock)
        {
            frame = _newest;
            _newest = null;
        }
        if (frame is null) return null;

        using (frame)
        {
            var size = frame.ContentSize;
            if (size.Width != _size.Width || size.Height != _size.Height)
            {
                _size = size;
                _pool.Recreate(_gpu.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                return null;
            }
            region.Intersect(new Rectangle(0, 0, size.Width, size.Height));
            if (region.Width <= 0 || region.Height <= 0) return null;

            int length = 4 * region.Width * region.Height;
            if (_pixels.Length != length) _pixels = new byte[length];
            _gpu.Copy(frame.Surface, region, _pixels);
            return new Frame(_pixels, region.Width, region.Height, region.Location, frame.SystemRelativeTime.TotalSeconds);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _closed = true;
            _newest?.Dispose();
            _newest = null;
        }
        _pool.FrameArrived -= OnFrame;
        _session.Dispose();
        _pool.Dispose();
        _gpu.Dispose();
    }

    /// <summary>
    /// The little of Direct3D 11 which copying a region takes. The methods are called through their COM vtable
    /// slots (d3d11.h), which are fixed, rather than adding a Direct3D binding package for five calls.
    /// </summary>
    sealed class Gpu : IDisposable
    {
        const int DeviceCreateTexture2D = 5, ContextMap = 14, ContextUnmap = 15, ContextCopySubresourceRegion = 46;
        const int MultithreadSetProtected = 5, SurfaceGetInterface = 3;
        const int FormatBgra8 = 87, UsageStaging = 3, CpuAccessRead = 0x20000, MapRead = 1;

        static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        static readonly Guid MultithreadId = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
        static readonly Guid SurfaceAccessId = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
        static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        struct TextureDescription
        {
            public int Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, Misc;
        }

#pragma warning disable CS0649 // filled in by Map
        struct Mapped
        {
            public IntPtr Data;
            public int RowPitch, DepthPitch;
        }

#pragma warning restore CS0649
        struct Box
        {
            public int Left, Top, Front, Right, Bottom, Back;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateTexture2DFn(IntPtr device, in TextureDescription description, IntPtr data, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int MapFn(IntPtr context, IntPtr resource, int subresource, int type, int flags, out Mapped mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void UnmapFn(IntPtr context, IntPtr resource, int subresource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void CopyRegionFn(IntPtr context, IntPtr target, int targetSubresource, int x, int y, int z, IntPtr source, int sourceSubresource, in Box box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int SetProtectedFn(IntPtr multithread, int on);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetInterfaceFn(IntPtr access, in Guid id, out IntPtr result);

        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels,
            uint levelCount, uint sdkVersion, out IntPtr device, IntPtr level, out IntPtr context);

        [DllImport("d3d11.dll")]
        static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr device);

        readonly CreateTexture2DFn _createTexture;
        readonly MapFn _map;
        readonly UnmapFn _unmap;
        readonly CopyRegionFn _copyRegion;
        IntPtr _device, _context, _staging;
        Size _stagingSize;

        public IDirect3DDevice Device { get; }

        public Gpu()
        {
            const int Hardware = 1, SdkVersion = 7;
            const uint BgraSupport = 0x20;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, Hardware, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0,
                SdkVersion, out _device, IntPtr.Zero, out _context));

            _createTexture = Method<CreateTexture2DFn>(_device, DeviceCreateTexture2D);
            _map = Method<MapFn>(_context, ContextMap);
            _unmap = Method<UnmapFn>(_context, ContextUnmap);
            _copyRegion = Method<CopyRegionFn>(_context, ContextCopySubresourceRegion);

            // The capture uses the device from its own threads
            if (Marshal.QueryInterface(_device, in MultithreadId, out var multithread) >= 0)
            {
                _ = Method<SetProtectedFn>(multithread, MultithreadSetProtected)(multithread, 1);
                Marshal.Release(multithread);
            }

            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_device, in DxgiDeviceId, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { Device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }

        static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        /// <summary>Copies the region, tightly packed, into pixels.</summary>
        public void Copy(IDirect3DSurface surface, Rectangle region, byte[] pixels)
        {
            var source = TextureOf(surface);
            try
            {
                if (_staging == IntPtr.Zero || _stagingSize != region.Size) CreateStaging(region.Size);

                var box = new Box { Left = region.Left, Top = region.Top, Front = 0, Right = region.Right, Bottom = region.Bottom, Back = 1 };
                _copyRegion(_context, _staging, 0, 0, 0, 0, source, 0, in box);

                Marshal.ThrowExceptionForHR(_map(_context, _staging, 0, MapRead, 0, out var mapped));
                try
                {
                    int rowBytes = region.Width * 4;
                    for (int y = 0; y < region.Height; y++)
                        Marshal.Copy(IntPtr.Add(mapped.Data, y * mapped.RowPitch), pixels, y * rowBytes, rowBytes);
                }
                finally { _unmap(_context, _staging, 0); }
            }
            finally { Marshal.Release(source); }
        }

        static IntPtr TextureOf(IDirect3DSurface surface)
        {
            var abi = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in SurfaceAccessId, out var access));
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetInterfaceFn>(access, SurfaceGetInterface)(access, in Texture2DId, out var texture));
                    return texture;
                }
                finally { Marshal.Release(access); }
            }
            finally { Marshal.Release(abi); }
        }

        void CreateStaging(Size size)
        {
            if (_staging != IntPtr.Zero) Marshal.Release(_staging);
            _staging = IntPtr.Zero;
            var description = new TextureDescription
            {
                Width = size.Width, Height = size.Height, MipLevels = 1, ArraySize = 1, Format = FormatBgra8,
                SampleCount = 1, SampleQuality = 0, Usage = UsageStaging, BindFlags = 0, CpuAccess = CpuAccessRead, Misc = 0,
            };
            Marshal.ThrowExceptionForHR(_createTexture(_device, in description, IntPtr.Zero, out _staging));
            _stagingSize = size;
        }

        public void Dispose()
        {
            if (_staging != IntPtr.Zero) Marshal.Release(_staging);
            if (_context != IntPtr.Zero) Marshal.Release(_context);
            if (_device != IntPtr.Zero) Marshal.Release(_device);
            _staging = _context = _device = IntPtr.Zero;
        }
    }
}
