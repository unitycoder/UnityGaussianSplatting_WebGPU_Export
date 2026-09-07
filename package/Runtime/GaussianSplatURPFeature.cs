// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting.Runtime
{
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        [Header("Gaussian Splat Fixed Resolution Override")]
        [SerializeField] bool m_OverrideResolution = false;
        [SerializeField] int m_MaxSize = 1280;

        [Header("Camera Filtering")]
        [SerializeField] bool m_RenderOnlyMainCamera = false;

        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";
            const string GaussianMotionRTName = "_GaussianSplatMotionRT";
            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
                internal TextureHandle GaussianSplatMotionRT;
                internal int GaussianSplatWidth;
                internal int GaussianSplatHeight;
                internal bool Stereo;
                internal bool ColorIsArray, DepthIsArray;
                internal RenderTextureDescriptor SplatDescriptor;
                internal readonly Matrix4x4[] Views = new Matrix4x4[2];
                internal readonly Matrix4x4[] Projections = new Matrix4x4[2];
                internal readonly Matrix4x4[] PreviousViews = new Matrix4x4[2];
                internal readonly Matrix4x4[] PreviousProjections = new Matrix4x4[2];
            }

            sealed class EyeHistory
            {
                internal int frame = -1;
                internal Matrix4x4 view, projection, previousView, previousProjection;
            }
            readonly Dictionary<(Camera, int), EyeHistory> m_EyeHistory = new();

            public GSRenderPass()
            {
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var settings = GaussianSplatSettings.instance;
                var usingRT = !settings.isDebugRender;
                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                passData.CameraData = cameraData;
                passData.Stereo = cameraData.xr.enabled;
                passData.GaussianSplatWidth = cameraData.cameraTargetDescriptor.width;
                passData.GaussianSplatHeight = cameraData.cameraTargetDescriptor.height;
                passData.ColorIsArray = renderGraph.GetTextureDesc(resourceData.activeColorTexture).dimension == TextureDimension.Tex2DArray;
                passData.DepthIsArray = renderGraph.GetTextureDesc(resourceData.activeDepthTexture).dimension == TextureDimension.Tex2DArray;
                if (passData.Stereo)
                {
                    for (int view = 0; view < cameraData.xr.viewCount; ++view)
                    {
                        int eye = cameraData.xr.singlePassEnabled ? view : cameraData.xr.multipassId;
                        var key = (cameraData.camera, eye);
                        if (!m_EyeHistory.TryGetValue(key, out var history))
                            m_EyeHistory.Add(key, history = new EyeHistory());
                        var currentView = cameraData.xr.GetViewMatrix(view);
                        var currentProjection = GL.GetGPUProjectionMatrix(cameraData.xr.GetProjMatrix(view), true);
                        if (history.frame != Time.frameCount)
                        {
                            bool consecutive = history.frame == Time.frameCount - 1;
                            history.previousView = consecutive ? history.view : currentView;
                            history.previousProjection = consecutive ? history.projection : currentProjection;
                            history.frame = Time.frameCount;
                        }
                        history.view = currentView;
                        history.projection = currentProjection;
                        passData.Views[eye] = currentView;
                        passData.Projections[eye] = currentProjection;
                        passData.PreviousViews[eye] = history.previousView;
                        passData.PreviousProjections[eye] = history.previousProjection;
                    }
                }

                if (usingRT)
                {
                    RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                    rtDesc.depthBufferBits = 0;
                    // Match the scene depth attachment, including when XR MSAA is enabled.
                    rtDesc.msaaSamples = passData.Stereo ? (int)renderGraph.GetTextureDesc(resourceData.activeDepthTexture).msaaSamples : 1;
                    rtDesc.bindMS = false;
                    if (passData.Stereo)
                    {
                        rtDesc.dimension = TextureDimension.Tex2DArray;
                        rtDesc.volumeDepth = 2;
                        rtDesc.vrUsage = VRTextureUsage.None; // slices are bound explicitly
                    }
                    rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

                    passData.SplatDescriptor = rtDesc;
                    var colorHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true);
                    passData.GaussianSplatRT = colorHandle;
                    passData.GaussianSplatWidth = rtDesc.width;
                    passData.GaussianSplatHeight = rtDesc.height;
                    builder.UseTexture(colorHandle, AccessFlags.ReadWrite);

                    // create a motion target (RG16 float) used by temporal filter
                    var motionDesc = rtDesc;
                    motionDesc.depthBufferBits = 0;
                    motionDesc.graphicsFormat = GraphicsFormat.R16G16_SFloat;
                    var motionHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, motionDesc, GaussianMotionRTName, true);
                    passData.GaussianSplatMotionRT = motionHandle;
                    builder.UseTexture(motionHandle, AccessFlags.ReadWrite);
                }
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var system = GaussianSplatRenderSystem.instance;
                    system.EnsureMaterials();
                    var matComposite = system.m_MatComposite;
                    if (matComposite == null)
                        return;

                    var settings = GaussianSplatSettings.instance;
                    var usingRT = !settings.isDebugRender;

                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);

                    if (data.Stereo)
                    {
                        RenderXR(data, commandBuffer, system, matComposite);
                        return;
                    }
                    commandBuffer.SetGlobalInt("_SplatStereoEnabled", 0);
                    if (usingRT)
                    {
                        // bind both color and motion targets as global textures
                        commandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatRT, data.GaussianSplatRT);
                        commandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatMotionRT, data.GaussianSplatMotionRT);
                        
                        if (settings.m_TemporalFilter != TemporalFilter.None)
                        {
                            // Render to both color and motion RTs
                            CoreUtils.SetRenderTarget(commandBuffer,
                                new RenderTargetIdentifier[] { data.GaussianSplatRT, data.GaussianSplatMotionRT },
                                data.SourceDepth, ClearFlag.None);
                        }
                        else
                        {
                            // Render only to color RT
                            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, data.SourceDepth, ClearFlag.None);
                        }
                    }
                    else
                    {
                        CoreUtils.SetRenderTarget(commandBuffer, data.SourceTexture, data.SourceDepth, ClearFlag.None);
                    }
                    system.RenderAllSplats(data.CameraData.camera, commandBuffer);
                    if (usingRT)
                    {
                        commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                        if (settings.m_TemporalFilter != TemporalFilter.None)
                        {
                            // use temporal filter to composite; pass the render graph texture handles directly
                            system.GetTemporalFilter().Render(commandBuffer, data.CameraData.camera, matComposite, 1,
                                data.GaussianSplatRT, data.SourceTexture,
                                data.GaussianSplatWidth, data.GaussianSplatHeight,
                                settings.m_FrameInfluence, settings.m_VarianceClampScale,
                                data.GaussianSplatMotionRT);
                        }
                        else
                        {
                            Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                        }
                         commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                    }
                });
            }

            static RenderTargetIdentifier Slice(RenderTargetIdentifier texture, int slice)
                => new RenderTargetIdentifier(texture, 0, CubemapFace.Unknown, slice);

            static void BindEyeTargets(CommandBuffer cmd, RenderTargetIdentifier[] colors, RenderTargetIdentifier depth)
            {
                var loads = new RenderBufferLoadAction[colors.Length];
                var stores = new RenderBufferStoreAction[colors.Length];
                for (int i = 0; i < colors.Length; ++i)
                {
                    loads[i] = RenderBufferLoadAction.Load;
                    stores[i] = RenderBufferStoreAction.Store;
                }
                // RenderTargetBinding preserves independently selected color/depth slices.
                cmd.SetRenderTarget(new RenderTargetBinding(colors, loads, stores, depth,
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store));
            }

            static void RenderXR(PassData data, CommandBuffer cmd, GaussianSplatRenderSystem system, Material composite)
            {
                var xr = data.CameraData.xr;
                var settings = GaussianSplatSettings.instance;
                bool usingRT = !settings.isDebugRender;
                var viewport = new Rect(0, 0, data.GaussianSplatWidth, data.GaussianSplatHeight);
                // URP owns the stereo keywords and instance multiplier. Explicit slice draws
                // must run without its x2 multiplier, then restore its original XR mode.
                xr.StopSinglePass(cmd);
                cmd.EnableShaderKeyword("GS_XR_ARRAY");
                try
                {
                    cmd.SetGlobalMatrixArray("_SplatView", data.Views);
                    cmd.SetGlobalMatrixArray("_SplatProjection", data.Projections);
                    cmd.SetGlobalMatrixArray("_SplatPrevView", data.PreviousViews);
                    cmd.SetGlobalMatrixArray("_SplatPrevProjection", data.PreviousProjections);
                    system.PrepareSplats(data.CameraData.camera, cmd, data.GaussianSplatWidth, data.GaussianSplatHeight, true);

                    // Draw every eye before sampling any MSAA color/motion attachments.
                    for (int view = 0; view < xr.viewCount; ++view)
                    {
                        int eye = xr.singlePassEnabled ? view : xr.multipassId;
                        int targetSlice = xr.GetTextureArraySlice(view);
                        cmd.SetGlobalInt("_SplatEyeIndex", eye);
                        RenderTargetIdentifier depth = Slice(data.SourceDepth, data.DepthIsArray ? targetSlice : 0);
                        RenderTargetIdentifier color = usingRT ? Slice(data.GaussianSplatRT, eye) :
                            Slice(data.SourceTexture, data.ColorIsArray ? targetSlice : 0);
                        var colors = usingRT && settings.m_TemporalFilter != TemporalFilter.None ?
                            new[] { color, Slice(data.GaussianSplatMotionRT, eye) } : new[] { color };
                        BindEyeTargets(cmd, colors, depth);
                        cmd.SetViewport(viewport);
                        if (usingRT) cmd.ClearRenderTarget(false, true, Color.clear);
                        system.DrawPreparedSplats(cmd);
                    }

                    if (usingRT)
                    {
                        // Unbind MRTs to resolve multisampled inputs before sampling.
                        CoreUtils.SetRenderTarget(cmd, data.SourceTexture, ClearFlag.None);
                        for (int view = 0; view < xr.viewCount; ++view)
                        {
                            int eye = xr.singlePassEnabled ? view : xr.multipassId;
                            int targetSlice = xr.GetTextureArraySlice(view);
                            var destination = Slice(data.SourceTexture, data.ColorIsArray ? targetSlice : 0);
                            cmd.SetGlobalInt("_SplatEyeIndex", eye);
                            if (settings.m_TemporalFilter != TemporalFilter.None)
                            {
                                system.GetTemporalFilter().RenderXREye(cmd, data.CameraData.camera, eye, composite,
                                    data.GaussianSplatRT, destination, data.GaussianSplatMotionRT,
                                    data.SplatDescriptor, viewport, settings.m_FrameInfluence, settings.m_VarianceClampScale);
                            }
                            else
                            {
                                cmd.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatRT, data.GaussianSplatRT);
                                cmd.SetRenderTarget(destination);
                                cmd.SetViewport(viewport);
                                cmd.DrawProcedural(Matrix4x4.identity, composite, 0, MeshTopology.Triangles, 3, 1);
                            }
                        }
                    }
                }
                finally
                {
                    cmd.DisableShaderKeyword("GS_XR_ARRAY");
                    cmd.SetGlobalInt("_SplatStereoEnabled", 0);
                    cmd.SetGlobalInt("_SplatEyeIndex", 0);
                    CoreUtils.SetRenderTarget(cmd, data.SourceTexture, data.SourceDepth, ClearFlag.None);
                    cmd.SetViewport(data.CameraData.camera.pixelRect);
                    xr.StartSinglePass(cmd);
                }
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass()
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };

            // Apply render-scale once when the feature is created. 
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset != null)
            {
                if (m_OverrideResolution)
                {
                    int maxSide = Mathf.Max(Screen.width, Screen.height);
                    float desiredScale = Mathf.Min(2f, (float)m_MaxSize / (float)maxSide);
                    asset.renderScale = desiredScale;
                }
            }
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;

            var camera = cameraData.camera;
            if (m_RenderOnlyMainCamera && (camera == null || camera.tag != "MainCamera"))
                return;

            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(camera))
                return;

            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera)
                return;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            // no restore of pipeline asset
            m_Pass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
