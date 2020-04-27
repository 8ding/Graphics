// XRSystem is where information about XR views and passes are read from 2 exclusive sources:
// - XRDisplaySubsystem from the XR SDK
// - the test automated test framework

#if ENABLE_VR && ENABLE_XR_MODULE

using System;
using System.Collections.Generic;
using UnityEngine.XR;

namespace UnityEngine.Rendering.Universal
{
    internal partial class XRSystem
    {
        // Valid empty pass when a camera is not using XR
        internal readonly XRPass emptyPass = new XRPass();

        // Store active passes and avoid allocating memory every frames
        List<XRPass> framePasses = new List<XRPass>();

        // XR SDK display interface
        static List<XRDisplaySubsystem> displayList = new List<XRDisplaySubsystem>();
        XRDisplaySubsystem display = null;

        // Internal resources used by XR rendering
        Material occlusionMeshMaterial = null;
        Material mirrorViewMaterial = null;
        MaterialPropertyBlock mirrorViewMaterialProperty = new MaterialPropertyBlock();

        // Set by test framework
        internal static bool automatedTestRunning = false;

        // Used by test framework and to enable debug features
        static bool testModeEnabledInitialization { get => Array.Exists(Environment.GetCommandLineArgs(), arg => arg == "-xr-tests"); }
        internal static bool testModeEnabled = testModeEnabledInitialization;

        RenderTexture testRenderTexture = null;

        const string k_XRMirrorTag = "XR Mirror View";
        static ProfilingSampler _XRMirrorProfilingSampler = new ProfilingSampler(k_XRMirrorTag);

        internal XRSystem()
        {
            RefreshXrSdk();

            TextureXR.maxViews = Math.Max(TextureXR.slices, GetMaxViews());
        }

        internal void InitializeXRSystemData(XRSystemData data)
        {
            if (data)
            {
                occlusionMeshMaterial = CoreUtils.CreateEngineMaterial(data.shaders.xrOcclusionMeshPS);
                mirrorViewMaterial = CoreUtils.CreateEngineMaterial(data.shaders.xrMirrorViewPS);
            }
        }

        static void GetDisplaySubsystem()
        {
#if UNITY_2020_2_OR_NEWER
            //SubsystemManager.GetSubsystems(displayList);
            SubsystemManager.GetInstances(displayList);
#else
            SubsystemManager.GetInstances(displayList);
#endif
        }

        // With XR SDK: disable legacy VR system before rendering first frame
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        internal static void XRSystemInit()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
                return;

            GetDisplaySubsystem();

            // XRTODO: refactor with RefreshXrSdk()
            for (int i = 0; i < displayList.Count; i++)
            {
                displayList[i].disableLegacyRenderer = true;
                displayList[i].textureLayout = XRDisplaySubsystem.TextureLayout.Texture2DArray;
                displayList[i].sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear;
            }
        }

        internal static void UpdateMSAALevel(int level)
        {
            GetDisplaySubsystem();

            for (int i = 0; i < displayList.Count; i++)
                displayList[i].SetMSAALevel(level);
        }

        internal static void UpdateRenderScale(float renderScale)
        {
            GetDisplaySubsystem();

            for (int i = 0; i < displayList.Count; i++)
                displayList[i].scaleOfAllRenderTargets = renderScale;
        }

        // Compute the maximum number of views (slices) to allocate for texture arrays
        internal int GetMaxViews()
        {
            int maxViews = 1;

            if (display != null)
            {
                // XRTODO : replace by API from XR SDK, assume we have 2 slices until then
                maxViews = 2;
            }
            else if (testModeEnabled)
            {
                maxViews = Math.Max(maxViews, 2);
            }

            return maxViews;
        }

        internal List<XRPass> SetupFrame(CameraData cameraData)
        {
            Camera camera = cameraData.camera;
            bool xrEnabled = RefreshXrSdk();

            if (display != null)
            {
                // XRTODO: Handle stereo mode selection in URP pipeline asset UI
                display.textureLayout = XRDisplaySubsystem.TextureLayout.Texture2DArray;
                display.zNear = camera.nearClipPlane;
                display.zFar  = camera.farClipPlane;
                display.sRGB  = QualitySettings.activeColorSpace == ColorSpace.Linear;
            }

            if (framePasses.Count > 0)
            {
                Debug.LogWarning("XRSystem.ReleaseFrame() was not called!");
                ReleaseFrame();
            }

            if (camera == null)
                return framePasses;

            // Enable XR layout only for game camera
            bool isGameCamera = (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.VR);
            bool xrSupported = isGameCamera && camera.targetTexture == null;

            if (testModeEnabled && automatedTestRunning && isGameCamera && LayoutSinglePassTestMode(cameraData, new XRLayout() { camera = camera, xrSystem = this }))
            {
                // test layout in used
            }
            else if (xrEnabled && xrSupported)
            {
                // Disable vsync on the main display when rendering to a XR device
                QualitySettings.vSyncCount = 0;

                // XRTODO: handle camera.stereoTargetEye here ? or just add xrRendering on the camera ?
                CreateLayoutFromXrSdk(camera, singlePassAllowed: true);
            }
            else
            {
                AddPassToFrame(emptyPass);
            }

            return framePasses;
        }

        internal void ReleaseFrame()
        {
            foreach (XRPass xrPass in framePasses)
            {
                if (xrPass != emptyPass)
                    XRPass.Release(xrPass);
            }

            framePasses.Clear();

            if (testRenderTexture)
                RenderTexture.ReleaseTemporary(testRenderTexture);
        }

        bool RefreshXrSdk()
        {
            GetDisplaySubsystem();

            if (displayList.Count > 0)
            {
                if (displayList.Count > 1)
                    throw new NotImplementedException("Only 1 XR display is supported.");

                display = displayList[0];
                display.disableLegacyRenderer = true;

                // Refresh max views
                TextureXR.maxViews = Math.Max(TextureXR.slices, GetMaxViews());

                return display.running;
            }
            else
            {
                display = null;
            }

            return false;
        }

        // Used for camera stacking where we need to update the parameters per camera
        internal void UpdateFromCamera(ref XRPass xrPass, Camera camera)
        {
            if (xrPass.enabled)
            {
                display.GetRenderPass(xrPass.multipassId, out var renderPass);
                display.GetCullingParameters(camera, renderPass.cullingPassIndex, out var cullingParams);

                // Disable legacy stereo culling path
                cullingParams.cullingOptions &= ~CullingOptions.Stereo;

                if (xrPass.singlePassEnabled)
                {
                    xrPass = XRPass.Create(renderPass, multipassId: xrPass.multipassId, cullingParams, occlusionMeshMaterial);

                    for (int renderParamIndex = 0; renderParamIndex < renderPass.GetRenderParameterCount(); ++renderParamIndex)
                    {
                        renderPass.GetRenderParameter(camera, renderParamIndex, out var renderParam);
                        xrPass.AddView(renderPass, renderParam);
                    }
                }
                else
                {
                    renderPass.GetRenderParameter(camera, 0, out var renderParam);

                    xrPass = XRPass.Create(renderPass, multipassId: xrPass.multipassId, cullingParams, occlusionMeshMaterial);
                    xrPass.AddView(renderPass, renderParam);
                }
            }
        }

        void CreateLayoutFromXrSdk(Camera camera, bool singlePassAllowed)
        {
            bool CanUseSinglePass(XRDisplaySubsystem.XRRenderPass renderPass)
            {
                if (renderPass.renderTargetDesc.dimension != TextureDimension.Tex2DArray)
                    return false;

                if (renderPass.GetRenderParameterCount() != 2 || renderPass.renderTargetDesc.volumeDepth != 2)
                    return false;

                renderPass.GetRenderParameter(camera, 0, out var renderParam0);
                renderPass.GetRenderParameter(camera, 1, out var renderParam1);

                if (renderParam0.textureArraySlice != 0 || renderParam1.textureArraySlice != 1)
                    return false;

                if (renderParam0.viewport != renderParam1.viewport)
                    return false;

                return true;
            }

            for (int renderPassIndex = 0; renderPassIndex < display.GetRenderPassCount(); ++renderPassIndex)
            {
                display.GetRenderPass(renderPassIndex, out var renderPass);
                display.GetCullingParameters(camera, renderPass.cullingPassIndex, out var cullingParams);

                // Disable legacy stereo culling path
                cullingParams.cullingOptions &= ~CullingOptions.Stereo;

                if (singlePassAllowed && CanUseSinglePass(renderPass))
                {
                    var xrPass = XRPass.Create(renderPass, multipassId: framePasses.Count, cullingParams, occlusionMeshMaterial);

                    for (int renderParamIndex = 0; renderParamIndex < renderPass.GetRenderParameterCount(); ++renderParamIndex)
                    {
                        renderPass.GetRenderParameter(camera, renderParamIndex, out var renderParam);
                        xrPass.AddView(renderPass, renderParam);
                    }

                    AddPassToFrame(xrPass);
                }
                else
                {
                    for (int renderParamIndex = 0; renderParamIndex < renderPass.GetRenderParameterCount(); ++renderParamIndex)
                    {
                        renderPass.GetRenderParameter(camera, renderParamIndex, out var renderParam);

                        var xrPass = XRPass.Create(renderPass, multipassId: framePasses.Count, cullingParams, occlusionMeshMaterial);
                        xrPass.AddView(renderPass, renderParam);

                        AddPassToFrame(xrPass);
                    }
                }
            }
        }

        internal void Cleanup()
        {
            CoreUtils.Destroy(occlusionMeshMaterial);
            CoreUtils.Destroy(mirrorViewMaterial);
        }

        internal void AddPassToFrame(XRPass xrPass)
        {
            framePasses.Add(xrPass);
        }

        internal static class XRShaderIDs
        {
            public static readonly int _BlitTexture       = Shader.PropertyToID("_BlitTexture");
            public static readonly int _BlitScaleBias     = Shader.PropertyToID("_BlitScaleBias");
            public static readonly int _BlitScaleBiasRt   = Shader.PropertyToID("_BlitScaleBiasRt");
            public static readonly int _BlitTexArraySlice = Shader.PropertyToID("_BlitTexArraySlice");
            public static readonly int _SRGBRead          = Shader.PropertyToID("_SRGBRead");
        }

        internal void RenderMirrorView(CommandBuffer cmd, Camera camera)
        {
            // XRTODO : remove this check when the Quest plugin is fixed
            if (Application.platform == RuntimePlatform.Android)
                return;

            if (display == null || !display.running || !mirrorViewMaterial)
                return;

            using (new ProfilingScope(cmd, _XRMirrorProfilingSampler))
            {
                cmd.SetRenderTarget(camera.targetTexture != null  ? camera.targetTexture : new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
                bool yflip = camera.targetTexture != null || camera.cameraType == CameraType.SceneView || camera.cameraType == CameraType.Preview;
                int mirrorBlitMode = display.GetPreferredMirrorBlitMode();
                if (display.GetMirrorViewBlitDesc(null, out var blitDesc, mirrorBlitMode))
                {
                    if (blitDesc.nativeBlitAvailable)
                    {
                        display.AddGraphicsThreadMirrorViewBlit(cmd, blitDesc.nativeBlitInvalidStates, mirrorBlitMode);
                    }
                    else
                    {
                        for (int i = 0; i < blitDesc.blitParamsCount; ++i)
                        {
                            blitDesc.GetBlitParameter(i, out var blitParam);

                            Vector4 scaleBias = yflip ? new Vector4(blitParam.srcRect.width, -blitParam.srcRect.height, blitParam.srcRect.x, blitParam.srcRect.height + blitParam.srcRect.y) :
                                                        new Vector4(blitParam.srcRect.width, blitParam.srcRect.height, blitParam.srcRect.x, blitParam.srcRect.y);
                            Vector4 scaleBiasRT = new Vector4(blitParam.destRect.width, blitParam.destRect.height, blitParam.destRect.x, blitParam.destRect.y);

                            mirrorViewMaterialProperty.SetInt(XRShaderIDs._SRGBRead, (!display.sRGB || blitParam.srcTex.sRGB) ? 0 : 1);
                            mirrorViewMaterialProperty.SetTexture(XRShaderIDs._BlitTexture, blitParam.srcTex);
                            mirrorViewMaterialProperty.SetVector(XRShaderIDs._BlitScaleBias, scaleBias);
                            mirrorViewMaterialProperty.SetVector(XRShaderIDs._BlitScaleBiasRt, scaleBiasRT);
                            mirrorViewMaterialProperty.SetInt(XRShaderIDs._BlitTexArraySlice, blitParam.srcTexArraySlice);

                            int shaderPass = (blitParam.srcTex.dimension == TextureDimension.Tex2DArray) ? 1 : 0;
                            cmd.DrawProcedural(Matrix4x4.identity, mirrorViewMaterial, shaderPass, MeshTopology.Quads, 4, 1, mirrorViewMaterialProperty);
                        }
                    }
                }
                else
                {
                    cmd.ClearRenderTarget(true, true, Color.black);
                }
            }
        }

        bool LayoutSinglePassTestMode(CameraData cameraData, XRLayout frameLayout)
        {
            Camera camera = frameLayout.camera;

            if (camera == null || camera != Camera.main)
                return false;

            if (camera.TryGetCullingParameters(false, out var cullingParams))
            {
                cullingParams.stereoProjectionMatrix = camera.projectionMatrix;
                cullingParams.stereoViewMatrix = camera.worldToCameraMatrix;

                // Allocate temp target to render test scene with single-pass
                // And copy the last view to the actual render texture used to compare image in test framework
                {
                    RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                    rtDesc.dimension = TextureDimension.Tex2DArray;
                    rtDesc.volumeDepth = 2;

                    testRenderTexture = RenderTexture.GetTemporary(rtDesc);
                }

                void copyToTestRenderTexture(XRPass pass, CommandBuffer cmd, RenderTexture rt, Rect viewport)
                {
                    cmd.SetViewport(viewport);
                    cmd.SetRenderTarget(rt == null ? new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget) : rt);

                    Vector4 scaleBias   = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);
                    Vector4 scaleBiasRT = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);

                    if (rt == null)
                    {
                        scaleBias.y = -1.0f;
                        scaleBias.w = 1.0f;
                    }

                    mirrorViewMaterialProperty.SetInt(XRShaderIDs._SRGBRead, (rt != null && rt.sRGB) ? 0 : 1);
                    mirrorViewMaterialProperty.SetTexture(XRShaderIDs._BlitTexture, testRenderTexture);
                    mirrorViewMaterialProperty.SetVector(XRShaderIDs._BlitScaleBias, scaleBias);
                    mirrorViewMaterialProperty.SetVector(XRShaderIDs._BlitScaleBiasRt, scaleBiasRT);
                    mirrorViewMaterialProperty.SetInt(XRShaderIDs._BlitTexArraySlice, 1);

                    cmd.DrawProcedural(Matrix4x4.identity, mirrorViewMaterial, 1, MeshTopology.Quads, 4, 1, mirrorViewMaterialProperty);
                }

                var passInfo = new XRPassCreateInfo
                {
                    multipassId = 0,
                    cullingPassId = 0,
                    cullingParameters = cullingParams,
                    renderTarget = testRenderTexture,
                    renderTargetIsRenderTexture = true,
                    customMirrorView = copyToTestRenderTexture
                };

                var viewInfo2 = new XRViewCreateInfo
                {
                    projMatrix = camera.projectionMatrix,
                    viewMatrix = camera.worldToCameraMatrix,
                    viewport = new Rect(camera.pixelRect.x, camera.pixelRect.y, camera.pixelWidth, camera.pixelHeight),
                    textureArraySlice = -1
                };

                // Change the first view so that it's a different viewpoint and projection to detect more issues
                var viewInfo1 = viewInfo2;
                var planes = viewInfo1.projMatrix.decomposeProjection;
                planes.left *= 0.44f;
                planes.right *= 0.88f;
                planes.top *= 0.11f;
                planes.bottom *= 0.33f;
                viewInfo1.projMatrix = Matrix4x4.Frustum(planes);
                viewInfo1.viewMatrix *= Matrix4x4.Translate(new Vector3(.34f, 0.25f, -0.08f));

                // single-pass 2x rendering
                {
                    XRPass pass = frameLayout.CreatePass(passInfo);

                    frameLayout.AddViewToPass(viewInfo1, pass);
                    frameLayout.AddViewToPass(viewInfo2, pass);
                }

                // valid layout
                return true;
            }

            return false;
        }
    }
}

#endif
