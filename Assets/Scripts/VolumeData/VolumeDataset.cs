using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Serialization;

namespace UnityVolumeRendering
{
    /// <summary>
    /// An imported dataset. Contains a 3D pixel array of density values.
    /// </summary>
    [Serializable]
    public class VolumeDataset : ScriptableObject, ISerializationCallbackReceiver
    {
        public string filePath;
        
        // Flattened 3D array of data sample values.
        [SerializeField]
        public float[] data;

        [SerializeField]
        public int dimX, dimY, dimZ;

        [SerializeField]
        public Vector3 scale = Vector3.one;

        [SerializeField]
        public Quaternion rotation;
        
        public float volumeScale;

        [SerializeField]
        public string datasetName;

        private float minDataValue = float.MaxValue;
        private float maxDataValue = float.MinValue;

        private Texture2D dataTexture = null;
        private Texture2D gradientTexture = null;

        private SemaphoreSlim createDataTextureLock = new SemaphoreSlim(1, 1);
        private SemaphoreSlim createGradientTextureLock = new SemaphoreSlim(1, 1);

        [SerializeField, FormerlySerializedAs("scaleX")]
        private float scaleX_deprecated = 1.0f;
        [SerializeField, FormerlySerializedAs("scaleY")]
        private float scaleY_deprecated = 1.0f;
        [SerializeField, FormerlySerializedAs("scaleZ")]
        private float scaleZ_deprecated = 1.0f;

        [System.Obsolete("Use scale instead")]
        public float scaleX { get { return scale.x; } set { scale.x = value; } }
        [System.Obsolete("Use scale instead")]
        public float scaleY { get { return scale.y; } set { scale.y = value; } }
        [System.Obsolete("Use scale instead")]
        public float scaleZ { get { return scale.z; } set { scale.z = value; } }

        /// <summary>
        /// Gets the 3D data texture, containing the density values of the dataset.
        /// Will create the data texture if it does not exist. This may be slow (consider using <see cref="GetDataTextureAsync"/>).
        /// </summary>
        /// <returns>3D texture of dataset</returns>
        public Texture2D GetDataTexture()
        {
            if (dataTexture == null)
            {
                dataTexture = AsyncHelper.RunSync<Texture2D>(() => CreateTextureInternalAsync(NullProgressHandler.instance));
                return dataTexture;
            }
            else
            {
                return dataTexture;
            }
        }

        public void RecreateDataTexture()
        {
            dataTexture = AsyncHelper.RunSync<Texture2D>(() => CreateTextureInternalAsync(NullProgressHandler.instance));
        }

        /// <summary>
        /// Gets the 3D data texture, containing the density values of the dataset.
        /// Will create the data texture if it does not exist, without blocking the main thread.
        /// </summary>
        /// <param name="progressHandler">Progress handler for tracking the progress of the texture creation (optional).</param>
        /// <returns>Async task returning a 3D texture of the dataset</returns>
        public async Task<Texture2D> GetDataTextureAsync(IProgressHandler progressHandler = null)
        {
            if (dataTexture == null)
            {
                await createDataTextureLock.WaitAsync();
                try
                {
                    if (progressHandler == null)
                        progressHandler = NullProgressHandler.instance;
                    dataTexture = await CreateTextureInternalAsync(progressHandler);
                }
                finally
                {
                    createDataTextureLock.Release();
                }
            }
            return dataTexture;
        }

        /// <summary>
        /// Gets the gradient texture, containing the gradient values (direction of change) of the dataset.
        /// Will create the gradient texture if it does not exist. This may be slow (consider using <see cref="GetGradientTextureAsync" />).
        /// </summary>
        /// <returns>Gradient texture</returns>
        public Texture2D GetGradientTexture()
        {
            if (gradientTexture == null)
            {
                gradientTexture = AsyncHelper.RunSync<Texture2D>(() => CreateGradientTextureInternalAsync(GradientTypeUtils.GetDefaultGradientType(), NullProgressHandler.instance));
                return gradientTexture;
            }
            else
            {
                return gradientTexture;
            }
        }

        public async Task<Texture2D> RegenerateGradientTextureAsync(GradientType gradientType, IProgressHandler progressHandler = null)
        {
            await createGradientTextureLock.WaitAsync();
            try
            {
                if (progressHandler == null)
                    progressHandler = new NullProgressHandler();
                try
                {
                    gradientTexture = await CreateGradientTextureInternalAsync(gradientType, progressHandler != null ? progressHandler : NullProgressHandler.instance);
                }
                catch (System.Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
            finally
            {
                createGradientTextureLock.Release();
            }
            return gradientTexture;
        }

        /// <summary>
        /// Gets the gradient texture, containing the gradient values (direction of change) of the dataset.
        /// Will create the gradient texture if it does not exist, without blocking the main thread.
        /// </summary>
        /// <param name="progressHandler">Progress handler for tracking the progress of the texture creation (optional).</param>
        /// <returns>Async task returning a 3D gradient texture of the dataset</returns>
        public async Task<Texture2D> GetGradientTextureAsync(IProgressHandler progressHandler = null)
        {
            if (gradientTexture == null)
            {
                gradientTexture = await RegenerateGradientTextureAsync(GradientTypeUtils.GetDefaultGradientType(), progressHandler);
            }
            return gradientTexture;
        }

        public float GetMinDataValue()
        {
            if (minDataValue == float.MaxValue)
                CalculateValueBounds(new NullProgressHandler());
            return minDataValue;
        }

        public float GetMaxDataValue()
        {
            if (maxDataValue == float.MinValue)
                CalculateValueBounds(new NullProgressHandler());
            return maxDataValue;
        }

        public void RecalculateBounds()
        {
            CalculateValueBounds(new NullProgressHandler());
        }

        /// <summary>
        /// Ensures that the dataset is not too large.
        /// This is automatically called during import,
        ///  so you should not need to call it yourself unless you're making your own importer of modify the dimensions.
        /// </summary>
        public void FixDimensions()
        {
            int MAX_DIM = 2048; // 3D texture max size. See: https://docs.unity3d.com/Manual/class-Texture3D.html

            while (Mathf.Max(dimX, dimY, dimZ) > MAX_DIM)
            {
                Debug.LogWarning("Dimension exceeds limits (maximum: "+MAX_DIM+"). Dataset is downscaled by 2 on each axis!");

                DownScaleData();
            }
        }

        /// <summary>
        /// Downscales the data by averaging 8 voxels per each new voxel,
        /// and replaces downscaled data with the original data
        /// </summary>
        public void DownScaleData()
        {
            int halfDimX = dimX / 2 + dimX % 2;
            int halfDimY = dimY / 2 + dimY % 2;
            int halfDimZ = dimZ / 2 + dimZ % 2;
            float[] downScaledData = new float[halfDimX * halfDimY * halfDimZ];

            for (int x = 0; x < halfDimX; x++)
            {
                for (int y = 0; y < halfDimY; y++)
                {
                    for (int z = 0; z < halfDimZ; z++)
                    {
                        downScaledData[x + y * halfDimX + z * (halfDimX * halfDimY)] = Mathf.Round(GetAvgerageVoxelValues(x * 2, y * 2, z * 2));
                    }
                }
            }

            //Update data & data dimensions
            data = downScaledData;
            dimX = halfDimX;
            dimY = halfDimY;
            dimZ = halfDimZ;
        }

        private void CalculateValueBounds(IProgressHandler progressHandler)
        {
            minDataValue = float.MaxValue;
            maxDataValue = float.MinValue;

            if (data != null)
            {
                int dimension = dimX * dimY * dimZ;
                int sliceDimension = dimX * dimY;
                for (int i = 0; i < dimension;)
                {
                    progressHandler.ReportProgress(i, dimension, "Calculating value bounds");
                    for (int j = 0; j < sliceDimension; j++, i++)
                    {
                        float val = data[i];
                        minDataValue = Mathf.Min(minDataValue, val);
                        maxDataValue = Mathf.Max(maxDataValue, val);
                    }
                }
            }
        }

        private async Task<Texture2D> CreateTextureInternalAsync(IProgressHandler progressHandler)                                        
        {
            Debug.Log("Async texture generation. Hold on.");

            Texture2D.allowThreadedTextureCreation = true;
            TextureFormat texformat = SystemInfo.SupportsTextureFormat(TextureFormat.RHalf) ? TextureFormat.RHalf : TextureFormat.RFloat;

            float minValue = 0;
            float maxValue = 0;
            float maxRange = 0;

            progressHandler.StartStage(0.2f, "Calculating value bounds");
            await Task.Run(() =>
            {
                minValue = GetMinDataValue();
                maxValue = GetMaxDataValue();
                maxRange = maxValue - minValue;
            });
            progressHandler.EndStage();

            Texture2D texture = null;
            bool isHalfFloat = texformat == TextureFormat.RHalf;

            int dimZX = (int)Math.Sqrt(dimZ);
            int dimZY = (dimZ + dimZX - 1) / dimZX;

            progressHandler.StartStage(0.8f, "Creating texture");
            try
            {
                int dimension = dimX * dimY * dimZ;
                int sliceDimension = dimX * dimY;

                if (isHalfFloat)
                {
                    progressHandler.StartStage(0.8f, "Allocating pixel data");
                    NativeArray<ushort> pixelBytes = new NativeArray<ushort>(data.Length, Allocator.Persistent);

                    await Task.Run(() => {

                        // quick fix for Texture2D
                        int i = 0;
                        for (int by = 0; by < dimZY; by++)
                        for (int bx = 0; bx < dimZX; bx++)
                        {
                            progressHandler.ReportProgress(i, dimension, "Copying slice data.");

                            for (int y = 0; y < dimY; y++)
                            for (int x = 0; x < dimX; x++)
                            {
                                int u = x + bx * dimX;
                                int v = y + by * dimY;
                                int idx = u + v * dimX * dimZX;
                                pixelBytes[idx] = Mathf.FloatToHalf((float)(data[i] - minValue) / maxRange);
                                i++;
                            }
                        }
                    });
                    progressHandler.EndStage();
                    progressHandler.ReportProgress(0.8f, "Applying texture");

                    texture = new Texture2D(dimX * dimZX, dimZ * dimZY, texformat, false);
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.SetPixelData(pixelBytes, 0);
                    texture.Apply(false, true);
                    dataTexture = texture;
                    pixelBytes.Dispose();
                }
                else
                {
                    progressHandler.StartStage(0.8f, "Allocating pixel data");
                    NativeArray<float> pixelBytes = new NativeArray<float>(data.Length, Allocator.Persistent);

                    await Task.Run(() => {
                        for (int i = 0; i < dimension;)
                        {
                            progressHandler.ReportProgress(i, dimension, "Copying slice data.");
                            for (int j = 0; j < sliceDimension; j++, i++)
                            {
                                pixelBytes[i] = (float)(data[i] - minValue) / maxRange;
                            }
                        }
                    });
                    progressHandler.EndStage();
                    progressHandler.ReportProgress(0.8f, "Applying texture");

                    texture = new Texture2D(dimX * dimZX, dimZ * dimZY, texformat, false);
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.SetPixelData(pixelBytes, 0);
                    texture.Apply(false, true);
                    pixelBytes.Dispose();
                }
            }
            catch (OutOfMemoryException)
            {
                texture = new Texture2D(dimX * dimZX, dimZ * dimZY, texformat, false);
                texture.wrapMode = TextureWrapMode.Clamp;


                Debug.LogWarning("Out of memory when creating texture. Using fallback method.");

                for (int v = 0; v < dimY * dimZY; v++)
                for (int u = 0; u < dimX * dimZX; u++)
                {
                    int bx = u / dimX;
                    int by = v / dimY;
                    int x  = u % dimX;
                    int y  = v % dimY;
                    int z = bx + by * dimZX;

                    texture.SetPixel(u, v, new Color((float)(data[x + y * dimX + z * (dimX * dimY)] - minValue) / maxRange, 0.0f, 0.0f, 0.0f));
                }

                texture.Apply(false, true);
            }
            progressHandler.EndStage();
            Debug.Log("Texture generation done.");
            return texture;
        }

        private async Task<Texture2D> CreateGradientTextureInternalAsync(GradientType gradientType, IProgressHandler progressHandler)
        {
            Debug.Log("Async gradient generation. Hold on.");

            Texture2D.allowThreadedTextureCreation = true;
            TextureFormat texformat = SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf) ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat;

            float minValue = 0;
            float maxValue = 0;
            float maxRange = 0;
            Color[] cols = null;

            progressHandler.StartStage(0.2f, "Calculating value bounds");
            await Task.Run(() => {
                if (minDataValue == float.MaxValue || maxDataValue == float.MinValue)
                    CalculateValueBounds(progressHandler);
                minValue = GetMinDataValue();
                maxValue = GetMaxDataValue();
                maxRange = maxValue - minValue;
            });
            progressHandler.EndStage();

            int dimZX = (int)Math.Sqrt(dimZ);
            int dimZY = (dimZ + dimZX - 1) / dimZX;

            try
            {
                await Task.Run(() => cols = new Color[data.Length]);
            }
            catch (OutOfMemoryException)
            {
                progressHandler.StartStage(0.6f, "Creating gradient texture");
                Texture2D textureTmp = new Texture2D(dimX * dimZX, dimY * dimZY, texformat, false);
                textureTmp.wrapMode = TextureWrapMode.Clamp;

                GradientComputator gradientComputator = GradientComputatorFactory.CreateGradientComputator(this, gradientType);

                for (int v = 0; v < dimY * dimZY; v++)
                {
                    progressHandler.ReportProgress(v, dimY * dimZY, "Calculating gradients for slice");

                    for (int u = 0; u < dimX * dimZX; u++)
                    {
                        int bx = u / dimX;
                        int by = v / dimY;
                        int x  = u % dimX;
                        int y  = v % dimY;
                        int z = bx + by * dimZX;

                        int iData = x + y * dimX + z * (dimX * dimY);
                        Vector3 grad = gradientComputator.ComputeGradient(x, y, z, minValue, maxRange);

                        textureTmp.SetPixel(u, v, new Color(grad.x, grad.y, grad.z, (float)(data[iData] - minValue) / maxRange));
                    }
                }
                progressHandler.EndStage();
                progressHandler.StartStage(0.2f, "Uploading gradient texture");
                textureTmp.Apply(false, true);

                progressHandler.EndStage();
                Debug.Log("Gradient gereneration done.");

                return textureTmp;
            }

#if false
            progressHandler.StartStage(0.6f, "Creating gradient texture");
            await Task.Run(() => {
                GradientComputator gradientComputator = GradientComputatorFactory.CreateGradientComputator(this, gradientType);

                for (int v = 0; v < dimY * dimZY; v++)
                {
                    progressHandler.ReportProgress(v, dimY * dimZY, "Calculating gradients for slice");

                    for (int u = 0; u < dimX * dimZX; u++)
                    {
                        int bx = u / dimX;
                        int by = v / dimY;
                        int x  = u % dimX;
                        int y  = v % dimY;
                        int z = bx + by * dimZX;
                        int idx = u + v * dimX * dimZX;

                        int iData = x + y * dimX + z * (dimX * dimY);
                        Vector3 grad = gradientComputator.ComputeGradient(x, y, z, minValue, maxRange);

                        cols[idx] = new Color(grad.x, grad.y, grad.z, (float)(data[iData] - minValue) / maxRange);
                    }
                }
            });
            progressHandler.EndStage();
#endif

            progressHandler.StartStage(0.2f, "Uploading gradient texture");
            Texture2D texture = new Texture2D(dimX * dimZX, dimY * dimZY, texformat, false);
            texture.wrapMode = TextureWrapMode.Clamp;
#if false
            texture.SetPixels(cols);
            texture.Apply(false, true);
#else
            RenderTexture renderTexture = new RenderTexture(dimX * dimZX, dimY * dimZY, 0, RenderTextureFormat.ARGBHalf);
            renderTexture.enableRandomWrite = true;
            renderTexture.Create();

            var compute = Resources.Load<ComputeShader>("ComputeGradient");
            compute.SetInts("Dims", new int[] { dimX, dimY, dimZ });
            compute.SetFloat("minValue", minValue / 255.0f);
            compute.SetFloat("maxRange", maxRange / 255.0f);
            compute.SetTexture(0, "InputTex3D_as2D", dataTexture);
            compute.SetTexture(0, "OutputTex3D_as2D", renderTexture);
            compute.Dispatch(0, dimX / 8, dimY / 8, dimZ / 8);

            // from RenderTexture (GPU) to Texture2D (GPU)
            RenderTexture.active = renderTexture;
            texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            texture.Apply();
            RenderTexture.active = null;

            renderTexture.DiscardContents();
#endif
            progressHandler.EndStage();

            Debug.Log("Gradient gereneration done.");
            return texture;

        }

        public float GetAvgerageVoxelValues(int x, int y, int z)
        {
            // if a dimension length is not an even number
            bool xC = x + 1 == dimX;
            bool yC = y + 1 == dimY;
            bool zC = z + 1 == dimZ;

            //if expression can only be true on the edges of the texture
            if (xC || yC || zC)
            {
                if (!xC && yC && zC) return (GetData(x, y, z) + GetData(x + 1, y, z)) / 2.0f;
                else if (xC && !yC && zC) return (GetData(x, y, z) + GetData(x, y + 1, z)) / 2.0f;
                else if (xC && yC && !zC) return (GetData(x, y, z) + GetData(x, y, z + 1)) / 2.0f;
                else if (!xC && !yC && zC) return (GetData(x, y, z) + GetData(x + 1, y, z) + GetData(x, y + 1, z) + GetData(x + 1, y + 1, z)) / 4.0f;
                else if (!xC && yC && !zC) return (GetData(x, y, z) + GetData(x + 1, y, z) + GetData(x, y, z + 1) + GetData(x + 1, y, z + 1)) / 4.0f;
                else if (xC && !yC && !zC) return (GetData(x, y, z) + GetData(x, y + 1, z) + GetData(x, y, z + 1) + GetData(x, y + 1, z + 1)) / 4.0f;
                else return GetData(x, y, z); // if xC && yC && zC
            }
            return (GetData(x, y, z) + GetData(x + 1, y, z) + GetData(x, y + 1, z) + GetData(x + 1, y + 1, z)
                    + GetData(x, y, z + 1) + GetData(x, y + 1, z + 1) + GetData(x + 1, y, z + 1) + GetData(x + 1, y + 1, z + 1)) / 8.0f;
        }

        public float GetData(int x, int y, int z)
        {
            return data[x + y * dimX + z * (dimX * dimY)];
        }

        public void OnBeforeSerialize()
        {
            scaleX_deprecated = scale.x;
            scaleY_deprecated = scale.y;
            scaleZ_deprecated = scale.z;
        }

        public void OnAfterDeserialize()
        {
            scale = new Vector3(scaleX_deprecated, scaleY_deprecated, scaleZ_deprecated);
        }
    }
}
