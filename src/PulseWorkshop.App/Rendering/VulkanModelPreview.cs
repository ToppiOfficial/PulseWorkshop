using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PulseWorkshop.Core.Materials;
using PulseWorkshop.Core.Mdl;
using PulseWorkshop.Core.Unpack;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace PulseWorkshop.App.Rendering;

/// <summary>
/// The little Vulkan renderer behind the Unpack tab's model preview: draws one untextured mesh over
/// a ground grid and hands back the pixels as a BGRA byte array.
/// <para>
/// It renders <b>offscreen</b> and copies the result out, rather than presenting to a swapchain in a
/// child HWND. That keeps it a plain bitmap source as far as WPF is concerned - no airspace problems
/// with the panel it sits in, no HwndHost, no surface extensions - at the cost of a per-frame image
/// copy, which for a pane a couple of hundred pixels across is nothing.
/// </para>
/// <para>
/// Vulkan rather than D3D/OpenGL because this is meant to grow into the model editor's viewport
/// later. What is here now is deliberately the smallest thing that draws: one device, one render
/// pass, one shader pair over a handful of pipelines (the triangle variants, the grid's lines and the
/// skeleton overlay's), and no frames in flight. Every submit is followed by a queue wait.
/// </para>
/// <para>
/// <b>Not thread safe.</b> A Vulkan device needs external synchronization and this class does none:
/// create it and call it from one thread (the UI thread). Parsing the model is the slow part and
/// that happens elsewhere; a render at preview size is sub-millisecond.
/// </para>
/// </summary>
public sealed unsafe class VulkanModelPreview : IDisposable
{
    /// <summary>Multisampling for the offscreen colour/depth targets. 4x is a Vulkan spec minimum
    /// every implementation must support, so it is required rather than negotiated - a device that
    /// somehow lacks it fails <see cref="TryCreate"/> and the caller falls back to a file glyph.</summary>
    private const SampleCountFlags Samples = SampleCountFlags.Count4Bit;

    private const Format ColorFormat = Format.B8G8R8A8Unorm; // matches WPF's Bgra32 byte-for-byte
    private const Format DepthFormat = Format.D32Sfloat;     // mandatory as a depth attachment

    /// <summary>
    /// Which winding faces the viewer, once the projection's Y flip has reversed what the .vtx index
    /// order would otherwise give. Settled by rendering closed models with culling on and diffing
    /// against the same render with culling off - the wrong choice shows the far interior instead.
    /// </summary>
    private const FrontFace ModelFrontFace = FrontFace.Clockwise;

    /// <summary>Push constant block, mirroring the one both shader stages declare (112 bytes - inside
    /// the 128 every Vulkan implementation guarantees).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public Matrix4x4 Mvp;
        public Vector4 Color;   // rgb = tint, a = opacity ($alpha; 1 for everything opaque)
        public Vector4 Key;     // xyz = direction from the surface to the key light, w = $alphatest cutoff
        public Vector4 Params;  // x = diffuse shading amount (0 = flat), yzw unused
    }

    /// <summary>How far the key light sits off the eye vector: swung round in azimuth and lifted, so
    /// shading still describes the form instead of flattening it the way a head-on light does.</summary>
    private const float KeyLightYawOffset = 0.65f, KeyLightPitch = 0.55f;

    /// <summary>Vertex layout: position, normal, texture coordinate.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex(Vector3 position, Vector3 normal, Vector2 uv = default)
    {
        public Vector3 Position = position;
        public Vector3 Normal = normal;
        public Vector2 Uv = uv;
    }

    /// <summary>How a part combines with what is already drawn. Opaque writes depth; the other two
    /// blend and do not, so they draw after every opaque part.</summary>
    private enum BlendMode { Opaque = 0, Translucent = 1, Additive = 2 }

    private const int BlendModeCount = 3;

    /// <summary>Which of <see cref="_trianglePipelines"/> a draw uses. Culling is the engine default
    /// and $nocull turns it off.</summary>
    private static int PipelineIndex(bool noCull, BlendMode blend) =>
        (noCull ? BlendModeCount : 0) + (int)blend;

    /// <summary>One draw: a slice of the index buffer, the material texture it samples, and how it is
    /// lit. Built once per mesh so the render loop is a flat walk with no material logic in it.
    /// <para>
    /// <paramref name="Center"/> is the part's centroid, used to sort translucent parts back to
    /// front; <paramref name="BodyPart"/> and <paramref name="Model"/> are its bodygroup slot, which
    /// decides whether it is drawn at all.
    /// </para></summary>
    private readonly record struct DrawPart(
        int FirstIndex, int IndexCount, DescriptorSet Texture, Vector4 Color, float AlphaTestReference,
        float Shading, int Pipeline, Vector3 Center, int BodyPart, int Model);

    /// <summary>A texture the fragment shader can sample, with the descriptor set that binds it.
    /// Descriptor sets are not freed individually - the whole pool is reset per mesh.</summary>
    private sealed class PreviewTexture
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public DescriptorSet Set;
    }

    private readonly Vk _vk;
    private readonly Instance _instance;
    private readonly PhysicalDevice _physical;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _commandPool;
    private readonly CommandBuffer _commandBuffer;
    private readonly RenderPass _renderPass;
    private readonly DescriptorSetLayout _descriptorLayout;
    private readonly PipelineLayout _pipelineLayout;

    /// <summary>The six triangle variants, indexed by <see cref="PipelineIndex"/>: back-face culling
    /// on or off ($nocull) crossed with the three blend modes.</summary>
    private readonly Pipeline[] _trianglePipelines;

    private readonly Pipeline _linePipeline;

    /// <summary>The skeleton overlay's pipeline: the same lines, but depth-tested not at all and
    /// blended, so bones read as an x-ray through whatever mesh is in front of them.</summary>
    private readonly Pipeline _xrayLinePipeline;

    private readonly Sampler _sampler;
    private readonly Action<string>? _log;

    /// <summary>True when the colour format supports a linear blit, which is how the mip chain is
    /// built. Universally true on desktop drivers; a device without it just gets mip 0.</summary>
    private readonly bool _canGenerateMipmaps;

    /// <summary>The adapter the preview is running on, for the console banner.</summary>
    public string DeviceName { get; }

    // Offscreen targets, rebuilt whenever the requested size changes.
    private int _targetWidth, _targetHeight;
    private Image _colorMsaa, _colorResolve, _depth;
    private DeviceMemory _colorMsaaMemory, _colorResolveMemory, _depthMemory;
    private ImageView _colorMsaaView, _colorResolveView, _depthView;
    private Framebuffer _framebuffer;
    private Buffer _readback;
    private DeviceMemory _readbackMemory;
    private void* _readbackMapped;

    /// <summary>Reused frame buffer, resized with the target. Handed back by <see cref="Render"/>
    /// rather than a fresh array, so a live render loop allocates nothing per frame.</summary>
    private byte[] _pixels = [];

    // The current mesh and its grid, uploaded straight into host-visible memory (a preview mesh is a
    // few MB at most and is drawn a handful of times, so a staging copy into device-local memory
    // would cost more than it saves).
    private Buffer _vertexBuffer, _indexBuffer, _gridBuffer, _boneBuffer;
    private DeviceMemory _vertexMemory, _indexMemory, _gridMemory, _boneMemory;
    private int _indexCount, _gridVertexCount, _boneVertexCount;

    // Material textures and the draw list that references them, both rebuilt by SetMesh. The pool is
    // recreated per mesh rather than freeing sets one at a time.
    private DescriptorPool _descriptorPool;
    private readonly List<PreviewTexture> _textures = [];

    // Opaque parts keep their build order; blended ones ($translucent or $additive) are re-sorted
    // every frame against the eye, so they live in their own array and draw last.
    private DrawPart[] _opaqueParts = [];
    private DrawPart[] _translucentParts = [];

    /// <summary>Which sub-model each bodygroup is showing, one entry per
    /// <see cref="StudioMesh.BodyParts"/>. Defaults to all-zero, which is the engine's body 0.</summary>
    private int[] _bodyGroupSelection = [];

    /// <summary>The loaded model's bodygroups, for the UI to build a picker from.</summary>
    public IReadOnlyList<StudioBodyPart> BodyParts { get; private set; } = [];

    /// <summary>Draws the bind-pose skeleton over the mesh as an x-ray. Free to toggle - the bones are
    /// uploaded with the mesh either way, this only decides whether the draw is recorded.</summary>
    public bool ShowSkeleton { get; set; }

    /// <summary>True when the loaded model has bones worth drawing (a static prop with a single root
    /// bone has none), which is what the UI hangs its checkbox off.</summary>
    public bool HasSkeleton => _boneVertexCount > 0;

    /// <summary>How many $texturegroup skins the loaded model has (1 when it has no real choice).</summary>
    public int SkinCount => _mesh?.SkinFamilies.Length ?? 1;

    // Kept so a bodygroup or skin change can rebuild the draw list without re-reading the model or
    // re-uploading a single texture.
    private StudioMesh? _mesh;
    private IReadOnlyList<ModelMaterial>? _materials;
    private int _skin;

    /// <summary>Descriptor set per material index, uploaded once with the mesh. Skins re-point parts
    /// at different entries here, which is why the upload is not tied to what skin 0 happens to use.</summary>
    private readonly Dictionary<int, DescriptorSet> _materialSets = [];

    /// <summary>The two stand-ins, kept for the renderer's lifetime: flat white for anything that
    /// draws untextured (the grid, a shader we don't implement), and the engine's magenta/black
    /// checkerboard for a material or texture that is genuinely missing.</summary>
    private PreviewTexture? _whiteTexture, _checkerTexture;

    /// <summary>The X/Y/Z axis lines live at the tail of the grid buffer - 2 vertices each, drawn as
    /// three 2-vertex draws so each gets its own colour without a per-vertex colour attribute.</summary>
    private int _axisFirstVertex;

    /// <summary>Colours for the origin gizmo, in X/Y/Z order (the usual red/green/blue). Fully opaque
    /// and drawn unshaded - the shading amount is a separate push constant.</summary>
    private static readonly Vector4[] AxisColors =
    [
        new(0.92f, 0.26f, 0.28f, 1f),
        new(0.40f, 0.83f, 0.35f, 1f),
        new(0.32f, 0.55f, 0.95f, 1f),
    ];

    /// <summary>
    /// A quarter turn about Z applied to the model (and its skeleton) but <b>not</b> to the ground grid
    /// or the origin gizmo. A compiled Source model's bind-pose vertices face -Y; HLMV presents them
    /// facing +X, and this is what lines the preview up with that. Purely presentational - the vertex
    /// buffer still holds what the .vvd stores, and the two constants are each other's inverse (the
    /// translucent sort needs the eye brought back into the model's own space).
    /// </summary>
    private static readonly Matrix4x4 ModelOrientation = Matrix4x4.CreateRotationZ(MathF.PI / 2f);

    private static readonly Matrix4x4 ModelOrientationInverse = Matrix4x4.CreateRotationZ(-MathF.PI / 2f);

    /// <summary>The skeleton overlay's colour: near-white grey at a bit over half opacity, which stays
    /// legible over both a dark texture and a bright one without reading as geometry.</summary>
    private static readonly Vector4 SkeletonColor = new(0.86f, 0.87f, 0.90f, 0.55f);

    /// <summary>Half the ground grid's extent, in Source units - the camera framing uses it so the
    /// model never floats outside the floor.</summary>
    private float _gridExtent;

    /// <summary>Centre of the loaded mesh's bounds; the camera orbits this point.</summary>
    private Vector3 _meshCenter;

    /// <summary>Radius of the loaded mesh, used as the camera's distance unit.</summary>
    private float _meshRadius = 1f;

    private bool _disposed;

    private VulkanModelPreview(Vk vk, Instance instance, PhysicalDevice physical, Device device,
        Queue queue, CommandPool pool, CommandBuffer cmd, RenderPass renderPass,
        DescriptorSetLayout descriptorLayout, PipelineLayout layout, Pipeline[] triangles, Pipeline lines,
        Pipeline xrayLines, Sampler sampler, bool canGenerateMipmaps, string deviceName,
        Action<string>? log)
    {
        _vk = vk;
        _instance = instance;
        _physical = physical;
        _device = device;
        _queue = queue;
        _commandPool = pool;
        _commandBuffer = cmd;
        _renderPass = renderPass;
        _descriptorLayout = descriptorLayout;
        _pipelineLayout = layout;
        _trianglePipelines = triangles;
        _linePipeline = lines;
        _xrayLinePipeline = xrayLines;
        _sampler = sampler;
        _canGenerateMipmaps = canGenerateMipmaps;
        DeviceName = deviceName;
        _log = log;
    }

    /// <summary>True once a mesh has been uploaded and the preview has something to draw.</summary>
    public bool HasMesh => _indexCount > 0;

    /// <summary>GPU time for the last <see cref="Render"/> call, wall-clock across submit and the
    /// wait that follows it. Drives the preview's frame-rate readout.</summary>
    public double LastFrameMilliseconds { get; private set; }

    // --- creation --------------------------------------------------------------------------------

    /// <summary>
    /// Brings up an instance, device and pipelines, or returns null when this machine cannot render
    /// with Vulkan at all (no loader, no adapter, driver refuses). Every failure is logged and
    /// swallowed: a missing preview is not worth an error dialog.
    /// </summary>
    public static VulkanModelPreview? TryCreate(Action<string>? log = null)
    {
        var started = Stopwatch.StartNew();
        Vk? vk = null;
        Instance instance = default;
        Device device = default;
        try
        {
            vk = Vk.GetApi();

            // No layers, no extensions: an offscreen renderer needs neither a surface nor a swapchain.
            var appName = (byte*)SilkMarshal.StringToPtr("PulseWorkshop");
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApplicationVersion = Vk.MakeVersion(1, 0, 0),
                PEngineName = appName,
                EngineVersion = Vk.MakeVersion(1, 0, 0),
                ApiVersion = Vk.Version10,
            };
            var instanceInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
            };
            var created = vk.CreateInstance(in instanceInfo, null, out instance);
            SilkMarshal.Free((nint)appName);
            if (created != Result.Success)
            {
                log?.Invoke($"3D preview: no Vulkan instance ({created}) - is a GPU driver with Vulkan support installed?");
                vk.Dispose();
                return null;
            }

            // Pick an adapter: a discrete GPU if there is one, else whatever has a graphics queue.
            uint deviceCount = 0;
            vk.EnumeratePhysicalDevices(instance, ref deviceCount, null);
            if (deviceCount == 0)
            {
                log?.Invoke("3D preview: Vulkan reports no physical devices");
                vk.DestroyInstance(instance, null);
                vk.Dispose();
                return null;
            }
            var physicalDevices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* p = physicalDevices)
                vk.EnumeratePhysicalDevices(instance, ref deviceCount, p);

            PhysicalDevice chosen = default;
            uint graphicsFamily = uint.MaxValue;
            string chosenName = "unknown";
            bool chosenIsDiscrete = false;
            foreach (var candidate in physicalDevices)
            {
                if (FindGraphicsFamily(vk, candidate) is not { } family)
                    continue;
                vk.GetPhysicalDeviceProperties(candidate, out var props);
                bool msaaOk =
                    (props.Limits.FramebufferColorSampleCounts & Samples) != 0 &&
                    (props.Limits.FramebufferDepthSampleCounts & Samples) != 0;
                string name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "unknown";
                if (!msaaOk)
                {
                    log?.Invoke($"3D preview: skipping {name} - no 4x MSAA support");
                    continue;
                }
                bool discrete = props.DeviceType == PhysicalDeviceType.DiscreteGpu;
                if (graphicsFamily == uint.MaxValue || (discrete && !chosenIsDiscrete))
                {
                    chosen = candidate;
                    graphicsFamily = family;
                    chosenName = name;
                    chosenIsDiscrete = discrete;
                }
            }
            if (graphicsFamily == uint.MaxValue)
            {
                log?.Invoke("3D preview: no Vulkan device with a usable graphics queue");
                vk.DestroyInstance(instance, null);
                vk.Dispose();
                return null;
            }

            float priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = graphicsFamily,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
            };
            if (vk.CreateDevice(chosen, in deviceInfo, null, out device) is var dr && dr != Result.Success)
            {
                log?.Invoke($"3D preview: vkCreateDevice failed ({dr})");
                vk.DestroyInstance(instance, null);
                vk.Dispose();
                return null;
            }

            vk.GetDeviceQueue(device, graphicsFamily, 0, out var queue);

            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = graphicsFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };
            Check(vk.CreateCommandPool(device, in poolInfo, null, out var pool), "vkCreateCommandPool");

            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(vk.AllocateCommandBuffers(device, in allocInfo, out var cmd), "vkAllocateCommandBuffers");

            var renderPass = CreateRenderPass(vk, device);
            var descriptorLayout = CreateDescriptorLayout(vk, device);
            var (layout, triangles, lines, xrayLines) =
                CreatePipelines(vk, device, renderPass, descriptorLayout);
            var sampler = CreateSampler(vk, device);

            // Mips come from a blit chain, which needs the format to filter linearly when sampled.
            vk.GetPhysicalDeviceFormatProperties(chosen, ColorFormat, out var formatProperties);
            bool canMip = (formatProperties.OptimalTilingFeatures
                           & FormatFeatureFlags.SampledImageFilterLinearBit) != 0;

            log?.Invoke($"3D preview: Vulkan ready on {chosenName} ({started.ElapsedMilliseconds} ms)");
            return new VulkanModelPreview(vk, instance, chosen, device, queue, pool, cmd, renderPass,
                descriptorLayout, layout, triangles, lines, xrayLines, sampler, canMip, chosenName, log);
        }
        catch (Exception ex)
        {
            // A missing vulkan-1.dll surfaces here as a DllNotFoundException from Vk.GetApi().
            log?.Invoke($"3D preview: Vulkan unavailable - {ex.GetType().Name}: {ex.Message}");
            try
            {
                if (vk is not null)
                {
                    if (device.Handle != 0) vk.DestroyDevice(device, null);
                    if (instance.Handle != 0) vk.DestroyInstance(instance, null);
                    vk.Dispose();
                }
            }
            catch { /* teardown of a half-built device is best effort */ }
            return null;
        }
    }

    private static uint? FindGraphicsFamily(Vk vk, PhysicalDevice device)
    {
        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        if (count == 0)
            return null;
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
            vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, p);
        for (uint i = 0; i < count; i++)
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                return i;
        return null;
    }

    /// <summary>
    /// One subpass: multisampled colour + depth, resolving into a single-sample image that ends in
    /// TRANSFER_SRC layout so the copy-out needs no barrier of its own.
    /// </summary>
    private static RenderPass CreateRenderPass(Vk vk, Device device)
    {
        var attachments = stackalloc AttachmentDescription[3];
        attachments[0] = new AttachmentDescription // multisampled colour - discarded after resolve
        {
            Format = ColorFormat,
            Samples = Samples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };
        attachments[1] = new AttachmentDescription // resolve target - this is what gets read back
        {
            Format = ColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        attachments[2] = new AttachmentDescription
        {
            Format = DepthFormat,
            Samples = Samples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var resolveRef = new AttachmentReference { Attachment = 1, Layout = ImageLayout.ColorAttachmentOptimal };
        var depthRef = new AttachmentReference { Attachment = 2, Layout = ImageLayout.DepthStencilAttachmentOptimal };

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PResolveAttachments = &resolveRef,
            PDepthStencilAttachment = &depthRef,
        };

        // The copy-out reads the resolve target, so the pass has to finish writing before transfer.
        var dependency = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.TransferBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };

        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 3,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };
        Check(vk.CreateRenderPass(device, in info, null, out var renderPass), "vkCreateRenderPass");
        return renderPass;
    }

    /// <summary>The one thing a draw binds beyond push constants: its material's diffuse texture.
    /// Every draw has one, including the grid (which gets the shared white texture).</summary>
    private static DescriptorSetLayout CreateDescriptorLayout(Vk vk, Device device)
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        Check(vk.CreateDescriptorSetLayout(device, in info, null, out var layout),
            "vkCreateDescriptorSetLayout");
        return layout;
    }

    /// <summary>
    /// One sampler for every texture: trilinear, repeating. Source content assumes wrapped UVs
    /// (tiling trims, wrapped body maps) and anisotropy is skipped because it is an optional device
    /// feature and this renderer enables none.
    /// </summary>
    private static Sampler CreateSampler(Vk vk, Device device)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MinLod = 0f,
            MaxLod = 1000f, // no clamp - each image's own level count is the real limit
            BorderColor = BorderColor.IntOpaqueBlack,
        };
        Check(vk.CreateSampler(device, in info, null, out var sampler), "vkCreateSampler");
        return sampler;
    }

    /// <summary>
    /// The pipelines, all off one shader pair and differing only in topology and state: triangles for
    /// the mesh, lines for the grid, and lines again for the skeleton overlay (blended, depth test
    /// off). Viewport and scissor are dynamic so a resized pane only rebuilds images, never pipelines.
    /// </summary>
    private static (PipelineLayout, Pipeline[] Triangles, Pipeline Lines, Pipeline XrayLines) CreatePipelines(
        Vk vk, Device device, RenderPass renderPass, DescriptorSetLayout descriptorLayout)
    {
        var vertModule = CreateShaderModule(vk, device, "mesh.vert.spv");
        var fragModule = CreateShaderModule(vk, device, "mesh.frag.spv");
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertModule,
            PName = entryPoint,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragModule,
            PName = entryPoint,
        };

        var binding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = (uint)sizeof(Vertex),
            InputRate = VertexInputRate.Vertex,
        };
        var attributes = stackalloc VertexInputAttributeDescription[3];
        attributes[0] = new VertexInputAttributeDescription
            { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
        attributes[1] = new VertexInputAttributeDescription
            { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = (uint)sizeof(Vector3) };
        attributes[2] = new VertexInputAttributeDescription
            { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = (uint)(sizeof(Vector3) * 2) };

        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 3,
            PVertexAttributeDescriptions = attributes,
        };

        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };

        // Rasterizer variants. Back-face culling is the engine's default; $nocull is what turns it
        // off, and it is common enough (foliage, decal sheets, cheap interiors) to be worth its own
        // pipeline rather than culling nothing and hoping.
        var rasterizers = stackalloc PipelineRasterizationStateCreateInfo[3];
        for (int i = 0; i < 3; i++)
        {
            rasterizers[i] = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = i == 0 ? CullModeFlags.BackBit : CullModeFlags.None,
                FrontFace = ModelFrontFace,
                LineWidth = 1f,
            };
        }

        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = Samples,
        };

        // Blended draws still test depth but must not write it, or the nearest one would occlude the
        // surfaces behind it that should show through. The extra entry past the blend modes tests no
        // depth at all - that is what makes the skeleton overlay an x-ray.
        var depthStencils = stackalloc PipelineDepthStencilStateCreateInfo[BlendModeCount + 1];
        for (int i = 0; i <= BlendModeCount; i++)
        {
            depthStencils[i] = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = i < BlendModeCount,
                DepthWriteEnable = i == (int)BlendMode.Opaque,
                DepthCompareOp = CompareOp.Less,
            };
        }

        // Alpha is never written, by either variant: the frame is read back into an opaque WPF
        // bitmap, so it has to stay at the clear value of 1. A $basetexture's alpha channel is a
        // mask (specular, blend), not coverage - writing it through would make the model see-through.
        const ColorComponentFlags WriteRgb =
            ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit;

        var blendAttachments = stackalloc PipelineColorBlendAttachmentState[BlendModeCount];
        blendAttachments[(int)BlendMode.Opaque] = new PipelineColorBlendAttachmentState
        {
            BlendEnable = false,
            ColorWriteMask = WriteRgb,
        };
        blendAttachments[(int)BlendMode.Translucent] = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.Zero,
            DstAlphaBlendFactor = BlendFactor.One,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = WriteRgb,
        };
        // $additive: straight sum, so black adds nothing and the draw order between additive parts
        // does not matter.
        blendAttachments[(int)BlendMode.Additive] = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.One,
            DstColorBlendFactor = BlendFactor.One,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.Zero,
            DstAlphaBlendFactor = BlendFactor.One,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = WriteRgb,
        };
        var blends = stackalloc PipelineColorBlendStateCreateInfo[BlendModeCount];
        for (int i = 0; i < BlendModeCount; i++)
        {
            blends[i] = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachments[i],
            };
        }

        var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamic = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };

        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(Push),
        };
        var setLayout = descriptorLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        Check(vk.CreatePipelineLayout(device, in layoutInfo, null, out var layout), "vkCreatePipelineLayout");

        var assemblies = stackalloc PipelineInputAssemblyStateCreateInfo[2];
        assemblies[0] = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };
        assemblies[1] = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.LineList,
        };

        // All in one call: the triangle variants in PipelineIndex order, then the grid's line list
        // (never culls, never blends), then the skeleton's (blended, no depth test).
        const int TriangleCount = BlendModeCount * 2;
        const int Count = TriangleCount + 2;
        var pipelineInfos = stackalloc GraphicsPipelineCreateInfo[Count];
        for (int i = 0; i < Count; i++)
        {
            bool lines = i >= TriangleCount;
            bool xray = i == TriangleCount + 1;
            int blend = xray ? (int)BlendMode.Translucent : lines ? (int)BlendMode.Opaque : i % BlendModeCount;
            bool noCull = lines || i >= BlendModeCount;
            pipelineInfos[i] = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &assemblies[lines ? 1 : 0],
                PViewportState = &viewportState,
                PRasterizationState = &rasterizers[noCull ? 1 : 0],
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencils[xray ? BlendModeCount : blend],
                PColorBlendState = &blends[blend],
                PDynamicState = &dynamic,
                Layout = layout,
                RenderPass = renderPass,
                Subpass = 0,
            };
        }

        var built = stackalloc Pipeline[Count];
        Check(vk.CreateGraphicsPipelines(device, default, Count, pipelineInfos, null, built),
            "vkCreateGraphicsPipelines");
        var triangles = new Pipeline[TriangleCount];
        for (int i = 0; i < TriangleCount; i++)
            triangles[i] = built[i];

        // Modules and the entry-point string are only needed while the pipelines are being built.
        vk.DestroyShaderModule(device, vertModule, null);
        vk.DestroyShaderModule(device, fragModule, null);
        SilkMarshal.Free((nint)entryPoint);
        return (layout, triangles, built[TriangleCount], built[TriangleCount + 1]);
    }

    /// <summary>Loads one of the embedded .spv blobs (see Rendering/Shaders/README.md).</summary>
    private static ShaderModule CreateShaderModule(Vk vk, Device device, string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded shader '{name}' is missing from the build");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var code = memory.ToArray();

        fixed (byte* p = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };
            Check(vk.CreateShaderModule(device, in info, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }

    // --- mesh upload ------------------------------------------------------------------------------

    /// <summary>
    /// Uploads a parsed mesh (and builds a ground grid scaled to it), replacing whatever was loaded
    /// before. The model is recentred on the origin here so the camera maths never has to care where
    /// the compiler happened to put it.
    /// </summary>
    /// <param name="materials">Resolved materials, indexed by <see cref="MeshPart.MaterialIndex"/>.
    /// Pass null or an empty list to draw the whole mesh untextured, as before.</param>
    public void SetMesh(StudioMesh mesh, IReadOnlyList<ModelMaterial>? materials = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ReleaseMeshBuffers();

        _meshCenter = (mesh.BoundsMin + mesh.BoundsMax) * 0.5f;
        var extent = mesh.BoundsMax - mesh.BoundsMin;
        _meshRadius = Math.Max(extent.Length() * 0.5f, 0.001f);

        var vertices = new Vertex[mesh.Positions.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new Vertex(mesh.Positions[i] - _meshCenter, mesh.Normals[i], mesh.TexCoords[i]);

        _vertexBuffer = CreateHostBuffer<Vertex>(vertices, BufferUsageFlags.VertexBufferBit, out _vertexMemory);
        _indexBuffer = CreateHostBuffer<uint>(
            Array.ConvertAll(mesh.Indices, i => (uint)i), BufferUsageFlags.IndexBufferBit, out _indexMemory);
        _indexCount = mesh.Indices.Length;

        // The grid sits at the mesh's own floor, not the origin - a model authored above its
        // compile origin (most props) would otherwise hover over a floor that isn't under it.
        float floor = mesh.BoundsMin.Z - _meshCenter.Z;
        var grid = BuildGrid(_meshRadius, floor, out _gridExtent, out _gridVertexCount);
        _gridBuffer = CreateHostBuffer<Vertex>(grid, BufferUsageFlags.VertexBufferBit, out _gridMemory);
        _axisFirstVertex = _gridVertexCount;

        var skeleton = BuildSkeleton(mesh.Bones, _meshCenter);
        _boneBuffer = CreateHostBuffer<Vertex>(skeleton, BufferUsageFlags.VertexBufferBit, out _boneMemory);
        _boneVertexCount = skeleton.Length;

        BuildDrawParts(mesh, materials);

        _log?.Invoke($"3D preview: uploaded {vertices.Length:N0} vertices / {mesh.TriangleCount:N0} triangles, "
                     + $"bounds {Fmt(mesh.BoundsMin)} .. {Fmt(mesh.BoundsMax)}");

        static string Fmt(Vector3 v) => $"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})";
    }

    /// <summary>Base tint for a material that draws no texture of its own - the same neutral grey the
    /// preview used before materials existed.</summary>
    private static readonly Vector4 UntexturedTint = new(0.78f, 0.79f, 0.81f, 1f);

    /// <summary>
    /// Uploads each material's texture and turns the mesh's parts into draw lists - opaque and
    /// translucent kept apart, because the translucent ones are re-sorted against the camera every
    /// frame and the opaque ones must all be drawn (and depth-written) before any of them.
    /// </summary>
    private void BuildDrawParts(StudioMesh mesh, IReadOnlyList<ModelMaterial>? materials)
    {
        BodyParts = mesh.BodyParts;
        _bodyGroupSelection = new int[mesh.BodyParts.Length];
        _mesh = mesh;
        _materials = materials;
        _skin = 0;

        EnsureFallbackTextures();
        CreateDescriptorPool(materials?.Count ?? 0);
        WriteDescriptor(_whiteTexture!);
        WriteDescriptor(_checkerTexture!);

        // Every material is uploaded, not just the ones skin 0 uses - switching skins must not have
        // to touch the GPU.
        _materialSets.Clear();
        for (int i = 0; materials is not null && i < materials.Count; i++)
        {
            if (materials[i].Diffuse is not { } diffuse)
                continue;
            var texture = CreateTextureFromVtf(diffuse);
            WriteDescriptor(texture);
            _textures.Add(texture);
            _materialSets[i] = texture.Set;
        }

        RebuildDrawParts();
    }

    /// <summary>
    /// Turns the mesh's parts into the two draw lists for the current skin. Cheap enough to redo
    /// whenever the skin changes - it allocates two arrays and uploads nothing.
    /// </summary>
    private void RebuildDrawParts()
    {
        if (_mesh is not { } mesh)
            return;
        var materials = _materials;

        var opaque = new List<DrawPart>();
        var translucent = new List<DrawPart>();
        foreach (var part in mesh.Parts)
        {
            int materialIndex = mesh.MaterialFor(part, _skin);
            var material = materials is not null && materialIndex >= 0 && materialIndex < materials.Count
                ? materials[materialIndex]
                : null;

            DescriptorSet set;
            Vector4 tint;
            if (_materialSets.TryGetValue(materialIndex, out var materialSet))
            {
                set = materialSet;
                tint = Vector4.One;
            }
            else if (material?.Fallback == MaterialFallback.Missing)
            {
                set = _checkerTexture!.Set;
                tint = Vector4.One; // the checkerboard supplies its own colour
            }
            else
            {
                set = _whiteTexture!.Set;
                tint = UntexturedTint;
            }

            bool noCull = material?.NoCull ?? false;
            var blend = material switch
            {
                { Additive: true } => BlendMode.Additive,
                { Translucent: true } => BlendMode.Translucent,
                _ => BlendMode.Opaque,
            };
            float opacity = material?.Opacity ?? 1f;
            var draw = new DrawPart(part.FirstIndex, part.IndexCount, set,
                tint with { W = opacity }, material?.AlphaTestReference ?? 0f,
                material?.Unlit == true ? 0f : 1f, PipelineIndex(noCull, blend),
                Centroid(mesh, part), part.BodyPart, part.Model);
            (blend == BlendMode.Opaque ? opaque : translucent).Add(draw);
        }

        _opaqueParts = opaque.ToArray();
        _translucentParts = translucent.ToArray();
    }

    /// <summary>The average of a part's referenced vertices, in the same recentred space the vertex
    /// buffer holds. Source sorts translucent studio meshes by exactly this sort of centroid.</summary>
    private Vector3 Centroid(StudioMesh mesh, MeshPart part)
    {
        var sum = Vector3.Zero;
        for (int i = part.FirstIndex; i < part.FirstIndex + part.IndexCount; i++)
            sum += mesh.Positions[mesh.Indices[i]];
        return sum / Math.Max(part.IndexCount, 1) - _meshCenter;
    }

    /// <summary>
    /// Shows one sub-model of a bodygroup, as the engine's <c>body</c> value would. Out-of-range
    /// arguments are ignored. Cheap - the draw loop simply skips parts that are not selected, so
    /// nothing is re-uploaded.
    /// </summary>
    public void SetBodyGroup(int bodyPart, int model)
    {
        if (bodyPart < 0 || bodyPart >= _bodyGroupSelection.Length)
            return;
        if (model < 0 || model >= BodyParts[bodyPart].Models.Length)
            return;
        _bodyGroupSelection[bodyPart] = model;
    }

    /// <summary>
    /// Switches $texturegroup skin, as the engine's <c>skin</c> value would. Rebuilds the draw list
    /// because a skin can change a part's blend mode and culling as well as its texture, but uploads
    /// nothing - every material's texture is already resident.
    /// </summary>
    public void SetSkin(int skin)
    {
        if (_mesh is null || skin < 0 || skin >= _mesh.SkinFamilies.Length || skin == _skin)
            return;
        _skin = skin;
        RebuildDrawParts();
    }

    /// Whether a part's bodygroup currently has it selected.
    private bool IsVisible(in DrawPart part) =>
        (uint)part.BodyPart >= (uint)_bodyGroupSelection.Length
        || _bodyGroupSelection[part.BodyPart] == part.Model;

    /// <summary>
    /// A square floor grid centred under the model, with the spacing rounded to a 1-2-5 step so the
    /// lines land on sensible unit intervals whatever the model's size, followed by the three origin
    /// axis lines (X, Y, Z - 2 vertices each) that the caller draws separately for their own colours.
    /// </summary>
    /// <param name="gridVertexCount">How many of the returned vertices are grid lines; the axes start
    /// at that index.</param>
    private static Vertex[] BuildGrid(float meshRadius, float floorZ, out float extent,
        out int gridVertexCount)
    {
        // Aim for roughly 16 cells across the model's footprint, snapped to a 1/2/5 x 10^n step.
        float ideal = meshRadius * 2.5f / 16f;
        float magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(Math.Max(ideal, 0.001f))));
        float normalized = ideal / magnitude;
        float step = magnitude * (normalized < 1.5f ? 1f : normalized < 3.5f ? 2f : normalized < 7.5f ? 5f : 10f);

        const int Cells = 16; // per side of centre
        extent = step * Cells;

        var vertices = new List<Vertex>((Cells * 2 + 1) * 4 + 6);
        var flat = Vector3.Zero; // normal unused - the grid draws with shading mixed out
        for (int i = -Cells; i <= Cells; i++)
        {
            float at = i * step;
            vertices.Add(new Vertex(new Vector3(at, -extent, floorZ), flat));
            vertices.Add(new Vertex(new Vector3(at, extent, floorZ), flat));
            vertices.Add(new Vertex(new Vector3(-extent, at, floorZ), flat));
            vertices.Add(new Vertex(new Vector3(extent, at, floorZ), flat));
        }
        gridVertexCount = vertices.Count;

        // Origin gizmo: two grid cells along each axis, lifted a hair off the floor so it wins the
        // depth test against the grid lines it sits on top of.
        float axis = step * 2f;
        float lift = step * 0.004f;
        var origin = new Vector3(0, 0, floorZ + lift);
        vertices.Add(new Vertex(origin, flat));
        vertices.Add(new Vertex(origin + new Vector3(axis, 0, 0), flat));
        vertices.Add(new Vertex(origin, flat));
        vertices.Add(new Vertex(origin + new Vector3(0, axis, 0), flat));
        vertices.Add(new Vertex(origin, flat));
        vertices.Add(new Vertex(origin + new Vector3(0, 0, axis), flat));

        return vertices.ToArray();
    }

    /// <summary>How wide an octahedron's waist is as a fraction of its bone's length, and how far along
    /// the bone that waist sits. Blender's armature display uses a tenth for both and it reads well at
    /// any scale, which is the whole reason the shape is drawn proportional rather than at a fixed
    /// size - a Source skeleton mixes finger bones with spine bones in one model.</summary>
    private const float BoneWaistFraction = 0.1f;

    /// <summary>
    /// The skeleton as a line list: one octahedron per bone, spanning from its parent's joint to its
    /// own, in the same recentred space the mesh vertices are uploaded in. Bones whose parent sits on
    /// top of them (helper and attachment bones, which Source has plenty of) have no direction to draw
    /// along and are skipped; so are roots, which have no parent joint to start from.
    /// <para>
    /// Wireframe rather than solid: an x-ray of a few hundred overlapping filled octahedra is mush,
    /// and lines need neither shading nor a sort.
    /// </para>
    /// </summary>
    private static Vertex[] BuildSkeleton(StudioBone[] bones, Vector3 center)
    {
        var lines = new List<Vertex>(bones.Length * 24);
        var flat = Vector3.Zero; // normal unused - the overlay draws with shading mixed out

        void Line(Vector3 a, Vector3 b)
        {
            lines.Add(new Vertex(a, flat));
            lines.Add(new Vertex(b, flat));
        }

        var ring = new Vector3[4];
        foreach (var bone in bones)
        {
            if ((uint)bone.Parent >= (uint)bones.Length)
                continue;
            var head = bones[bone.Parent].Position - center;
            var tail = bone.Position - center;
            var axis = tail - head;
            float length = axis.Length();
            if (length < 1e-4f)
                continue;
            axis /= length;

            // Any two perpendiculars will do - the shape is symmetric about its axis - so long as the
            // seed is not parallel to it, which is what the Z/X switch avoids.
            var side = Vector3.Normalize(Vector3.Cross(
                axis, MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
            var other = Vector3.Cross(axis, side);

            float half = length * BoneWaistFraction;
            var waist = head + axis * half;
            ring[0] = waist + side * half;
            ring[1] = waist + other * half;
            ring[2] = waist - side * half;
            ring[3] = waist - other * half;

            for (int i = 0; i < 4; i++)
            {
                Line(head, ring[i]);
                Line(ring[i], ring[(i + 1) % 4]);
                Line(ring[i], tail);
            }
        }

        return lines.ToArray();
    }

    /// <summary>
    /// Allocates a host-visible buffer and memcpys the data straight in.
    /// <para>
    /// <paramref name="forHostReads"/> picks the memory type, and it matters enormously. The default
    /// (coherent) is right for data the CPU only ever <b>writes</b> - vertices, indices - because
    /// coherent memory is typically write-combined, which streams writes fast. Reading it back is the
    /// opposite story: WC reads bypass the cache and run at a small fraction of bus speed, which for a
    /// full-frame readback is the difference between a millisecond and sixty. Buffers the CPU reads
    /// therefore ask for HOST_CACHED, falling back to coherent only if the device has nothing cached.
    /// </para>
    /// </summary>
    private Buffer CreateHostBuffer<T>(T[] data, BufferUsageFlags usage, out DeviceMemory memory,
        bool forHostReads = false)
        where T : unmanaged
    {
        ulong size = (ulong)(data.Length * sizeof(T));
        if (size == 0)
            size = 1; // a zero-sized buffer is invalid; an empty grid/mesh simply never gets drawn

        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Check(_vk.CreateBuffer(_device, in info, null, out var buffer), "vkCreateBuffer");

        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        uint memoryType = forHostReads
            ? TryFindMemoryType(requirements.MemoryTypeBits,
                  MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit)
              ?? FindMemoryType(requirements.MemoryTypeBits,
                  MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
            : FindMemoryType(requirements.MemoryTypeBits,
                  MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryType,
        };
        Check(_vk.AllocateMemory(_device, in allocInfo, null, out memory), "vkAllocateMemory");
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");

        if (data.Length > 0)
        {
            void* mapped;
            Check(_vk.MapMemory(_device, memory, 0, size, 0, &mapped), "vkMapMemory");
            fixed (T* source = data)
                System.Buffer.MemoryCopy(source, mapped, size, size);
            _vk.UnmapMemory(_device, memory);
        }
        return buffer;
    }

    // --- textures ---------------------------------------------------------------------------------

    /// <summary>Side of the generated checkerboard, and of one of its squares. The engine's missing
    /// material is a coarse magenta/black check, which is exactly what makes it unmissable.</summary>
    private const int CheckerSize = 64, CheckerSquare = 8;

    /// <summary>Creates the shared white and checkerboard textures on first use. They outlive any one
    /// mesh - only their descriptor sets are reallocated, when the pool is rebuilt.</summary>
    private void EnsureFallbackTextures()
    {
        _whiteTexture ??= CreateTexture([255, 255, 255, 255], 1, 1);

        if (_checkerTexture is not null)
            return;
        var pixels = new byte[CheckerSize * CheckerSize * 4];
        for (int y = 0; y < CheckerSize; y++)
        {
            for (int x = 0; x < CheckerSize; x++)
            {
                bool magenta = (x / CheckerSquare + y / CheckerSquare) % 2 == 0;
                int at = (y * CheckerSize + x) * 4;
                pixels[at] = magenta ? (byte)255 : (byte)0;     // B
                pixels[at + 1] = 0;                             // G
                pixels[at + 2] = magenta ? (byte)255 : (byte)0; // R
                pixels[at + 3] = 255;
            }
        }
        _checkerTexture = CreateTexture(pixels, CheckerSize, CheckerSize);
    }

    private PreviewTexture CreateTextureFromVtf(VtfImage image) =>
        CreateTexture(image.Bgra, image.Width, image.Height);

    /// <summary>
    /// Uploads BGRA pixels into a sampled image with a full mip chain, built by successive linear
    /// blits. Everything runs through one submit-and-wait: this is called while a model is being
    /// loaded, never inside the frame loop.
    /// </summary>
    private PreviewTexture CreateTexture(byte[] bgra, int width, int height)
    {
        uint mipLevels = _canGenerateMipmaps
            ? (uint)(Math.Floor(Math.Log2(Math.Max(width, height))) + 1)
            : 1;

        var staging = CreateHostBuffer<byte>(bgra, BufferUsageFlags.TransferSrcBit, out var stagingMemory);
        var texture = new PreviewTexture();
        texture.Image = CreateImage(width, height, ColorFormat, SampleCountFlags.Count1Bit,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
            out texture.Memory, mipLevels);

        BeginOneShot();

        Transition(texture.Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, mipLevels);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1,
            },
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        _vk.CmdCopyBufferToImage(_commandBuffer, staging, texture.Image,
            ImageLayout.TransferDstOptimal, 1, in region);

        GenerateMipmaps(texture.Image, width, height, mipLevels);

        EndOneShot();

        _vk.DestroyBuffer(_device, staging, null);
        _vk.FreeMemory(_device, stagingMemory, null);

        texture.View = CreateImageView(texture.Image, ColorFormat, ImageAspectFlags.ColorBit, mipLevels);
        return texture;
    }

    /// <summary>
    /// Halves mip 0 down the chain with linear blits, leaving every level in SHADER_READ_ONLY. With a
    /// single level this is just the final transition.
    /// </summary>
    private void GenerateMipmaps(Image image, int width, int height, uint mipLevels)
    {
        int mipWidth = width, mipHeight = height;
        for (uint level = 1; level < mipLevels; level++)
        {
            Transition(image, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, level - 1, 1);

            int nextWidth = Math.Max(1, mipWidth / 2), nextHeight = Math.Max(1, mipHeight / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit, MipLevel = level - 1,
                    BaseArrayLayer = 0, LayerCount = 1,
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit, MipLevel = level,
                    BaseArrayLayer = 0, LayerCount = 1,
                },
            };
            blit.SrcOffsets.Element1 = new Offset3D(mipWidth, mipHeight, 1);
            blit.DstOffsets.Element1 = new Offset3D(nextWidth, nextHeight, 1);
            _vk.CmdBlitImage(_commandBuffer, image, ImageLayout.TransferSrcOptimal,
                image, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);

            Transition(image, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, level - 1, 1);
            mipWidth = nextWidth;
            mipHeight = nextHeight;
        }
        // The last level was never blitted from, so it is still a transfer destination.
        Transition(image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, mipLevels - 1, 1);
    }

    /// <summary>An image layout transition over a mip range, with access masks inferred from the two
    /// layouts. Only the three transfer/sample transitions this renderer performs are handled.</summary>
    private void Transition(Image image, ImageLayout from, ImageLayout to, uint baseMip, uint mipCount)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = baseMip, LevelCount = mipCount,
                BaseArrayLayer = 0, LayerCount = 1,
            },
            SrcAccessMask = from switch
            {
                ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
                _ => 0,
            },
            DstAccessMask = to switch
            {
                ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
                ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
                _ => AccessFlags.ShaderReadBit,
            },
        };
        var stage = to == ImageLayout.ShaderReadOnlyOptimal
            ? PipelineStageFlags.FragmentShaderBit
            : PipelineStageFlags.TransferBit;
        _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, stage,
            0, 0, null, 0, null, 1, in barrier);
    }

    private void BeginOneShot()
    {
        Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(_vk.BeginCommandBuffer(_commandBuffer, in begin), "vkBeginCommandBuffer");
    }

    private void EndOneShot()
    {
        Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");
        var commandBuffer = _commandBuffer;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        Check(_vk.QueueSubmit(_queue, 1, in submit, default), "vkQueueSubmit");
        Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");
    }

    // --- descriptors ------------------------------------------------------------------------------

    /// <summary>Rebuilds the pool for a new mesh, sized for its materials plus the two fallbacks.
    /// Recreating is simpler than freeing sets individually and costs nothing at this scale.</summary>
    private void CreateDescriptorPool(int materialCount)
    {
        if (_descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);

        uint max = (uint)materialCount + 2;
        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = max,
        };
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = max,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        Check(_vk.CreateDescriptorPool(_device, in info, null, out _descriptorPool), "vkCreateDescriptorPool");
    }

    /// Allocates a set from the current pool and points it at the texture's image view.
    private void WriteDescriptor(PreviewTexture texture)
    {
        var layout = _descriptorLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        Check(_vk.AllocateDescriptorSets(_device, in allocInfo, out texture.Set), "vkAllocateDescriptorSets");

        var imageInfo = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = texture.View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = texture.Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo,
        };
        _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
        => TryFindMemoryType(typeBits, required)
           ?? throw new InvalidOperationException($"no Vulkan memory type with {required}");

    private uint? TryFindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physical, out var properties);
        for (uint i = 0; i < properties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0)
                continue;
            if ((properties.MemoryTypes[(int)i].PropertyFlags & required) == required)
                return i;
        }
        return null;
    }

    // --- rendering --------------------------------------------------------------------------------

    /// <summary>
    /// Draws the loaded mesh and returns the frame as tightly packed BGRA rows (<c>width * 4</c>
    /// stride), ready for <c>WriteableBitmap.WritePixels</c> with <c>PixelFormats.Bgra32</c>. Returns
    /// null when there is nothing loaded or the size is degenerate.
    /// <para>
    /// The array is <b>reused</b> between calls - copy what you need out of it before rendering
    /// again, and never hand it to something that keeps the reference (a frozen BitmapSource does).
    /// </para>
    /// </summary>
    /// <param name="yaw">Orbit angle around the model's up axis, radians.</param>
    /// <param name="pitch">Elevation, radians, clamped by the caller to just under +-90 degrees.</param>
    /// <param name="zoom">Distance multiplier: 1 frames the whole model, smaller moves in.</param>
    /// <param name="pan">Where the camera is looking, sideways and up, as a fraction of the model's
    /// default framing distance. Both eye and target move together, so panning slides the model
    /// without turning it, and <b>zoom does not move the pan point</b> - scaling this by the zoomed
    /// distance instead would drag the view back towards the origin every time you zoomed in.</param>
    public byte[]? Render(int width, int height, float yaw, float pitch, float zoom, Vector2 pan = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0 || _indexCount == 0)
            return null;

        EnsureTargets(width, height);

        // Source is Z-up. The eye orbits the (recentred) model on a sphere whose radius covers the
        // mesh and its grid, so nothing clips out of frame at the default framing.
        float framing = Math.Max(_meshRadius, _gridExtent * 0.35f) * 2.6f;
        float distance = framing * zoom;
        var eye = new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch)) * distance;

        // Pan slides along the screen's own axes, so dragging always moves the model the way the
        // cursor went whatever the orbit is.
        var forward = Vector3.Normalize(-eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var target = (right * pan.X + Vector3.Cross(right, forward) * pan.Y) * framing;

        var view = Matrix4x4.CreateLookAt(eye + target, target, Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, (float)width / height,
            Math.Max(distance * 0.01f, 0.05f),
            distance + _gridExtent * 3f + _meshRadius * 4f + target.Length());
        projection.M22 *= -1f; // Vulkan clip space has +Y down

        // System.Numerics is row-vector (v * M) and stores its fields row by row; GLSL reads a mat4
        // from the same bytes column-major, which transposes it on the way in. The two cancel, so the
        // product is uploaded exactly as it comes out - transposing here would undo the fix.
        // Key light follows the orbit: same azimuth as the eye plus a fixed swing, at a fixed
        // elevation. Which way the mesh faces is a compile-time property we cannot know, so anchoring
        // the light to the camera instead of the world is the only rig that is never backlit.
        float keyYaw = yaw + KeyLightYawOffset;
        var key = new Vector4(
            MathF.Cos(KeyLightPitch) * MathF.Cos(keyYaw),
            MathF.Cos(KeyLightPitch) * MathF.Sin(keyYaw),
            MathF.Sin(KeyLightPitch), 0f);

        // Source draws translucent studio meshes back to front by their centroid's distance to the
        // eye, and so does this. Per mesh, not per triangle - two translucent surfaces inside one
        // mesh can still order wrong, exactly as they do in the engine.
        if (_translucentParts.Length > 1)
        {
            // Centroids are in the model's own (unrotated) space, so the eye is brought back into it
            // rather than rotating every centroid.
            var from = Vector3.Transform(eye + target, ModelOrientationInverse);
            Array.Sort(_translucentParts, (a, b) =>
                Vector3.DistanceSquared(b.Center, from).CompareTo(Vector3.DistanceSquared(a.Center, from)));
        }

        var timer = Stopwatch.StartNew();
        RecordAndSubmit(width, height, view * projection, key);
        var pixels = ReadPixels(width, height);
        LastFrameMilliseconds = timer.Elapsed.TotalMilliseconds;
        return pixels;
    }

    private void RecordAndSubmit(int width, int height, Matrix4x4 mvp, Vector4 key)
    {
        Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(_vk.BeginCommandBuffer(_commandBuffer, in begin), "vkBeginCommandBuffer");

        var clears = stackalloc ClearValue[3];
        clears[0] = new ClearValue { Color = new ClearColorValue(0.129f, 0.137f, 0.153f, 1f) }; // panel grey
        clears[1] = default; // resolve target - never loaded
        clears[2] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };

        var passBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height)),
            ClearValueCount = 3,
            PClearValues = clears,
        };
        _vk.CmdBeginRenderPass(_commandBuffer, in passBegin, SubpassContents.Inline);

        var viewport = new Viewport
        {
            X = 0, Y = 0, Width = width, Height = height, MinDepth = 0f, MaxDepth = 1f,
        };
        _vk.CmdSetViewport(_commandBuffer, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));
        _vk.CmdSetScissor(_commandBuffer, 0, 1, in scissor);

        ulong zeroOffset = 0;

        // The model and its skeleton draw turned a quarter turn (see ModelOrientation); the grid and
        // gizmo are the world reference and stay put, which is what makes the turn visible at all.
        var modelMvp = ModelOrientation * mvp;

        // Grid and the origin gizmo: flat lines out of one buffer, depth-tested against the mesh that
        // follows. Four draws rather than a per-vertex colour attribute - the colour is a push
        // constant, and four draws of a handful of lines cost nothing.
        if (_gridVertexCount > 0)
        {
            _vk.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Graphics, _linePipeline);
            var gridBuffer = _gridBuffer;
            _vk.CmdBindVertexBuffers(_commandBuffer, 0, 1, in gridBuffer, in zeroOffset);
            BindTexture(_whiteTexture!.Set);

            var push = new Push { Mvp = mvp, Color = new Vector4(0.30f, 0.32f, 0.36f, 1f), Key = key };
            _vk.CmdPushConstants(_commandBuffer, _pipelineLayout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(Push), &push);
            _vk.CmdDraw(_commandBuffer, (uint)_gridVertexCount, 1, 0, 0);

            for (int i = 0; i < AxisColors.Length; i++)
            {
                push.Color = AxisColors[i];
                _vk.CmdPushConstants(_commandBuffer, _pipelineLayout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(Push), &push);
                _vk.CmdDraw(_commandBuffer, 2, 1, (uint)(_axisFirstVertex + i * 2), 0);
            }
        }

        // Mesh: one draw per material. Every opaque part first (they must have written depth before
        // anything blends over them), then the translucent ones farthest-first.
        {
            var vertexBuffer = _vertexBuffer;
            _vk.CmdBindVertexBuffers(_commandBuffer, 0, 1, in vertexBuffer, in zeroOffset);
            _vk.CmdBindIndexBuffer(_commandBuffer, _indexBuffer, 0, IndexType.Uint32);

            int boundPipeline = -1;
            foreach (var part in _opaqueParts)
                Draw(in part);
            foreach (var part in _translucentParts)
                Draw(in part);

            void Draw(in DrawPart part)
            {
                if (!IsVisible(in part))
                    return;
                if (part.Pipeline != boundPipeline)
                {
                    _vk.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Graphics,
                        _trianglePipelines[part.Pipeline]);
                    boundPipeline = part.Pipeline;
                }
                BindTexture(part.Texture);

                var push = new Push
                {
                    Mvp = modelMvp,
                    Color = part.Color,
                    Key = key with { W = part.AlphaTestReference },
                    Params = new Vector4(part.Shading, 0f, 0f, 0f),
                };
                _vk.CmdPushConstants(_commandBuffer, _pipelineLayout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(Push), &push);
                _vk.CmdDrawIndexed(_commandBuffer, (uint)part.IndexCount, 1, (uint)part.FirstIndex, 0, 0);
            }
        }

        // Skeleton overlay: last, so it blends over the finished model, and with no depth test at all
        // so bones inside the mesh still show. Deliberately near-grey - it is a reference overlay, not
        // a part of the model, and colouring it would compete with the axis gizmo.
        if (ShowSkeleton && _boneVertexCount > 0)
        {
            _vk.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Graphics, _xrayLinePipeline);
            var boneBuffer = _boneBuffer;
            _vk.CmdBindVertexBuffers(_commandBuffer, 0, 1, in boneBuffer, in zeroOffset);
            BindTexture(_whiteTexture!.Set);

            var push = new Push { Mvp = modelMvp, Color = SkeletonColor, Key = key };
            _vk.CmdPushConstants(_commandBuffer, _pipelineLayout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(Push), &push);
            _vk.CmdDraw(_commandBuffer, (uint)_boneVertexCount, 1, 0, 0);
        }

        _vk.CmdEndRenderPass(_commandBuffer);

        // The resolve target already ends the pass in TRANSFER_SRC layout (see CreateRenderPass).
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0, // tightly packed
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        _vk.CmdCopyImageToBuffer(_commandBuffer, _colorResolve, ImageLayout.TransferSrcOptimal,
            _readback, 1, in region);

        // Make the copy visible to the host read that follows the queue wait.
        var hostBarrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
        };
        _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit,
            0, 1, in hostBarrier, 0, null, 0, null);

        Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

        var commandBuffer = _commandBuffer;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        Check(_vk.QueueSubmit(_queue, 1, in submit, default), "vkQueueSubmit");
        // ponytail: one submit, one wait - no fences or frames in flight. A preview redraws on user
        // input at a few hundred pixels; add pipelining only if the editor viewport ever needs it.
        Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");
    }

    private void BindTexture(DescriptorSet set) =>
        _vk.CmdBindDescriptorSets(_commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, in set, 0, null);

    private byte[] ReadPixels(int width, int height)
    {
        // Cached memory is not necessarily coherent, so the GPU's writes have to be pulled into the
        // CPU's view before reading. Harmless (and cheap) on the coherent fallback.
        var range = new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = _readbackMemory,
            Offset = 0,
            Size = Vk.WholeSize,
        };
        Check(_vk.InvalidateMappedMemoryRanges(_device, 1, in range), "vkInvalidateMappedMemoryRanges");

        new ReadOnlySpan<byte>(_readbackMapped, width * height * 4).CopyTo(_pixels);
        return _pixels;
    }

    // --- offscreen targets --------------------------------------------------------------------------

    /// <summary>Rebuilds the colour/depth/readback set when the requested size changes; a no-op at the
    /// same size, which is the common case while orbiting.</summary>
    private void EnsureTargets(int width, int height)
    {
        if (width == _targetWidth && height == _targetHeight)
            return;

        ReleaseTargets();
        _targetWidth = width;
        _targetHeight = height;

        _colorMsaa = CreateImage(width, height, ColorFormat, Samples,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
            out _colorMsaaMemory);
        _colorResolve = CreateImage(width, height, ColorFormat, SampleCountFlags.Count1Bit,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, out _colorResolveMemory);
        _depth = CreateImage(width, height, DepthFormat, Samples,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
            out _depthMemory);

        _colorMsaaView = CreateImageView(_colorMsaa, ColorFormat, ImageAspectFlags.ColorBit);
        _colorResolveView = CreateImageView(_colorResolve, ColorFormat, ImageAspectFlags.ColorBit);
        _depthView = CreateImageView(_depth, DepthFormat, ImageAspectFlags.DepthBit);

        var views = stackalloc ImageView[3] { _colorMsaaView, _colorResolveView, _depthView };
        var framebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 3,
            PAttachments = views,
            Width = (uint)width,
            Height = (uint)height,
            Layers = 1,
        };
        Check(_vk.CreateFramebuffer(_device, in framebufferInfo, null, out _framebuffer), "vkCreateFramebuffer");

        _readback = CreateHostBuffer<byte>(new byte[width * height * 4], BufferUsageFlags.TransferDstBit,
            out _readbackMemory, forHostReads: true);
        _pixels = new byte[width * height * 4];

        // Mapped once and left mapped for the target's lifetime - a live preview would otherwise pay
        // for a map/unmap pair every single frame.
        void* mapped;
        Check(_vk.MapMemory(_device, _readbackMemory, 0, Vk.WholeSize, 0, &mapped), "vkMapMemory");
        _readbackMapped = mapped;
        // Deliberately not logged: this runs on every size change, so dragging the pane splitter would
        // fill the console. The live size is on the preview's own readout instead.
    }

    private Image CreateImage(int width, int height, Format format, SampleCountFlags samples,
        ImageUsageFlags usage, out DeviceMemory memory, uint mipLevels = 1)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = mipLevels,
            ArrayLayers = 1,
            Samples = samples,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(_vk.CreateImage(_device, in info, null, out var image), "vkCreateImage");

        _vk.GetImageMemoryRequirements(_device, image, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(_vk.AllocateMemory(_device, in allocInfo, null, out memory), "vkAllocateMemory");
        Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory");
        return image;
    }

    private ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspect,
        uint mipLevels = 1)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect, BaseMipLevel = 0, LevelCount = mipLevels,
                BaseArrayLayer = 0, LayerCount = 1,
            },
        };
        Check(_vk.CreateImageView(_device, in info, null, out var view), "vkCreateImageView");
        return view;
    }

    // --- teardown -----------------------------------------------------------------------------------

    private void ReleaseTargets()
    {
        if (_targetWidth == 0)
            return;
        _vk.DestroyFramebuffer(_device, _framebuffer, null);
        _vk.DestroyImageView(_device, _colorMsaaView, null);
        _vk.DestroyImageView(_device, _colorResolveView, null);
        _vk.DestroyImageView(_device, _depthView, null);
        _vk.DestroyImage(_device, _colorMsaa, null);
        _vk.DestroyImage(_device, _colorResolve, null);
        _vk.DestroyImage(_device, _depth, null);
        _vk.FreeMemory(_device, _colorMsaaMemory, null);
        _vk.FreeMemory(_device, _colorResolveMemory, null);
        _vk.FreeMemory(_device, _depthMemory, null);
        if (_readbackMapped is not null)
        {
            _vk.UnmapMemory(_device, _readbackMemory);
            _readbackMapped = null;
        }
        _vk.DestroyBuffer(_device, _readback, null);
        _vk.FreeMemory(_device, _readbackMemory, null);
        _targetWidth = _targetHeight = 0;
    }

    private void ReleaseMeshBuffers()
    {
        if (_indexCount == 0 && _gridVertexCount == 0)
            return;
        _vk.DeviceWaitIdle(_device);
        _vk.DestroyBuffer(_device, _vertexBuffer, null);
        _vk.FreeMemory(_device, _vertexMemory, null);
        _vk.DestroyBuffer(_device, _indexBuffer, null);
        _vk.FreeMemory(_device, _indexMemory, null);
        _vk.DestroyBuffer(_device, _gridBuffer, null);
        _vk.FreeMemory(_device, _gridMemory, null);
        _vk.DestroyBuffer(_device, _boneBuffer, null);
        _vk.FreeMemory(_device, _boneMemory, null);
        foreach (var texture in _textures)
            DestroyTexture(texture);
        _textures.Clear();
        _materialSets.Clear();
        _mesh = null;
        _materials = null;
        _skin = 0;
        _opaqueParts = [];
        _translucentParts = [];
        _indexCount = 0;
        _gridVertexCount = 0;
        _boneVertexCount = 0;
    }

    private void DestroyTexture(PreviewTexture texture)
    {
        _vk.DestroyImageView(_device, texture.View, null);
        _vk.DestroyImage(_device, texture.Image, null);
        _vk.FreeMemory(_device, texture.Memory, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _vk.DeviceWaitIdle(_device);
        ReleaseMeshBuffers();
        ReleaseTargets();
        if (_whiteTexture is not null) DestroyTexture(_whiteTexture);
        if (_checkerTexture is not null) DestroyTexture(_checkerTexture);
        if (_descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroySampler(_device, _sampler, null);
        _vk.DestroyDescriptorSetLayout(_device, _descriptorLayout, null);
        foreach (var pipeline in _trianglePipelines)
            _vk.DestroyPipeline(_device, pipeline, null);
        _vk.DestroyPipeline(_device, _linePipeline, null);
        _vk.DestroyPipeline(_device, _xrayLinePipeline, null);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk.DestroyRenderPass(_device, _renderPass, null);
        _vk.DestroyCommandPool(_device, _commandPool, null);
        _vk.DestroyDevice(_device, null);
        _vk.DestroyInstance(_instance, null);
        _vk.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Check(Result result, string call)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"{call} failed: {result}");
    }
}
