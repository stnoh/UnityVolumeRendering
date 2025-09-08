using UnityEngine;

using UnityVolumeRendering;

public class LoadingBehaviour : MonoBehaviour
{
    #region PUBLIC_MEMBERS
        
    public string filePath = "./DataFiles/VisMale.raw";

    #endregion // PUBLIC_MEMBERS



    #region MONO_BEHAVIOUR

    void Start()
    {
        OnOpenRAWDatasetResult(filePath);
    }

    void Update()
    {

    }

    #endregion // MONO_BEHAVIOUR



    #region SUBROUTINES

    private async void OnOpenRAWDatasetResult(string filePath)
    {
        Debug.Log("Async dataset load. Hold on.");

        // Did the user try to import an .ini-file? Open the corresponding .raw file instead
        if (System.IO.Path.GetExtension(filePath) == ".ini")
            filePath = filePath.Substring(0, filePath.Length - 4);

        // Parse .ini file
        DatasetIniData initData = DatasetIniReader.ParseIniFile(filePath + ".ini");
        if (initData != null)
        {
            // Import the dataset
            RawDatasetImporter importer = new RawDatasetImporter(filePath, initData.dimX, initData.dimY, initData.dimZ, initData.format, initData.endianness, initData.bytesToSkip);
            VolumeDataset dataset = await importer.ImportAsync();

            // Spawn the object
            if (dataset != null)
            {
                GameObject outerObject = new GameObject("VolumeRenderedObject_" + dataset.datasetName);
                VolumeRenderedObject volObj = outerObject.AddComponent<VolumeRenderedObject>();

                GameObject meshContainer = GameObject.Instantiate((GameObject)Resources.Load("VolumeContainer"));
                volObj.volumeContainerObject = meshContainer;
                MeshRenderer meshRenderer = meshContainer.GetComponent<MeshRenderer>();

                CreateObjectInternal(dataset, meshContainer, meshRenderer, volObj, outerObject);

                meshRenderer.sharedMaterial.SetTexture("_DataTex", dataset.GetDataTexture());
            }
        }
    }

    private static void CreateObjectInternal(VolumeDataset dataset, GameObject meshContainer, MeshRenderer meshRenderer, VolumeRenderedObject volObj, GameObject outerObject, IProgressHandler progressHandler = null)
    {
        meshContainer.transform.parent = outerObject.transform;
        meshContainer.transform.localScale = Vector3.one;
        meshContainer.transform.localPosition = Vector3.zero;
        meshContainer.transform.parent = outerObject.transform;
        outerObject.transform.localRotation = Quaternion.Euler(0.0f, 0.0f, 0.0f);

        meshRenderer.sharedMaterial = new Material(meshRenderer.sharedMaterial);
        volObj.meshRenderer = meshRenderer;
        volObj.dataset = dataset;

        TransferFunction tf;
        if (false) {
            volObj.SetRenderMode(UnityVolumeRendering.RenderMode.DirectVolumeRendering);
            tf = TransferFunctionDatabase.CreateTransferFunction();
        }
        else {
            volObj.SetRenderMode(UnityVolumeRendering.RenderMode.IsosurfaceRendering);

            tf = ScriptableObject.CreateInstance<TransferFunction>();
            tf.AddControlPoint(new TFColourControlPoint(0.0f, Color.gray));
            tf.AddControlPoint(new TFColourControlPoint(0.0f, Color.gray));
            tf.AddControlPoint(new TFAlphaControlPoint(0.0f, 0.0f));
            tf.AddControlPoint(new TFAlphaControlPoint(1.0f, 1.0f));
        }

        Texture2D tfTexture = tf.GetTexture();
        volObj.transferFunction = tf;

        volObj.SetVisibilityWindow(0.2f, 1.0f);
        volObj.SetLightSource(LightSource.SceneMainLight);

        meshRenderer.sharedMaterial.SetTexture("_GradientTex", null);

        meshRenderer.sharedMaterial.DisableKeyword("MODE_DVR");
        meshRenderer.sharedMaterial.DisableKeyword("MODE_MIP");
        meshRenderer.sharedMaterial.DisableKeyword("MODE_SURF");

        meshContainer.transform.localScale = dataset.scale;
        meshContainer.transform.localRotation = dataset.rotation;

        if (PlayerPrefs.GetInt("NormaliseScaleOnImport") > 0)
            volObj.NormaliseScale();
    }

    #endregion // SUBROUTINES
}
