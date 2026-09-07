// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    public class GaussianSplatTemporalFilter
    {
        static class Props
        {
            public static readonly int _TaaAccumulationTex = Shader.PropertyToID("_TaaAccumulationTex");
            public static readonly int _TaaFrameInfluence     = Shader.PropertyToID("_TaaFrameInfluence");
            public static readonly int _TaaVarianceClampScale = Shader.PropertyToID("_TaaVarianceClampScale");
            public static readonly int _TaaMotionVectorTex = Shader.PropertyToID("_TaaMotionVectorTex");
        }

        int m_CurWidth = -1, m_CurHeight = -1;
        RenderTexture m_AccumulationTexture;
        RenderTexture m_TempTexture;

        sealed class XREyeHistory
        {
            internal RenderTexture read, write;
            internal int frame = -1;
        }
        readonly Dictionary<(Camera, int), XREyeHistory> m_XRHistory = new();

        // Keep XR textures and each eye's history separate from the legacy 2D filter.
        internal void RenderXREye(CommandBuffer cmd, Camera camera, int eye, Material material,
            RenderTargetIdentifier source, RenderTargetIdentifier destination, RenderTargetIdentifier motion,
            RenderTextureDescriptor descriptor, Rect viewport, float influence, float variance)
        {
            var key = (camera, eye);
            if (!m_XRHistory.TryGetValue(key, out var history))
                m_XRHistory.Add(key, history = new XREyeHistory());
            descriptor.msaaSamples = 1;
            descriptor.bindMS = false;
            descriptor.depthBufferBits = 0;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.useDynamicScale = false;
            descriptor.memoryless = RenderTextureMemoryless.None;
            bool reset = history.read == null || history.read.width != descriptor.width || history.read.height != descriptor.height;
            if (reset)
            {
                Object.DestroyImmediate(history.read);
                Object.DestroyImmediate(history.write);
                history.read = new RenderTexture(descriptor) { name = "Gaussian XR History " + eye };
                history.write = new RenderTexture(descriptor) { name = "Gaussian XR Filter " + eye };
                history.read.Create();
                history.write.Create();
            }
            if (reset || history.frame != Time.frameCount - 1)
                influence = 1;
            var texelSize = new Vector4(1f / descriptor.width, 1f / descriptor.height, descriptor.width, descriptor.height);
            var properties = new MaterialPropertyBlock();
            properties.SetFloat(Props._TaaFrameInfluence, influence);
            properties.SetFloat(Props._TaaVarianceClampScale, variance);
            properties.SetTexture(Props._TaaAccumulationTex, history.read);
            properties.SetVector("_TaaAccumulationTex_TexelSize", texelSize);
            properties.SetVector("_TaaMotionVectorTex_TexelSize", texelSize);
            properties.SetVector("_GaussianSplatRT_TexelSize", texelSize);
            cmd.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatRT, source);
            cmd.SetGlobalTexture(Props._TaaMotionVectorTex, motion);
            cmd.SetRenderTarget(history.write, 0, CubemapFace.Unknown, eye);
            cmd.SetViewport(viewport);
            cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3, 1, properties);

            // Composite directly from the filtered array slice; no cmd.Blit or copy
            // that can change URP's stereo keywords or mix eye histories.
            cmd.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatRT, history.write);
            cmd.SetRenderTarget(destination);
            cmd.SetViewport(viewport);
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
            (history.read, history.write) = (history.write, history.read);
            history.frame = Time.frameCount;
        }

        public void Dispose()
        {
            foreach (var history in m_XRHistory.Values)
            {
                Object.DestroyImmediate(history.read);
                Object.DestroyImmediate(history.write);
            }
            m_XRHistory.Clear();
            Object.DestroyImmediate(m_AccumulationTexture); m_AccumulationTexture = null;
            Object.DestroyImmediate(m_TempTexture); m_TempTexture = null;
            m_CurWidth = -1;
            m_CurHeight = -1;
        }

        public void Render(
            CommandBuffer cmb,
            Camera camera,
            Material material,
            int passIndex,
            RenderTargetIdentifier srcSplatColor,
            RenderTargetIdentifier dstComposedColor,
            int srcWidth,
            int srcHeight,
            float frameInfluence,
            float varianceClampScale,
            RenderTargetIdentifier motionVectorTex)
        {
            int width = srcWidth;
            int height = srcHeight;

            float taaFrameInfluence = frameInfluence;

            if (width != m_CurWidth || height != m_CurHeight || m_AccumulationTexture == null || m_TempTexture == null)
            {
                Object.DestroyImmediate(m_AccumulationTexture);
                Object.DestroyImmediate(m_TempTexture);
                m_CurWidth = width;
                m_CurHeight = height;

                RenderTextureDescriptor desc = default;
                desc.width = m_CurWidth;
                desc.height = m_CurHeight;
                desc.msaaSamples = 1;
                desc.volumeDepth = 1;
                // Use higher precision (half float) for weighted intermediate accumulation
                // to avoid precision loss when blending successive frames.
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                desc.dimension = TextureDimension.Tex2D;
                m_AccumulationTexture = new RenderTexture(desc);
                m_TempTexture = new RenderTexture(desc);
                taaFrameInfluence = 1.0f; // copy input into history when initializing/resizing
            }

            // sample new frame & history -> output temp buffer
            cmb.SetRenderTarget(m_TempTexture);
            material.SetFloat(Props._TaaFrameInfluence, taaFrameInfluence);
            material.SetFloat(Props._TaaVarianceClampScale, varianceClampScale);
            material.SetTexture(Props._TaaAccumulationTex, m_AccumulationTexture);
            cmb.SetGlobalTexture(Props._TaaMotionVectorTex, motionVectorTex); // bind motion vector texture
            cmb.DrawProcedural(Matrix4x4.identity, material, passIndex, MeshTopology.Triangles, 3, 1);

            // copy temp buffer -> into history
            cmb.CopyTexture(m_TempTexture, m_AccumulationTexture);

            // composite temp buffer into output
            // Use Blit here to allow proper format conversion if the destination
            // render target has a different format than our float accumulation texture.
            cmb.Blit(m_TempTexture, srcSplatColor);
            cmb.SetRenderTarget(dstComposedColor);
            cmb.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
        }
    }
}