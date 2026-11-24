using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace SmoothMotion.Windows;

/// <summary>
/// GPU-only, bidirectional block motion interpolation. Calls must be serialized by the host.
/// The host copies each decoded frame into InputSurface, calls SubmitFrame, then renders phases
/// between 0 and 1. The host owns presentation timing and the one-frame audio lookahead delay.
/// No WinUI or media-player dependency; software devices are reserved for automated tests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MotionEngine : IDisposable
{
    private static readonly Lazy<Dictionary<string, ReadOnlyMemory<byte>>> Bytecode = new(() =>
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("SmoothMotion.Windows.Shaders.Motion.hlsl")!;
        var text = new StreamReader(source).ReadToEnd();
        return new[] { "Luma", "Search", "Median", "Scene", "Render" }.ToDictionary(
            entry => entry, entry => Compiler.Compile(text, entry, "Motion.hlsl", "cs_5_0"));
    });
    private readonly List<IDisposable> _owned = [];
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly Dictionary<string, ID3D11ComputeShader> _shaders = [];
    private readonly Texture _input, _output, _scene;
    private Texture _a, _b;
    private readonly List<Level> _levels = [];
    private readonly IDXGISwapChain1? _swapChain;
    private nint _surface;
    private bool _disposed;
    private int _frames;

    public uint Width { get; }
    public uint Height { get; }
    /// <summary>Hosts using a render thread must serialize all GPU operations with this gate.</summary>
    public object SyncRoot { get; } = new();
    public string AdapterName { get; } = "Unknown GPU";
    /// <summary>An owned IInspectable/IDirect3DSurface pointer. Do not release it.</summary>
    public nint InputSurface => _surface;
    public nint SwapChain => _swapChain?.NativePointer ?? 0;
    public bool HasPair => _frames >= 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public uint Width, Height, FieldWidth, FieldHeight, ParentWidth, ParentHeight, Radius, HasParent;
        public float Phase, Pad1, Pad2, Pad3;
    }

    private sealed record Texture(ID3D11Texture2D Resource, ID3D11ShaderResourceView View,
        ID3D11UnorderedAccessView? Write, uint Width, uint Height);
    private sealed record Level(Texture A, Texture B, Texture RawF, Texture RawB, Texture F, Texture Bwd);

    public MotionEngine(uint width, uint height, bool presentation = true, bool softwareDevice = false)
    {
        if (width == 0 || height == 0 || width > 1920 || height > 1920 || (ulong)width * height > 1920 * 1080)
            throw new ArgumentOutOfRangeException(nameof(width), "The first Windows engine supports up to 1080p SDR.");
        Width = width; Height = height;
        try
        {
            D3D11CreateDevice(null, softwareDevice ? DriverType.Warp : DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0],
                out _device!, out _context!).CheckError();
            Own(_device); Own(_context);
            _constants = Own(_device.CreateBuffer(new BufferDescription(48, BindFlags.ConstantBuffer)));
            _sampler = Own(_device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
                TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp)));
            foreach (var (entry, code) in Bytecode.Value)
                _shaders[entry] = Own(_device.CreateComputeShader(code.Span));
            _input = Create(width, height, Format.B8G8R8A8_UNorm, false);
            _a = Create(width, height, Format.B8G8R8A8_UNorm, false);
            _b = Create(width, height, Format.B8G8R8A8_UNorm, false);
            _output = Create(width, height, Format.R8G8B8A8_UNorm);
            _scene = Create(1, 1, Format.R32_UInt);
            var scale = Math.Min(1.0, 320.0 / Math.Max(width, height));
            uint w = Math.Max(1, (uint)Math.Round(width * scale)), h = Math.Max(1, (uint)Math.Round(height * scale));
            while (true)
            {
                var fw = (w + 7) / 8; var fh = (h + 7) / 8;
                _levels.Add(new(Create(w, h), Create(w, h), Create(fw, fh), Create(fw, fh), Create(fw, fh), Create(fw, fh)));
                if (Math.Max(w, h) <= 40) break;
                w = (w + 1) / 2; h = (h + 1) / 2;
            }
            using (var dxgiSurface = _input.Resource.QueryInterface<IDXGISurface>())
                Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out _surface));
            if (presentation)
            {
                using var dxgi = _device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgi.GetAdapter();
                AdapterName = adapter.Description.Description;
                using var factory = adapter.GetParent<IDXGIFactory2>();
                _swapChain = Own(factory.CreateSwapChainForComposition(_device, new SwapChainDescription1
                {
                    Width = width,
                    Height = height,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipSequential,
                    AlphaMode = AlphaMode.Ignore,
                }));
            }
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }
    private Texture Create(uint width, uint height, Format format = Format.R32G32B32A32_Float, bool writable = true)
    {
        var resource = Own(_device.CreateTexture2D(new Texture2DDescription(format, width, height, 1, 1,
            BindFlags.ShaderResource | (writable ? BindFlags.UnorderedAccess : BindFlags.RenderTarget))));
        return new(resource, Own(_device.CreateShaderResourceView(resource)),
            writable ? Own(_device.CreateUnorderedAccessView(resource)) : null, width, height);
    }

    private void Run(string shader, Parameters p, Texture output, Texture? a = null, Texture? b = null,
        Texture? parent = null, Texture? backward = null, Texture? forward = null, Texture? scene = null)
    {
        _context.UpdateSubresource(in p, _constants);
        _context.CSSetConstantBuffer(0, _constants);
        _context.CSSetSampler(0, _sampler);
        _context.CSSetShader(_shaders[shader]);
        Texture?[] inputs = [a, b, parent, backward, forward, scene];
        for (uint i = 0; i < inputs.Length; ++i) _context.CSSetShaderResource(i, inputs[i]?.View);
        _context.CSSetUnorderedAccessView(shader == "Scene" ? 1u : 0u, output.Write);
        var width = shader is "Search" or "Median" or "Scene" ? p.FieldWidth : p.Width;
        var height = shader is "Search" or "Median" or "Scene" ? p.FieldHeight : p.Height;
        _context.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
        // Unbind before the next dispatch uses an output as an input (D3D11 implicit barrier).
        _context.CSSetUnorderedAccessView(0, null); _context.CSSetUnorderedAccessView(1, null);
        for (uint i = 0; i < 6; ++i) _context.CSSetShaderResource(i, null);
    }

    /// <summary>Call after copying a decoded frame into InputSurface. No CPU pixel readback.</summary>
    public void SubmitFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        (_a, _b) = (_b, _a);
        _context.CopyResource(_b.Resource, _input.Resource);
        _frames++;
        if (!HasPair) return;
        foreach (var level in _levels)
        {
            var p = new Parameters { Width = level.A.Width, Height = level.A.Height };
            Run("Luma", p, level.A, _a);
            Run("Luma", p, level.B, _b);
        }
        for (int i = _levels.Count - 1; i >= 0; --i)
        {
            var level = _levels[i]; var parent = i + 1 < _levels.Count ? _levels[i + 1] : null;
            var p = new Parameters
            {
                Width = level.A.Width,
                Height = level.A.Height,
                FieldWidth = level.F.Width,
                FieldHeight = level.F.Height,
                Radius = parent is null ? 4u : 2u,
                HasParent = parent is null ? 0u : 1u
            };
            Run("Search", p, level.RawF, level.A, level.B, parent?.F);
            Run("Search", p, level.RawB, level.B, level.A, parent?.Bwd);
            Run("Median", p, level.F, level.RawF);
            Run("Median", p, level.Bwd, level.RawB);
        }
        _context.ClearUnorderedAccessView(_scene.Write!, new UInt4(0));
        var fine = _levels[0];
        Run("Scene", new Parameters { FieldWidth = fine.F.Width, FieldHeight = fine.F.Height }, _scene, fine.F);
    }

    /// <summary>Draw the pair at a normalized phase. Phase 0 and 1 preserve originals exactly.</summary>
    public void Render(float phase)
    {
        if (!HasPair) return;
        var fine = _levels[0];
        Run("Render", new Parameters
        {
            Width = Width,
            Height = Height,
            FieldWidth = fine.F.Width,
            FieldHeight = fine.F.Height,
            ParentWidth = fine.A.Width,
            ParentHeight = fine.A.Height,
            Phase = Math.Clamp(phase, 0, 1)
        },
            _output, _a, _b, backward: fine.Bwd, forward: fine.F, scene: _scene);
    }

    /// <summary>Draw a decoded original, including the single frame delivered by a paused seek.</summary>
    public void RenderLatest()
    {
        if (_frames == 0) return;
        Run("Render", new Parameters { Width = Width, Height = Height, Phase = 1 }, _output, _b, _b, scene: _scene);
    }

    /// <summary>Nonblocking presentation; false means the presenter could not accept this tick.</summary>
    public bool Present()
    {
        if (_swapChain is null || _frames == 0) return false;
        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _context.CopyResource(back, _output.Resource);
        var result = _swapChain.Present(0, PresentFlags.DoNotWait);
        if (result.Code == unchecked((int)0x887A000A)) return false; // DXGI_ERROR_WAS_STILL_DRAWING
        result.CheckError();
        return true;
    }

    public void SetPresentationSize(float width, float height)
    {
        if (_swapChain is null) return;
        using var chain = _swapChain.QueryInterface<IDXGISwapChain2>();
        chain.MatrixTransform = Matrix3x2.CreateScale(width / Width, height / Height);
    }

    public void Reset() => _frames = 0;

    // Deterministic GPU harness: upload BGRA frames and read the rendered RGBA result.
    public void UploadFrame(ReadOnlySpan<uint> bgra)
    {
        if (bgra.Length != Width * Height) throw new ArgumentException("Wrong frame size.", nameof(bgra));
        _context.UpdateSubresource(bgra, _input.Resource, rowPitch: Width * 4);
        SubmitFrame();
    }
    public unsafe byte[] ReadOutput()
    {
        var description = _output.Resource.Description;
        description.BindFlags = BindFlags.None; description.Usage = ResourceUsage.Staging;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _device.CreateTexture2D(description);
        _context.CopyResource(staging, _output.Resource);
        var mapped = _context.Map(staging, 0, MapMode.Read);
        try
        {
            var bytes = new byte[Width * Height * 4];
            for (uint y = 0; y < Height; ++y)
                Marshal.Copy(mapped.DataPointer + (int)(y * mapped.RowPitch), bytes, (int)(y * Width * 4), (int)(Width * 4));
            return bytes;
        }
        finally { _context.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_surface != 0) { Marshal.Release(_surface); _surface = 0; }
        for (int i = _owned.Count - 1; i >= 0; --i) _owned[i].Dispose();
        _owned.Clear();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint surface);
}
