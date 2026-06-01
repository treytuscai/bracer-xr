using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Loads experiment scenes by name. Attach to menu buttons or call from UI code.
/// </summary>
public class ExperimentSceneLoader : MonoBehaviour
{
    static bool _isLoading;

    public void LoadMainScene()
    {
        LoadScene(ExperimentScenes.Main);
    }

    public void LoadExperiment1A()
    {
        LoadScene(ExperimentScenes.Experiment1A);
    }

    public void LoadExperiment2()
    {
        LoadScene(ExperimentScenes.Experiment2);
    }

    public void LoadSceneByName(string sceneName)
    {
        LoadScene(sceneName);
    }

    public static void LoadScene(string sceneName)
    {
        if (_isLoading || string.IsNullOrWhiteSpace(sceneName))
            return;

        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogError(
                $"[ExperimentSceneLoader] Scene '{sceneName}' is not in Build Settings or the name is wrong.");
            return;
        }

        _isLoading = true;
        SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
    }
}
