using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using Unity.Collections;
using UnityEngine;

namespace SmartRoom.Capture
{
    /// <summary>
    /// Minimal Quest 3 RGB-D capture script.
    ///
    /// Output directory contents:
    ///   rgb.jpg   - PassthroughCameraAccess RGB image
    ///   depth.raw - selected EnvironmentDepth texture-array slice, float32 raw values in 0..1
    ///   meta.json - all camera/depth metadata required by the Python alignment script
    ///
    /// Call CaptureOnce("E:/some/path/capture_0000") from the Unity main thread.
    /// </summary>
    public sealed class Quest3RgbdCaptureFinal : MonoBehaviour
    {
        [Header("Capture")]
        [SerializeField] private string outputDirectory = "";
        [SerializeField] private int rgbOutputWidth = 1280;
        [SerializeField] private int rgbOutputHeight = 1280;
        [SerializeField] private int jpegQuality = 95;

        [Header("References")]
        [SerializeField] private PassthroughCameraAccess passthroughCamera;
        [SerializeField] private EnvironmentDepthManager depthManager;

        private Shader _depthShader;
        private Material _depthMaterial;
        private RenderTexture _rgbRt;
        private Texture2D _rgbReadback;
        private RenderTexture _depthRt;
        private Texture2D _depthReadback;

        private int _lastDepthTextureWidth;
        private int _lastDepthTextureHeight;
        private int _lastDepthTextureSlices;
        private string _lastDepthTextureDimension = "unknown";

        private void Awake()
        {
            if (passthroughCamera == null)
                passthroughCamera = FindFirstObjectByType<PassthroughCameraAccess>();
            if (depthManager == null)
                depthManager = FindFirstObjectByType<EnvironmentDepthManager>();

            if (passthroughCamera != null)
                passthroughCamera.RequestedResolution = new Vector2Int(rgbOutputWidth, rgbOutputHeight);

            _depthShader = Shader.Find("Hidden/SmartRoom/DepthArraySliceToFloat");
            if (_depthShader == null)
                _depthShader = Shader.Find("Hidden/SmartRoom/DepthArraySliceToFloat_Resource");
            if (_depthShader == null)
                _depthShader = Resources.Load<Shader>("SmartRoomDepthArraySliceToFloat");
            if (_depthShader != null)
                _depthMaterial = new Material(_depthShader);
        }

        private void OnDestroy()
        {
            if (_depthMaterial != null) Destroy(_depthMaterial);
            if (_rgbRt != null) _rgbRt.Release();
            if (_depthRt != null) _depthRt.Release();
            if (_rgbReadback != null) Destroy(_rgbReadback);
            if (_depthReadback != null) Destroy(_depthReadback);
        }

        [ContextMenu("Capture To Configured Directory")]
        public void CaptureToConfiguredDirectory()
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                Debug.LogError("[Quest3RgbdCaptureFinal] outputDirectory is empty.");
                return;
            }

            CaptureOnce(outputDirectory);
        }

        public bool CaptureOnce(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                Debug.LogError("[Quest3RgbdCaptureFinal] CaptureOnce directory is empty.");
                return false;
            }

            Directory.CreateDirectory(directory);

            bool rgbOk = TryCaptureRgb(out byte[] jpegBytes, out RgbMeta rgbMeta);
            bool depthOk = TryCaptureDepthRaw(out byte[] depthRaw);
            bool descriptorOk = TryGetCurrentDepthDescriptor(out DepthDescriptorData descriptor);

            int selectedEye = GetSelectedDepthEyeIndex();
            Matrix4x4[] reprojectionMatrices = GetEnvironmentDepthReprojectionMatrices();
            Matrix4x4 trackingSpaceWorldToLocal = GetTrackingSpaceWorldToLocalMatrix();
            Matrix4x4 descriptorReprojection = descriptorOk
                ? CalculateDescriptorReprojection(descriptor)
                : Matrix4x4.identity;

            CaptureMeta meta = new CaptureMeta
            {
                capture_index = 0,
                unity_frame = Time.frameCount,
                timestamp_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                rgb = rgbMeta,
                depth = new DepthMeta
                {
                    is_valid = rgbOk && depthOk && descriptorOk,
                    pose_source = descriptorOk ? $"frameDescriptors[{selectedEye}](current)" : "none",
                    selected_eye = selectedEye,
                    texture_width = _lastDepthTextureWidth,
                    texture_height = _lastDepthTextureHeight,
                    texture_slices = _lastDepthTextureSlices,
                    texture_dimension = _lastDepthTextureDimension,
                    depth_values = "float32_raw_environment_depth_0_1",
                    depth_origin = "Graphics.Blit_Texture2DArray_slice_to_RFloat_ReadPixels",
                    pose_position_x = descriptor.pose.position.x,
                    pose_position_y = descriptor.pose.position.y,
                    pose_position_z = descriptor.pose.position.z,
                    pose_rotation_x = descriptor.pose.rotation.x,
                    pose_rotation_y = descriptor.pose.rotation.y,
                    pose_rotation_z = descriptor.pose.rotation.z,
                    pose_rotation_w = descriptor.pose.rotation.w,
                    resolution_w = _lastDepthTextureWidth > 0 ? _lastDepthTextureWidth : 320,
                    resolution_h = _lastDepthTextureHeight > 0 ? _lastDepthTextureHeight : 320,
                    fov_left = descriptor.fovLeft,
                    fov_right = descriptor.fovRight,
                    fov_top = descriptor.fovTop,
                    fov_bottom = descriptor.fovBottom,
                    near_z = descriptor.nearZ,
                    far_z = descriptor.farZ,
                    zbuffer_x = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams").x,
                    zbuffer_y = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams").y,
                    zbuffer_z = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams").z,
                    zbuffer_w = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams").w,
                    reprojection_matrix = MatrixToRowMajorArray(reprojectionMatrices[Mathf.Clamp(selectedEye, 0, reprojectionMatrices.Length - 1)]),
                    reprojection_matrix_eye0 = MatrixToRowMajorArray(reprojectionMatrices[0]),
                    reprojection_matrix_eye1 = MatrixToRowMajorArray(reprojectionMatrices[1]),
                    tracking_space_world_to_local = MatrixToRowMajorArray(trackingSpaceWorldToLocal),
                    descriptor_reprojection_matrix = MatrixToRowMajorArray(descriptorReprojection),
                }
            };

            File.WriteAllText(Path.Combine(directory, "meta.json"), JsonUtility.ToJson(meta, true));
            if (rgbOk && jpegBytes != null)
                File.WriteAllBytes(Path.Combine(directory, "rgb.jpg"), jpegBytes);
            if (depthOk && depthRaw != null)
                File.WriteAllBytes(Path.Combine(directory, "depth.raw"), depthRaw);

            Debug.Log($"[Quest3RgbdCaptureFinal] saved rgb={rgbOk} depth={depthOk} descriptor={descriptorOk} dir={directory}");
            return rgbOk && depthOk && descriptorOk;
        }

        private bool TryCaptureRgb(out byte[] jpegBytes, out RgbMeta meta)
        {
            jpegBytes = null;
            meta = new RgbMeta();

            if (passthroughCamera == null || !passthroughCamera.IsPlaying)
                return false;

            Texture source = passthroughCamera.GetTexture();
            if (source == null)
                return false;

            Vector2Int currentResolution = passthroughCamera.CurrentResolution;
            int captureWidth = source.width > 0 ? source.width : currentResolution.x;
            int captureHeight = source.height > 0 ? source.height : currentResolution.y;
            if (captureWidth <= 0 || captureHeight <= 0)
                return false;

            EnsureRgbBuffers(captureWidth, captureHeight);

            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, _rgbRt);
                RenderTexture.active = _rgbRt;
                _rgbReadback.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0, false);
                _rgbReadback.Apply(false, false);
                jpegBytes = _rgbReadback.EncodeToJPG(jpegQuality);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            var intrinsics = passthroughCamera.Intrinsics;
            Pose camPose = passthroughCamera.GetCameraPose();
            float scaleX = intrinsics.SensorResolution.x > 0 ? (float)captureWidth / intrinsics.SensorResolution.x : 1f;
            float scaleY = intrinsics.SensorResolution.y > 0 ? (float)captureHeight / intrinsics.SensorResolution.y : 1f;

            meta = new RgbMeta
            {
                timestamp_ticks = passthroughCamera.Timestamp.Ticks,
                resolution_w = captureWidth,
                resolution_h = captureHeight,
                requested_resolution_w = rgbOutputWidth,
                requested_resolution_h = rgbOutputHeight,
                current_resolution_w = currentResolution.x,
                current_resolution_h = currentResolution.y,
                source_resolution_w = captureWidth,
                source_resolution_h = captureHeight,
                camera_position = passthroughCamera.CameraPosition.ToString(),
                selected_depth_eye = GetSelectedDepthEyeIndex(),
                sensor_resolution_w = intrinsics.SensorResolution.x,
                sensor_resolution_h = intrinsics.SensorResolution.y,
                focal_length_x = intrinsics.FocalLength.x * scaleX,
                focal_length_y = intrinsics.FocalLength.y * scaleY,
                principal_point_x = intrinsics.PrincipalPoint.x * scaleX,
                principal_point_y = intrinsics.PrincipalPoint.y * scaleY,
                sensor_focal_length_x = intrinsics.FocalLength.x,
                sensor_focal_length_y = intrinsics.FocalLength.y,
                sensor_principal_point_x = intrinsics.PrincipalPoint.x,
                sensor_principal_point_y = intrinsics.PrincipalPoint.y,
                pose_position_x = camPose.position.x,
                pose_position_y = camPose.position.y,
                pose_position_z = camPose.position.z,
                pose_rotation_x = camPose.rotation.x,
                pose_rotation_y = camPose.rotation.y,
                pose_rotation_z = camPose.rotation.z,
                pose_rotation_w = camPose.rotation.w,
            };
            return jpegBytes != null && jpegBytes.Length > 0;
        }

        private bool TryCaptureDepthRaw(out byte[] depthRaw)
        {
            depthRaw = null;
            _lastDepthTextureWidth = 0;
            _lastDepthTextureHeight = 0;
            _lastDepthTextureSlices = 0;
            _lastDepthTextureDimension = "unknown";

            if (depthManager == null || !depthManager.IsDepthAvailable)
                return false;
            if (_depthMaterial == null)
                return false;
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat))
                return false;

            Texture sourceDepth = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
            if (sourceDepth == null || sourceDepth.dimension != UnityEngine.Rendering.TextureDimension.Tex2DArray)
                return false;

            int width = sourceDepth.width;
            int height = sourceDepth.height;
            if (width <= 0 || height <= 0)
                return false;

            _lastDepthTextureWidth = width;
            _lastDepthTextureHeight = height;
            _lastDepthTextureDimension = sourceDepth.dimension.ToString();
            RenderTexture sourceRt = sourceDepth as RenderTexture;
            _lastDepthTextureSlices = sourceRt != null ? sourceRt.volumeDepth : 0;

            EnsureDepthBuffers(width, height);
            _depthMaterial.SetTexture("_SourceDepthArray", sourceDepth);
            _depthMaterial.SetFloat("_ArraySlice", GetSelectedDepthEyeIndex());
            Graphics.Blit(null, _depthRt, _depthMaterial);

            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = _depthRt;
                _depthReadback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                _depthReadback.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            NativeArray<float> floats = _depthReadback.GetRawTextureData<float>();
            if (!floats.IsCreated || floats.Length != width * height)
                return false;

            float[] copy = new float[width * height];
            floats.CopyTo(copy);
            depthRaw = new byte[copy.Length * sizeof(float)];
            Buffer.BlockCopy(copy, 0, depthRaw, 0, depthRaw.Length);
            return true;
        }

        private bool TryGetCurrentDepthDescriptor(out DepthDescriptorData descriptor)
        {
            descriptor = new DepthDescriptorData
            {
                pose = Pose.identity,
                nearZ = 0.1f,
                farZ = float.PositiveInfinity,
            };

            if (depthManager == null)
                return false;

            FieldInfo field = typeof(EnvironmentDepthManager).GetField(
                "frameDescriptors",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
                return false;

            object value = field.GetValue(depthManager);
            Array descriptors = value as Array;
            int eye = GetSelectedDepthEyeIndex();
            if (descriptors == null || descriptors.Length <= eye)
                return false;

            object desc = descriptors.GetValue(eye);
            if (desc == null)
                return false;

            Type type = desc.GetType();
            Vector3 pos = (Vector3)type.GetField("createPoseLocation").GetValue(desc);
            Quaternion rot = (Quaternion)type.GetField("createPoseRotation").GetValue(desc);

            descriptor.pose = new Pose(pos, rot);
            descriptor.fovLeft = ReadFloatField(type, desc, "fovLeftAngleTangent");
            descriptor.fovRight = ReadFloatField(type, desc, "fovRightAngleTangent");
            descriptor.fovTop = ReadFloatField(type, desc, "fovTopAngleTangent");
            descriptor.fovBottom = ReadFloatField(type, desc, "fovDownAngleTangent");
            descriptor.nearZ = ReadFloatField(type, desc, "nearZ");
            descriptor.farZ = ReadFloatField(type, desc, "farZ");
            return true;
        }

        private static float ReadFloatField(Type type, object obj, string fieldName)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null ? Convert.ToSingle(field.GetValue(obj)) : 0f;
        }

        private int GetSelectedDepthEyeIndex()
        {
            if (passthroughCamera == null)
                return 0;
            return string.Equals(passthroughCamera.CameraPosition.ToString(), "Right", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        private Matrix4x4[] GetEnvironmentDepthReprojectionMatrices()
        {
            Matrix4x4[] mats = { Matrix4x4.identity, Matrix4x4.identity };
            List<Matrix4x4> list = new List<Matrix4x4>(2);
            Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices", list);
            for (int i = 0; i < Mathf.Min(mats.Length, list.Count); i++)
                mats[i] = list[i];
            return mats;
        }

        private Matrix4x4 GetTrackingSpaceWorldToLocalMatrix()
        {
            if (depthManager != null && depthManager.CustomTrackingSpace != null)
                return depthManager.CustomTrackingSpace.worldToLocalMatrix;

            OVRCameraRig cameraRig = FindFirstObjectByType<OVRCameraRig>();
            Transform trackingSpace = cameraRig != null ? cameraRig.trackingSpace : null;
            return trackingSpace != null ? trackingSpace.worldToLocalMatrix : Matrix4x4.identity;
        }

        private static Matrix4x4 CalculateDescriptorReprojection(DepthDescriptorData descriptor)
        {
            float left = descriptor.fovLeft;
            float right = descriptor.fovRight;
            float top = descriptor.fovTop;
            float bottom = descriptor.fovBottom;
            float near = descriptor.nearZ;
            float far = descriptor.farZ;

            float x = 2.0f / (right + left);
            float y = 2.0f / (top + bottom);
            float a = (right - left) / (right + left);
            float b = (top - bottom) / (top + bottom);
            float c;
            float d;
            if (float.IsInfinity(far) || far < near)
            {
                c = -1.0f;
                d = -2.0f * near;
            }
            else
            {
                c = -(far + near) / (far - near);
                d = -(2.0f * far * near) / (far - near);
            }

            Matrix4x4 projection = new Matrix4x4
            {
                m00 = x,
                m01 = 0,
                m02 = a,
                m03 = 0,
                m10 = 0,
                m11 = y,
                m12 = b,
                m13 = 0,
                m20 = 0,
                m21 = 0,
                m22 = c,
                m23 = d,
                m30 = 0,
                m31 = 0,
                m32 = -1.0f,
                m33 = 0
            };
            Matrix4x4 view = Matrix4x4.TRS(descriptor.pose.position, descriptor.pose.rotation, new Vector3(1, 1, -1)).inverse;
            return projection * view;
        }

        private void EnsureRgbBuffers(int width, int height)
        {
            if (_rgbRt == null || _rgbRt.width != width || _rgbRt.height != height)
            {
                if (_rgbRt != null) _rgbRt.Release();
                _rgbRt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    useMipMap = false,
                    autoGenerateMips = false
                };
                _rgbRt.Create();
            }

            if (_rgbReadback == null || _rgbReadback.width != width || _rgbReadback.height != height)
            {
                if (_rgbReadback != null) Destroy(_rgbReadback);
                _rgbReadback = new Texture2D(width, height, TextureFormat.RGB24, false);
            }
        }

        private void EnsureDepthBuffers(int width, int height)
        {
            if (_depthRt == null || _depthRt.width != width || _depthRt.height != height)
            {
                if (_depthRt != null) _depthRt.Release();
                _depthRt = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat)
                {
                    useMipMap = false,
                    autoGenerateMips = false
                };
                _depthRt.Create();
            }

            if (_depthReadback == null || _depthReadback.width != width || _depthReadback.height != height)
            {
                if (_depthReadback != null) Destroy(_depthReadback);
                _depthReadback = new Texture2D(width, height, TextureFormat.RFloat, false);
            }
        }

        private static float[] MatrixToRowMajorArray(Matrix4x4 m)
        {
            return new[]
            {
                m.m00, m.m01, m.m02, m.m03,
                m.m10, m.m11, m.m12, m.m13,
                m.m20, m.m21, m.m22, m.m23,
                m.m30, m.m31, m.m32, m.m33
            };
        }

        private struct DepthDescriptorData
        {
            public Pose pose;
            public float fovLeft, fovRight, fovTop, fovBottom;
            public float nearZ, farZ;
        }

        [Serializable]
        private class CaptureMeta
        {
            public int capture_index;
            public int unity_frame;
            public long timestamp_unix_ms;
            public RgbMeta rgb;
            public DepthMeta depth;
        }

        [Serializable]
        private class RgbMeta
        {
            public long timestamp_ticks;
            public int resolution_w, resolution_h;
            public int requested_resolution_w, requested_resolution_h;
            public int current_resolution_w, current_resolution_h;
            public int source_resolution_w, source_resolution_h;
            public string camera_position;
            public int selected_depth_eye;
            public int sensor_resolution_w, sensor_resolution_h;
            public float focal_length_x, focal_length_y;
            public float principal_point_x, principal_point_y;
            public float sensor_focal_length_x, sensor_focal_length_y;
            public float sensor_principal_point_x, sensor_principal_point_y;
            public float pose_position_x, pose_position_y, pose_position_z;
            public float pose_rotation_x, pose_rotation_y, pose_rotation_z, pose_rotation_w;
        }

        [Serializable]
        private class DepthMeta
        {
            public bool is_valid;
            public string pose_source;
            public int selected_eye;
            public int texture_width, texture_height, texture_slices;
            public string texture_dimension;
            public string depth_values;
            public string depth_origin;
            public float pose_position_x, pose_position_y, pose_position_z;
            public float pose_rotation_x, pose_rotation_y, pose_rotation_z, pose_rotation_w;
            public int resolution_w, resolution_h;
            public float fov_left, fov_right, fov_top, fov_bottom;
            public float near_z, far_z;
            public float zbuffer_x, zbuffer_y, zbuffer_z, zbuffer_w;
            public float[] reprojection_matrix;
            public float[] reprojection_matrix_eye0;
            public float[] reprojection_matrix_eye1;
            public float[] tracking_space_world_to_local;
            public float[] descriptor_reprojection_matrix;
        }
    }
}
